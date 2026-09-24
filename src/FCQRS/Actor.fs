module  rec FCQRS.Actor

open Akka.Streams
open Akka.Cluster
open Akka.Cluster.Tools.PublishSubscribe
open Akkling
open Microsoft.Extensions.Configuration
open Hocon.Extensions.Configuration
open DynamicConfig
open System.Dynamic
open Akkling.Persistence
open Akka
open Common
open AkklingHelpers
open System
open Microsoft.Extensions.Logging
open AkkaTimeProvider
open FCQRS.Model.Data
open Akkling.Cluster.Sharding
open System.Diagnostics

// ActivitySource for distributed tracing
let private activitySource = new ActivitySource(Common.Telemetry.ActivitySourceName)


[<AutoOpen>]
module internal Internal =

    type State<'InnerState> =
        { Version: Version
          State: 'InnerState }
        interface ISerializable

    type BodyInput<'TEvent when 'TEvent : not null> =
        { 
        Message: obj
        State: obj
        PublishEvent: Event<'TEvent> -> unit
        /// Starts the sagas these events begin, then applies the persist effect.
        StartSagasThenPersist: Event<'TEvent> list -> Effect<obj> -> Effect<obj>
        Mediator: IActorRef<Publish>
        Log: ILogger
        /// Set by handleEffect when the in-flight persist should be followed by
        /// an immediate snapshot (PersistAndSnapshot); cleared after saving.
        ManualSnapshotRequested: bool ref }
    /// The self messages of one saga-start handshake, each carrying the handshake's id so a
    /// signal left over from an earlier handshake is recognised and ignored.
    type SagaStartSignal =
        /// The application wired the saga start rules.
        | RulesWired of Guid
        /// Send the starting message again to the sagas that have not answered.
        | ResendStart of Guid
        /// The handshake ran out of time.
        | StartDeadline of Guid
        /// Every saga is ready. Put at the front of the mailbox with the command's sender,
        /// so the events are stored while that sender is current.
        | StartComplete of Guid
        // Sent only to the aggregate itself, so it never crosses nodes.
        interface Akka.Actor.INoSerializationVerificationNeeded

    let private actorOfCell =
        typeof<Akka.Actor.ActorCell>.GetProperty("Actor", Reflection.BindingFlags.Instance ||| Reflection.BindingFlags.Public ||| Reflection.BindingFlags.NonPublic)

    /// The actor's own stash. Akkling exposes Stash, Unstash, and UnstashAll; the saga-start
    /// handshake also needs ClearStash and Prepend, so it reads the IStash from the actor.
    let stashOf (mailbox: Eventsourced<obj>) : Akka.Actor.IStash =
        match actorOfCell with
        | null -> invalidOp "FCQRS cannot reach the actor's stash: ActorCell.Actor is missing from this Akka.NET version."
        | property ->
            match mailbox.UntypedContext with
            | :? Akka.Actor.ActorCell as cell ->
                match property.GetValue cell with
                | :? Akka.Actor.IActorStash as actor -> actor.Stash
                | other -> invalidOp $"FCQRS cannot reach the stash of {other}."
            | other -> invalidOp $"FCQRS cannot reach the stash of an actor with context {other}."

    /// Flags an aborted saga recovery in the trace, so aborted flows are findable without tag
    /// filters. An instantaneous Error span.
    let markRestartDetected (e: Event<'TEvent>) (currentVersion: int64) (actorName: string) =
        if activitySource.HasListeners() then
            let eventCid = e.CorrelationId |> ValueLens.Value |> ValueLens.Value
            let eventCase = caseNameOf (box e.EventDetails)

            let act =
                match tryTraceContext e.Metadata eventCid with
                | Some parent -> activitySource.StartActivity($"Abort:{eventCase}", ActivityKind.Internal, parent)
                | None -> activitySource.StartActivity($"Abort:{eventCase}", ActivityKind.Internal)

            match act with
            | null -> ()
            | act ->
                act.SetTag("cid", eventCid) |> ignore
                act.SetTag("actor", actorName) |> ignore
                act.SetTag("event.type", payloadTag (box e.EventDetails)) |> ignore
                act.SetTag("version.current", currentVersion) |> ignore
                act.SetTag("version.event", e.Version |> ValueLens.Value) |> ignore

                act.SetStatus(ActivityStatusCode.Error, "Restart detected: the originator did not store the saga's starting event")
                |> ignore

                act.Dispose()

    /// Reports whether the event stored at a saga's starting-event version is that event. The
    /// originator has stored later events, so its state no longer shows this; its journal does.
    /// Reads with the replay request recovery uses, through the aggregate's own journal plugin.
    /// Each attempt has its own reader, so a late reply cannot answer a newer attempt. Attempts
    /// repeat while the journal cannot answer and stop when the actor system terminates.
    let answerFromJournal
        (system: Akka.Actor.ActorSystem)
        (logger: ILogger)
        (persistenceId: string)
        (startingEvent: Event<'TEvent>)
        (answer: bool -> unit)
        =
        let journal = Akka.Persistence.Persistence.Instance.Apply(system).JournalFor(null)
        let sequenceNr = startingEvent.Version |> ValueLens.Value

        let readStored () =
            let stored = Threading.Tasks.TaskCompletionSource<obj option>(Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)

            let reader =
                spawnAnonymous system (props (fun (reader: Actor<obj>) ->
                    journal.Tell(
                        Akka.Persistence.ReplayMessages(sequenceNr, sequenceNr, 1L, persistenceId, untyped reader.Self),
                        untyped reader.Self)

                    let rec receive (payload: obj option) =
                        actor {
                            let! message = reader.Receive()

                            match message with
                            | :? Akka.Persistence.ReplayedMessage as replayed -> return! receive (Some replayed.Persistent.Payload)
                            | :? Akka.Persistence.RecoverySuccess ->
                                stored.TrySetResult payload |> ignore
                                return! Stop
                            | :? Akka.Persistence.ReplayMessagesFailure as failure ->
                                stored.TrySetException failure.Cause |> ignore
                                return! Stop
                            | _ -> return! receive payload
                        }

                    receive None))

            task {
                try
                    return! stored.Task.WaitAsync(TimeSpan.FromSeconds 30.0)
                finally
                    // A reader that answered has stopped already; one that timed out must not linger.
                    (untyped reader).Tell(Akka.Actor.PoisonPill.Instance, Akka.Actor.ActorRefs.NoSender)
            }

        let isStartingEvent (payload: obj option) =
            match payload with
            | None -> false
            | Some payload ->
                let event =
                    try
                        EventUpcasting.Internal.upcastEvent system payload
                    with error ->
                        logger.LogError(error, "Fatal error upcasting aggregate history for {PersistenceId}.", persistenceId)
                        fatalFailFast null "Process terminated due to aggregate event-upcast error" error
                        failwith "unreachable"

                match event with
                | :? Event<'TEvent> as event -> event.Id = startingEvent.Id && event.Version = startingEvent.Version
                | _ -> false

        task {
            let mutable attempt = 0
            let mutable answered = false

            while not answered && not system.WhenTerminated.IsCompleted do
                let! outcome =
                    task {
                        try
                            let! payload = readStored ()
                            return Choice1Of2 payload
                        with error ->
                            return Choice2Of2 error
                    }

                match outcome with
                | Choice1Of2 payload ->
                    answer (isStartingEvent payload)
                    answered <- true
                | Choice2Of2 error ->
                    let delay = TimeSpan.FromSeconds(min 30.0 (2.0 ** float attempt))

                    logger.LogWarning(
                        error,
                        "Could not read {PersistenceId} at sequence {SequenceNr} to answer a saga's recovery check; retrying in {Delay}.",
                        persistenceId,
                        sequenceNr,
                        delay)

                    attempt <- attempt + 1
                    do! Threading.Tasks.Task.Delay delay
        }
        |> ignore

    let runActor<'TEvent , 'TState when 'TEvent : not null>
        (snapshotEvery: int64 option)
        (manualSnapshotRequested: bool ref)
        (journaledStateRef: 'TState ref)
        (lastJournaledIdRef: FCQRS.Model.Data.MessageId option ref)
        (sagaStartTimeout: TimeSpan)
        (logger: ILogger)
        (flowLogger: ILogger)
        (mailbox: Eventsourced<obj>)
        mediator
        (set: State<'TState> -> _)
        (state: State<'TState>)
        (applyNewState: Event<'TEvent> -> 'TState -> 'TState)
        (body: BodyInput<'TEvent> -> _)
        : Effect<obj> =

        // Aggregates are eternal: a fold that throws means the journal and the
        // code no longer agree, and an Akka actor restart would just drop the
        // message and hide it. Same policy as sagas — kill the process.
        let applyChecked (event: Event<'TEvent>) (st: 'TState) : 'TState =
            try
                applyNewState event st
            with ex ->
                logger.LogError(ex, "Fatal error applying event in aggregate {0}. Terminating process to prevent silent state divergence.", mailbox.Self.Path.ToString())
                fatalFailFast null "Process terminated due to aggregate apply error" ex
                failwith "unreachable" // FailFast never returns; satisfies the compiler

        // The delivery stamp: the CID-correlated caller (and sagas) can tell a
        // journaled ack from a deferred/publish-only one — read-your-writes
        // needs this to know whether a projection event will ever follow.
        // Stamped ONLY on the outbound copy; the journal record stays clean.
        let stamp journaled (event: Event<'TEvent>) =
            { event with
                Metadata =
                    event.Metadata
                    |> Map.add Common.JournaledMetadataKey (if journaled then "true" else "false") }

        // The saga-start handshake. An event that starts a saga is stored only after that saga
        // has stored its start and subscribed to the event's topic, so the published event
        // cannot pass it by. The aggregate waits for the sagas' readiness as messages: commands
        // that arrive meanwhile are stashed, and no thread is held.
        let startSagasThenPersist (events: Event<'TEvent> list) (persist: Effect<obj>) : Effect<obj> =
            let rulesTask = SagaStarter.Internal.startRulesOf mailbox.System
            let originatorId = mailbox.Self.Path.Name |> SagaStarter.Internal.entityIdOf

            let sagasFor (rules: SagaStarter.Internal.StartRules) =
                [ for event in events do
                      let cid =
                          SagaStarter.Internal.toCidWithExisting
                              originatorId
                              (event.CorrelationId |> ValueLens.Value |> ValueLens.Value)

                      let matched =
                          try
                              rules (event |> box |> Unchecked.nonNull)
                          with error ->
                              logger.LogError(error, "A saga start rule threw for an event of aggregate {Aggregate}.", mailbox.Self.Path.ToString())
                              fatalFailFast null "Process terminated because a saga start rule threw" error
                              failwith "unreachable" // FailFast never returns; satisfies the compiler

                      for factory, prefix, payload in matched do
                          match SagaStarter.Internal.sagaIdFor logger originatorId cid prefix with
                          | Some sagaId ->
                              let saga = factory sagaId
                              yield (saga.TypeName, saga.EntityId), (saga, payload)
                          | None -> () ]
                |> Map.ofList

            let wait (sagas: Map<string * string, IEntityRef<obj> * obj> option) =
                let handshake = Guid.NewGuid()
                let self = untyped mailbox.Self
                let originalSender = untyped (mailbox.Sender())
                let stash = stashOf mailbox
                let scheduler = mailbox.System.Scheduler
                // Commands a user stashed stay stashed: set them aside while this handshake
                // stashes what arrives, and put them back afterwards.
                let userStash = stash.ClearStash() |> List.ofSeq
                let deadline =
                    Akka.Actor.SchedulerExtensions.ScheduleTellOnceCancelable(scheduler, sagaStartTimeout, self, StartDeadline handshake, self)
                // Only a saga that restarted during the handshake needs the starting message
                // again, so repeat it rarely: a sixth of the deadline, at least a second.
                let resendEvery = max (TimeSpan.FromSeconds 1.0) (TimeSpan.FromTicks(sagaStartTimeout.Ticks / 6L))
                let mutable resend: Akka.Actor.ICancelable | null = null

                let start (saga: IEntityRef<obj>, payload) = saga <! SagaStarter.Internal.unboxx payload

                let scheduleResend () =
                    resend <- Akka.Actor.SchedulerExtensions.ScheduleTellOnceCancelable(scheduler, resendEvery, self, ResendStart handshake, self)

                let cancelTimers () =
                    deadline.Cancel()

                    match resend with
                    | null -> ()
                    | timer -> timer.Cancel()

                let timedOut () =
                    logger.LogError("The saga-start handshake of aggregate {Aggregate} did not complete within {Timeout}. Terminating the process.", mailbox.Self.Path.ToString(), sagaStartTimeout)
                    fatalFailFast
                        null
                        $"FCQRS saga-start handshake did not complete within {sagaStartTimeout} for originator '{mailbox.Self.Path.Name}'. Crashing per fail-fast policy."
                        (TimeoutException "The sagas this event starts did not report ready in time.")

                // Put the complete signal first, with the command's sender, then what arrived
                // during the wait, and restore the user's stash behind them.
                let release () =
                    stash.Prepend [ Akka.Actor.Envelope(StartComplete handshake, originalSender) ]
                    stash.UnstashAll()
                    stash.Prepend userStash

                let rec awaitingRules () =
                    actor {
                        let! msg = mailbox.Receive()

                        match msg with
                        | :? SagaStartSignal as signal ->
                            match signal with
                            | RulesWired id when id = handshake ->
                                let sagas = sagasFor rulesTask.Task.Result

                                if sagas.IsEmpty then
                                    release ()
                                    return! completing ()
                                else
                                    sagas |> Map.iter (fun _ start' -> start start')
                                    scheduleResend ()
                                    return! awaitingSagas sagas
                            | StartDeadline id when id = handshake ->
                                timedOut ()
                                return! awaitingRules ()
                            | _ -> return! awaitingRules ()
                        | _ ->
                            mailbox.Stash()
                            return! awaitingRules ()
                    }

                and awaitingSagas (pending: Map<string * string, IEntityRef<obj> * obj>) =
                    actor {
                        let! msg = mailbox.Receive()

                        match msg with
                        | :? SagaStarter.Internal.Message ->
                            // Sharded entity paths end in <type>/<shard>/<entity>, and cluster
                            // sharding escapes both names.
                            let saga = untyped (mailbox.Sender())

                            let identity =
                                saga.Path.Parent.Parent.Name |> SagaStarter.Internal.entityIdOf,
                                saga.Path.Name |> SagaStarter.Internal.entityIdOf

                            let pending = pending.Remove identity

                            if pending.IsEmpty then
                                release ()
                                return! completing ()
                            else
                                return! awaitingSagas pending
                        | :? SagaStartSignal as signal ->
                            match signal with
                            | ResendStart id when id = handshake ->
                                // A saga that restarted during the handshake lost the reference
                                // it answers to. A repeated starting message reaches it again,
                                // and a saga that already stored its start only answers.
                                pending |> Map.iter (fun _ start' -> start start')
                                scheduleResend ()
                                return! awaitingSagas pending
                            | StartDeadline id when id = handshake ->
                                timedOut ()
                                return! awaitingSagas pending
                            | _ -> return! awaitingSagas pending
                        | _ ->
                            mailbox.Stash()
                            return! awaitingSagas pending
                    }

                and completing () =
                    actor {
                        let! msg = mailbox.Receive()

                        match msg with
                        | :? SagaStartSignal as signal when signal = StartComplete handshake ->
                            cancelTimers ()
                            return! persist <@> set state
                        | _ ->
                            // Unreachable: release put the complete signal at the front of the
                            // mailbox. Keep the message rather than lose it.
                            mailbox.Stash()
                            return! completing ()
                    }

                match sagas with
                | Some sagas ->
                    sagas |> Map.iter (fun _ start' -> start start')
                    scheduleResend ()
                    awaitingSagas sagas
                | None ->
                    rulesTask.Task.ContinueWith(fun (_: Threading.Tasks.Task<_>) -> self.Tell(RulesWired handshake, self))
                    |> ignore

                    awaitingRules ()

            if rulesTask.Task.IsCompletedSuccessfully then
                let sagas = sagasFor rulesTask.Task.Result
                // Most events start no saga and are stored at once.
                if sagas.IsEmpty then persist else wait (Some sagas)
            else
                wait None

        let publishEvent journaled (event: Event<'TEvent>) =
            let stamped = stamp journaled event

            SagaStarter.Internal.publishEvent
                logger
                mailbox
                mediator
                stamped
                (stamped.CorrelationId |> ValueLens.Value |> ValueLens.Value)

        actor {
            let! msg = mailbox.Receive()

            let msg =
                if mailbox.IsRecovering() then
                    try
                        let converted = EventUpcasting.Internal.upcastEvent mailbox.System msg
                        let originalType = msg.GetType()
                        if originalType.IsGenericType
                           && originalType.GetGenericTypeDefinition() = typedefof<Common.Event<_>>
                           && not (converted :? Common.Event<'TEvent>) then
                            invalidOp $"Aggregate '{mailbox.Self.Path.Name}' recovered {converted.GetType().FullName}, but its fold requires Event<{typeof<'TEvent>.FullName}>. Register a complete event-upcast chain before starting the aggregate."
                        converted
                    with error ->
                        logger.LogError(error, "Fatal error upcasting aggregate history for {Aggregate}.", mailbox.Self.Path.ToString())
                        fatalFailFast null "Process terminated due to aggregate event-upcast error" error
                        failwith "unreachable"
                else
                    msg

            let eventName (event: obj | null) =
                match event with
                | null -> "null"
                | event -> event.GetType().Name

            match msg with
            | PersistentLifecycleEvent(PersistFailed(error, event, sequenceNr)) ->
                // Akka stops the entity after this callback; its next command recovers it from the journal.
                logger.LogError(
                    error,
                    "Aggregate {Aggregate} could not persist {Event} at sequence {SequenceNr}; the entity stops.",
                    mailbox.Self.Path.ToString(), eventName event, sequenceNr)
                return! state |> set
            | PersistentLifecycleEvent(PersistRejected(error, event, sequenceNr)) ->
                // The journal refused the event. Akka keeps the entity running with this sequence
                // number used, so its next event would leave a journal gap that stops transactional
                // projections. Stopping the entity would drop the commands queued behind the write.
                // Crash instead, as a serialization error does.
                logger.LogError(
                    error,
                    "The journal rejected {Event} at sequence {SequenceNr} for aggregate {Aggregate}. Terminating the process.",
                    eventName event, sequenceNr, mailbox.Self.Path.ToString())
                fatalFailFast null "Process terminated because the journal rejected an event" error
                return! state |> set
            | PersistentLifecycleEvent(ReplayFailed(error, _)) ->
                // Akka stops the entity after this callback.
                logger.LogError(error, "Aggregate {Aggregate} could not recover from the journal; the entity stops.", mailbox.Self.Path.ToString())
                return! state |> set
            | PersistentLifecycleEvent _
            | :? Persistence.SaveSnapshotSuccess
            | LifecycleEvent _ -> return! state |> set

            // Passivation or shard hand-off. As an ordinary message it waits for any save
            // in flight, unlike PoisonPill.
            | :? FCQRS.Common.StopEntity -> return! Stop

            | SnapshotOffer(snapState: obj) ->
                let snap = snapState |> unbox<State<'TState>>
                // The snapshot holds journal-only state by construction; resume
                // the mirror from it before replay continues.
                journaledStateRef.Value <- snap.State
                // The snapshot does not record which event holds its version.
                lastJournaledIdRef.Value <- None
                return! snap |> set
            // A saga recovered before it leaves Started asks whether this aggregate stored its
            // starting event: the saga-start handshake runs before the write, and the write can fail.
            | :? Command<ContinueOrAbort<'TEvent>> as (cmd) ->
                let (ContinueOrAbort(e: Event<'TEvent>)) = cmd.CommandDetails
                let currentVersion = state.Version |> ValueLens.Value
                let eventVersion = e.Version |> ValueLens.Value
                let saga = untyped (mailbox.Sender())
                let self = untyped mailbox.Self
                let actorName = mailbox.Self.Path.Name

                let abortedEvent =
                    { EventDetails = AbortedEvent
                      CreationDate = mailbox.System.Scheduler.Now.UtcDateTime
                      Id = Guid.CreateVersion7().ToString() |> ValueLens.CreateAsResult |> Result.value
                      Sender =
                        actorName
                        |> SagaStarter.Internal.entityIdOf
                        |> ValueLens.CreateAsResult
                        |> Result.value
                        |> Some
                      CorrelationId = e.CorrelationId
                      Version = state.Version
                      Metadata = e.Metadata }

                // Only the saga that asked receives the answer. Publishing the starting event again
                // would reach every subscriber of its correlation ID, and a pending send that reuses
                // the ID could take the old event as its reply. Other sagas sharing the ID must not
                // be passivated by this saga's abort either.
                let answer stored =
                    if stored then
                        saga.Tell(stamp true e :> obj, self)
                    else
                        markRestartDetected e currentVersion actorName
                        saga.Tell(abortedEvent :> obj, self)

                if not (saga.Path.Name |> SagaStarter.Internal.entityIdOf |> SagaStarter.Internal.isSaga) then
                    logger.LogWarning(
                        "ContinueOrAbort arrived from non-saga sender {sender}; ignoring it (contract violation)",
                        saga.Path.Name)
                elif currentVersion = eventVersion then
                    // After a failed save, a different event can hold the same version. After
                    // recovery from a snapshot with no later events the identity is unknown, so only
                    // the version is compared.
                    let sameEvent =
                        match lastJournaledIdRef.Value with
                        | Some id -> id = e.Id
                        | None -> true

                    answer sameEvent
                elif currentVersion < eventVersion then
                    // This check runs after any write in progress, so the event was never stored.
                    answer false
                else
                    // Later events were stored after the starting event, and this aggregate's state
                    // does not show whether the starting event is among them. Its journal does.
                    answerFromJournal mailbox.System logger mailbox.Pid e answer

                return! state |> set

            // actor level events will come here
            | Deferred mailbox (:? Common.Event<'TEvent> as event) ->
                if messageFlowEnabled flowLogger then
                    flowLogger.LogInformation(
                        "Aggregate {Aggregate} applied deferred event {Event} (not persisted) [cid: {CID}]",
                        mailbox.Self.Path.Name,
                        logPayload event,
                        event.CorrelationId |> ValueLens.Value |> ValueLens.Value)

                let state = applyChecked event (state.State)
                publishEvent false event

                let newState =
                    {   Version = event.Version
                        State = state }

                return! newState |> set

            | Persisted mailbox (:? Common.Event<'TEvent> as event) ->
                let versionN = event.Version |> ValueLens.Value

                if messageFlowEnabled flowLogger then
                    flowLogger.LogInformation(
                        "Aggregate {Aggregate} persisted event {Event} (v{Version}) [cid: {CID}]",
                        mailbox.Self.Path.Name,
                        logPayload event,
                        versionN,
                        event.CorrelationId |> ValueLens.Value |> ValueLens.Value)

                let activity =
                    if activitySource.HasListeners() then
                        let eventCid = event.CorrelationId |> ValueLens.Value |> ValueLens.Value

                        let eventCase = caseNameOf (box event.EventDetails)

                        let act =
                            match tryTraceContext event.Metadata eventCid with
                            | Some parent ->
                                activitySource.StartActivity($"Event:{eventCase}", ActivityKind.Internal, parent)
                            | None -> activitySource.StartActivity($"Event:{eventCase}", ActivityKind.Internal)

                        match act with
                        | null -> ()
                        | act ->
                            act.SetTag("cid", eventCid) |> ignore
                            act.SetTag("actor", mailbox.Self.Path.Name) |> ignore
                            act.SetTag("event.type", payloadTag (box event.EventDetails)) |> ignore
                            act.SetTag("version", versionN) |> ignore

                        act
                    else
                        null

                let innerState = applyChecked event state.State
                // The mirror folds only journaled events, and snapshots store it —
                // a DeferEvent fold (never journaled) must not leak into a snapshot
                // and reappear on recovery.
                let journaledState = applyChecked event journaledStateRef.Value
                journaledStateRef.Value <- journaledState
                lastJournaledIdRef.Value <- Some event.Id
                publishEvent true event

                match activity with
                | null -> ()
                | act -> act.Dispose()

                let newState =
                    {   Version = event.Version
                        State = innerState }

                let state = newState

                let manual = manualSnapshotRequested.Value
                manualSnapshotRequested.Value <- false

                let dueByCadence =
                    match snapshotEvery with
                    | Some every -> versionN > 0L && versionN % every = 0L
                    | None -> false

                if manual || dueByCadence then
                    return! state |> set <@> SaveSnapshot { state with State = journaledState }
                else
                    return! state |> set

            | Recovering mailbox (:? Common.Event<'TEvent> as event) ->
                let state = applyChecked event state.State
                // Replay is journal-only by construction: keep the mirror in step.
                journaledStateRef.Value <- applyChecked event journaledStateRef.Value
                lastJournaledIdRef.Value <- Some event.Id

                let newState =
                    {   Version = event.Version
                        State = state }

                return! newState |> set
            // A readiness message or timer of a handshake that already finished.
            | :? SagaStartSignal
            | :? SagaStarter.Internal.Message -> return! state |> set
            | _ ->
                let bodyInput =
                    {   Message = msg
                        State = state
                        PublishEvent = publishEvent false
                        StartSagasThenPersist = startSagasThenPersist
                        Mediator = mediator
                        Log = logger
                        ManualSnapshotRequested = manualSnapshotRequested }

                return! body bodyInput
        }

    let rec handleEffect effect state  (mailbox: Eventsourced<obj>) toEvent nextVersion bodyInput runEffect set = actor {
         match effect with
            | PersistEvent event ->
                let nextVersion: Version =
                    (state.Version |> ValueLens.Value) + 1L |> ValueLens.TryCreate |> Result.value
                let stored = event |> toEvent nextVersion
                return! bodyInput.StartSagasThenPersist [ stored ] (stored |> box |> Unchecked.nonNull |> Persist :> Effect<obj>)

            | PersistAndSnapshot event ->
                let nextVersion: Version =
                    (state.Version |> ValueLens.Value) + 1L |> ValueLens.TryCreate |> Result.value
                // flag consumed by the Persisted branch after the event is durable
                bodyInput.ManualSnapshotRequested.Value <- true
                let stored = event |> toEvent nextVersion
                return! bodyInput.StartSagasThenPersist [ stored ] (stored |> box |> Unchecked.nonNull |> Persist :> Effect<obj>)

            | PersistAllEvents [] -> return set state
            | PersistAllEvents events ->
                // One journal AtomicWrite: versions are pre-allocated sequentially,
                // one saga-start handshake covers every event of the batch (any of them
                // may start a saga), then the batch persists all-or-nothing. The Persisted
                // callback fires per event after the WHOLE batch is durable, so
                // folds/publishes/awaiters never observe a torn batch.
                let baseVersion = state.Version |> ValueLens.Value

                let stored =
                    events
                    |> List.mapi (fun i event ->
                        let version: Version =
                            baseVersion + int64 i + 1L |> ValueLens.TryCreate |> Result.value

                        event |> toEvent version)

                let persistAll =
                    stored |> List.map (fun event -> event |> box |> Unchecked.nonNull) |> Seq.ofList |> PersistAll :> Effect<obj>

                return! bodyInput.StartSagasThenPersist stored persistAll

            | DeferEvent event ->
                // A deferred event is not journaled, so it does not start sagas. Saga recovery
                // checks the originator's journaled version, and a repeated verdict answered with
                // a deferred event would otherwise start a duplicate workflow.
                return! seq { event |> toEvent state.Version |> box |> Unchecked.nonNull } |> Defer
            | PublishEvent event ->
                event |> bodyInput.PublishEvent |> ignore
                return set state
            | IgnoreEvent -> return set state
            | StateChangedEvent _
            | UnhandledEvent -> return Unhandled
            | Stash effect ->
                mailbox.Stash()
                return! handleEffect effect state mailbox toEvent nextVersion bodyInput runEffect set
            | Unstash effect ->
                mailbox.Unstash()
                return! handleEffect effect state mailbox toEvent nextVersion bodyInput runEffect set
            | UnstashAll effect ->
                mailbox.UnstashAll()
                return! handleEffect effect state mailbox toEvent nextVersion bodyInput runEffect set

            | RunAsync description ->
                // A "mini saga": the registered runner turns the description
                // into a command OFF the mailbox (the actor keeps processing
                // other commands); runEffect then self-dispatches it, re-entering
                // decide re-validated against current state. See RunAsync's doc
                // for the ephemeral / totality contract. runEffect is built per
                // command (it knows the originating CID and command type).
                runEffect description
                return set state
        }

    /// A C# 15 union shares no base type with its cases, so a case sent as its own type, for
    /// example through the object-typed saga command helpers, arrives as Command<Case> and no
    /// aggregate of Command<Union> would handle it. For a union command type, these rebuild such
    /// a message as Command<Union>. Anything else passes through unchanged.
    type internal UnionCommands<'Command> private () =
        static let unionType = typeof<'Command>

        // The union's case constructors, keyed by case type. The C# compiler marks a union with
        // this attribute and gives it one single-argument constructor per case.
        static let cases: (Type * Reflection.ConstructorInfo) array =
            if unionType.GetCustomAttributes(false)
               |> Array.exists (fun a -> a.GetType().FullName = "System.Runtime.CompilerServices.UnionAttribute") then
                unionType.GetConstructors(
                    Reflection.BindingFlags.Public ||| Reflection.BindingFlags.NonPublic ||| Reflection.BindingFlags.Instance)
                |> Array.choose (fun ctor ->
                    match ctor.GetParameters() with
                    | [| parameter |] when parameter.ParameterType <> unionType -> Some(parameter.ParameterType, ctor)
                    | _ -> None)
            else
                [||]

        static let caseFor = Collections.Concurrent.ConcurrentDictionary<Type, Reflection.ConstructorInfo option>()

        // An exact case type first, then a case its payload derives from.
        static let find (payloadType: Type) =
            caseFor.GetOrAdd(
                payloadType,
                fun payloadType ->
                    match cases |> Array.tryFind (fun (caseType, _) -> caseType = payloadType) with
                    | Some(_, ctor) -> Some ctor
                    | None ->
                        cases
                        |> Array.tryFind (fun (caseType, _) -> caseType.IsAssignableFrom payloadType)
                        |> Option.map snd)

        static let commandDetailsIndex =
            FSharp.Reflection.FSharpType.GetRecordFields typeof<Command<'Command>>
            |> Array.findIndex (fun field -> field.Name = "CommandDetails")

        /// The payload as the union when it is one of the union's cases; otherwise unchanged.
        static member Payload(payload: obj) : obj =
            if cases.Length = 0 || payload.GetType() = unionType then
                payload
            else
                match find (payload.GetType()) with
                | Some ctor -> ctor.Invoke [| payload |]
                | None -> payload

        /// The message as Command<Union> when it is a Command<Case>; otherwise unchanged.
        static member Message(message: obj) : obj =
            if cases.Length = 0 then
                message
            else
                let messageType = message.GetType()

                if messageType.IsGenericType
                   && messageType.GetGenericTypeDefinition() = typedefof<Command<_>>
                   && messageType.GetGenericArguments().[0] <> unionType then
                    match find (messageType.GetGenericArguments().[0]) with
                    | Some ctor ->
                        let fields = FSharp.Reflection.FSharpValue.GetRecordFields message
                        fields.[commandDetailsIndex] <- ctor.Invoke [| fields.[commandDetailsIndex] |]
                        FSharp.Reflection.FSharpValue.MakeRecord(typeof<Command<'Command>>, fields)
                    | None -> message
                else
                    message

    let actorProp
        (config: IConfiguration)
        (loggerFactory: ILoggerFactory)
        handleCommand
        apply
        (initialState: 'State)
        (name: string)
        toEvent
        (snapshotPolicy: SnapshotPolicy)
        (effectRunner: (obj -> Async<obj>) option)
        (mediator: IActorRef<Publish>)
        (mailbox: Eventsourced<obj>)
        =
        let logger = loggerFactory.CreateLogger name
        let flowLogger = loggerFactory.CreateLogger Telemetry.MessageFlowCategory

        // Per-entity policy first; Default falls back to the global config key, then 30.
        let snapshotEvery: int64 option =
            match snapshotPolicy with
            | Every n when n > 0 -> Some(int64 n)
            | NoSnapshots -> None
            | Default
            | Every _ ->
                let s: string | null = config["config:akka:persistence:snapshot-version-count"]

                match s |> System.Int64.TryParse with
                | true, v when v > 0L -> Some v
                | _ -> Some 30L

        // Upper bound for the saga-start handshake (seconds). Generous: the
        // handshake spans a saga journal write and normally completes in
        // milliseconds; hitting this bound means a saga cannot start or answer,
        // and the process FailFasts rather than parking the entity forever.
        let sagaStartTimeout: TimeSpan =
            let s: string | null = config["config:akka:fcqrs:saga-start-timeout"]

            match s |> System.Int32.TryParse with
            | true, v when v > 0 -> TimeSpan.FromSeconds(float v)
            | _ -> TimeSpan.FromSeconds 30.0

        // per-actor flag: a PersistAndSnapshot is in flight
        let manualSnapshotRequested = ref false

        // Journal-only mirror of the entity state: only persisted (journaled)
        // events fold into it. Snapshots must store THIS state, not the behavior
        // state — the behavior state may include DeferEvent folds, and a deferred
        // change captured by a snapshot would reappear on recovery even though a
        // deferred change is supposed to disappear on recovery.
        let journaledStateRef = ref initialState
        // The ID of the event journaled at the current version, for ContinueOrAbort.
        let lastJournaledIdRef: FCQRS.Model.Data.MessageId option ref = ref None

        let rec set (state: State<'State>) =
            let body (bodyInput: BodyInput<'Event>) =
                let condition, msg =
                    match bodyInput.Message with
                    | :? ConditionalCommand as conditional -> Some conditional, conditional.Command
                    | message -> None, message

                let msg = UnionCommands<'Command>.Message msg

                actor {
                    match msg, state with
                    | :? Persistence.RecoveryCompleted, _ -> return! state |> set
                    | :? (Common.Command<'Command>) as cmd, _
                        when condition |> Option.exists (fun c -> c.ExpectedVersion <> (state.Version |> ValueLens.Value)) ->
                        let conditional = condition.Value
                        conditional.ReplyTo.Tell(
                            { ExpectedVersion = conditional.ExpectedVersion
                              ActualVersion = state.Version |> ValueLens.Value
                              CommandId = cmd.Id
                              CorrelationId = cmd.CorrelationId
                              AggregateId = mailbox.Self.Path.Name |> SagaStarter.Internal.entityIdOf },
                            untyped mailbox.Self)
                        return! set state
                    | :? (Common.Command<'Command>) as cmd, _ ->
                        // Span only when someone is listening: the payload
                        // formatting and context parsing are not free.
                        let activity =
                            if activitySource.HasListeners() then
                                let cmdCid = cmd.CorrelationId |> ValueLens.Value |> ValueLens.Value

                                let cmdCase = caseNameOf (box cmd.CommandDetails)

                                let act =
                                    match tryTraceContext cmd.Metadata cmdCid with
                                    | Some parent ->
                                        activitySource.StartActivity($"Command:{cmdCase}", ActivityKind.Internal, parent)
                                    | None -> activitySource.StartActivity($"Command:{cmdCase}", ActivityKind.Internal)

                                match act with
                                | null -> ()
                                | act ->
                                    act.SetTag("cid", cmdCid) |> ignore
                                    act.SetTag("actor", mailbox.Self.Path.Name) |> ignore
                                    act.SetTag("command.type", payloadTag (box cmd.CommandDetails)) |> ignore
                                    act.SetTag("version", state.Version |> ValueLens.Value) |> ignore

                                act
                            else
                                null

                        // Keep original CID for pub/sub routing - trace hierarchy is via TraceId, not CID propagation
                        let toEvent =
                            toEvent
                                cmd.Id
                                cmd.CorrelationId
                                // The entity id, not the escaped path name: escaping can
                                // push a legal id past the field's length limit, and the
                                // id is what callers expect to read back.
                                (mailbox.Self.Path.Name
                                 |> SagaStarter.Internal.entityIdOf
                                 |> ValueLens.CreateAsResult
                                 |> Result.value
                                 |> Some)
                                cmd.Metadata
                        let effect =
                            try
                                handleCommand cmd state.State
                            with ex ->
                                bodyInput.Log.LogError(ex, "Fatal error in aggregate handleCommand for {0}. Terminating process to prevent silent command loss.", mailbox.Self.Path.ToString())
                                fatalFailFast null "Process terminated due to aggregate command-handler error" ex
                                failwith "unreachable" // FailFast never returns; satisfies the compiler

                        if messageFlowEnabled flowLogger then
                            flowLogger.LogInformation(
                                "Command {Command} to aggregate {Aggregate} (v{Version}) yielded {Effect} [cid: {CID}]",
                                logPayload cmd,
                                mailbox.Self.Path.Name,
                                state.Version |> ValueLens.Value,
                                payloadTag effect,
                                cmd.CorrelationId |> ValueLens.Value |> ValueLens.Value)

                        match activity with
                        | null -> ()
                        | act ->
                            act.SetTag("effect", effect.GetType().Name) |> ignore

                            // OTel convention: Unset means success; flag only the outcomes
                            // the framework itself knows are wrong. An unhandled command is
                            // the classic silent-hang scenario, so make it light up in traces.
                            match effect with
                            | UnhandledEvent ->
                                act.SetStatus(ActivityStatusCode.Error, "Command not handled by aggregate") |> ignore
                            | StateChangedEvent _ ->
                                act.SetStatus(ActivityStatusCode.Error, "StateChangedEvent is not valid for aggregates") |> ignore
                            | _ -> ()

                            act.Dispose()

                        // Built per command so it closes over the originating
                        // command (CID/metadata) and the aggregate's command
                        // type — turns a RunAsync description into a self-command.
                        let runEffect (description: obj) : unit =
                            match effectRunner with
                            | None ->
                                logger.LogError(
                                    "Aggregate {Aggregate} returned a RunAsync effect but was registered without an effect runner (use Fcqrs.aggregateWithEffects). Terminating.",
                                    name)

                                fatalFailFast
                                    null
                                    (sprintf "Aggregate '%s' used RunAsync without a registered effect runner" name)
                                    (exn "no effect runner")
                            | Some run ->
                                // Span the runner execution — the async side effect
                                // (e.g. the oracle call) runs OFF the mailbox, so
                                // without this the trace has a hole exactly where the
                                // latency lives. Low-cardinality name (case only),
                                // parented onto the originating command's trace via
                                // its traceparent metadata, disposed when the runner
                                // settles. Its domain outcome shows in the child
                                // result-command span.
                                let dispatchActivity =
                                    if activitySource.HasListeners() then
                                        let cmdCid = cmd.CorrelationId |> ValueLens.Value |> ValueLens.Value

                                        let act =
                                            match tryTraceContext cmd.Metadata cmdCid with
                                            | Some parent ->
                                                activitySource.StartActivity(
                                                    $"Dispatch:{caseNameOf description}",
                                                    ActivityKind.Internal,
                                                    parent)
                                            | None ->
                                                activitySource.StartActivity(
                                                    $"Dispatch:{caseNameOf description}",
                                                    ActivityKind.Internal)

                                        match act with
                                        | null -> ()
                                        | act ->
                                            act.SetTag("cid", cmdCid) |> ignore
                                            act.SetTag("actor", mailbox.Self.Path.Name) |> ignore
                                            act.SetTag("dispatch.type", payloadTag description) |> ignore

                                        act
                                    else
                                        null

                                let disposeActivity () =
                                    match dispatchActivity with
                                    | null -> ()
                                    | act -> act.Dispose()

                                Async.StartWithContinuations(
                                    // StartWithContinuations runs synchronously until the first
                                    // asynchronous step. Leave the aggregate's thread first, and
                                    // create the runner's work inside the async, so its synchronous
                                    // part cannot block the mailbox and its exceptions reach the
                                    // documented fail-fast continuation below.
                                    async {
                                        do! Async.SwitchToThreadPool()
                                        return! run description
                                    },
                                    (fun (boxedCommand: obj) ->
                                        disposeActivity ()
                                        // Conditional continuations retain their request ID and
                                        // recheck the original version after the async work.
                                        let selfCmd =
                                            { cmd with
                                                // A runner can return a single case of a C# union.
                                                CommandDetails = unbox (UnionCommands<'Command>.Payload boxedCommand)
                                                Id =
                                                    match condition with
                                                    | Some _ -> cmd.Id
                                                    | None ->
                                                        Guid.CreateVersion7().ToString()
                                                        |> ValueLens.CreateAsResult
                                                        |> Result.value
                                                Sender = None }

                                        match condition with
                                        | Some conditional ->
                                            mailbox.Self <! box { conditional with Command = box selfCmd |> Unchecked.nonNull }
                                        | None -> mailbox.Self <! box selfCmd),
                                    (fun (ex: exn) ->
                                        match dispatchActivity with
                                        | null -> ()
                                        | act ->
                                            act.SetStatus(ActivityStatusCode.Error, "RunAsync runner threw") |> ignore
                                            act.Dispose()

                                        logger.LogError(
                                            ex,
                                            "RunAsync runner threw for {0}; the runner must be total (map failure to a command, never an exception). Terminating.",
                                            mailbox.Self.Path.ToString())

                                        fatalFailFast null "RunAsync runner threw; totality violated" ex),
                                    (fun (_: OperationCanceledException) -> disposeActivity ()),
                                    System.Threading.CancellationToken.None)

                        return! handleEffect effect state mailbox toEvent state.Version bodyInput runEffect set
                    | _ ->
                        bodyInput.Log.LogWarning("Unhandled message: {msg}", msg)
                        return Unhandled
                }

            runActor snapshotEvery manualSnapshotRequested journaledStateRef lastJournaledIdRef sagaStartTimeout logger flowLogger mailbox mediator set state (apply: Event<_> -> 'State -> 'State) body

        let initialState =
            { 
                Version = 0L |> ValueLens.TryCreate |> Result.value
                State = initialState }

        set initialState





    let private createCommandSubscriptionCore (actorApi: IActor) factory (cid: CID) (id: AggregateId) command filter (metadata: Map<string, string> option) expectedVersion =
        let actor = factory (id |> ValueLens.Value |> ValueLens.Value)

        // Stamp the ambient trace context (if any) so spans downstream — the
        // aggregate, events, sagas, projections — parent onto the caller's trace.
        let metadataWithTrace =
            let baseMetadata = metadata |> Option.defaultValue Map.empty

            if baseMetadata.ContainsKey Telemetry.TraceparentMetadataKey then
                baseMetadata
            else
                match currentTraceparent () with
                | Some tp -> baseMetadata.Add(Telemetry.TraceparentMetadataKey, tp)
                | None -> baseMetadata

        let commonCommand: Command<_> =
            {
                CommandDetails = command
                Id = Guid.CreateVersion7().ToString() |> ValueLens.CreateAsResult |> Result.value
                CreationDate = actorApi.System.Scheduler.Now.UtcDateTime
                CorrelationId = cid
                Sender = None
                Metadata = metadataWithTrace }

        let e =
            { 
                Cmd = commonCommand
                EntityRef = actor
                Filter = filter }

        match expectedVersion with
        | Some version ->
            Common.CommandHandler.Internal.subscribeForConditionalCommand
                version actorApi.System (typed actorApi.Mediator) (Execute e)
        | None -> Execute e |> actorApi.SubscribeForCommand

    let createCommandSubscription actorApi factory cid id command filter metadata =
        createCommandSubscriptionCore actorApi factory cid id command filter metadata None

    let createConditionalCommandSubscription actorApi factory expectedVersion cid id command filter =
        async {
            if expectedVersion < 0L then
                invalidArg (nameof expectedVersion) "An expected aggregate version must be nonnegative."
            return! createCommandSubscriptionCore actorApi factory cid id command filter None (Some expectedVersion)
        }

    /// System.Text.Json writes a value through an abstract class or interface as `{}` unless the
    /// type configures polymorphism. Such an event type loses every field in the journal without
    /// an error, and the next load cannot read the rows back, so registration rejects it.
    let internal requireStorableEvents (name: string) (eventType: Type) =
        let has (attribute: Type) = eventType.IsDefined(attribute, false)

        if (eventType.IsAbstract || eventType.IsInterface)
           && not (Microsoft.FSharp.Reflection.FSharpType.IsUnion(
                   eventType, Reflection.BindingFlags.Public ||| Reflection.BindingFlags.NonPublic))
           && not (has typeof<System.Text.Json.Serialization.JsonDerivedTypeAttribute>)
           && not (has typeof<System.Text.Json.Serialization.JsonPolymorphicAttribute>)
           && not (has typeof<System.Text.Json.Serialization.JsonConverterAttribute>) then
            invalidOp (
                $"Aggregate '%s{name}' cannot store events of type '%s{eventType.FullName}': it is abstract "
                + "and has no System.Text.Json polymorphism, so every event would be stored as {}. "
                + "Declare the events as a C# union or F# union, or mark the base type with "
                + "[JsonDerivedType] for each case."
            )

    let init config loggerFactory initialState name toEvent (actorApi: IActor) (handleCommand: Command<'Command> -> 'State -> EventAction<'Event>) apply snapshotPolicy passivationPolicy effectRunner =
        requireStorableEvents name typeof<'Event>
        EventUpcasting.Internal.freeze actorApi.System
        AkklingHelpers.Internal.entityFactoryFor actorApi.System shardResolver name
        <| propsPersist (
            actorProp
                config
                loggerFactory
                handleCommand
                apply
                initialState
                name
                toEvent
                snapshotPolicy
                effectRunner
                (typed actorApi.Mediator)
        )
        <| passivationPolicy
        <| false

/// Custom configuration provider for in-memory HOCON strings
type internal HoconStringConfigurationProvider(hoconString: string) =
    inherit ConfigurationProvider()

    override this.Load() =
        use memoryStream = new IO.MemoryStream(Text.Encoding.UTF8.GetBytes hoconString)
        let hoconSource = new HoconConfigurationSource()
        let hoconProvider = new HoconConfigurationProvider(hoconSource)
        hoconProvider.Load memoryStream

        // Use reflection to get the Data property from the provider
        let providerType = hoconProvider.GetType()
        let dataProperty =
            match providerType.GetProperty("Data", Reflection.BindingFlags.Instance ||| Reflection.BindingFlags.NonPublic ||| Reflection.BindingFlags.Public) with
            | null -> failwith "Could not find Data property on HoconConfigurationProvider"
            | prop -> prop
        let providerData =
            match dataProperty.GetValue(hoconProvider) with
            | null -> failwith "HoconConfigurationProvider Data is null"
            | data -> data :?> System.Collections.Generic.IDictionary<string, string>

        // Copy data from hoconProvider to this provider
        for kvp in providerData do
            this.Data.[kvp.Key] <- kvp.Value

/// Custom configuration source for in-memory HOCON strings
type internal HoconStringConfigurationSource(hoconString: string) =
    interface IConfigurationSource with
        member _.Build(builder: IConfigurationBuilder) =
            upcast new HoconStringConfigurationProvider(hoconString)

/// Represents the type of database connection
type DBType =
    /// SQLite using Microsoft.Data.Sqlite provider
    | Sqlite
    /// Microsoft SQL Server 2012
    | SqlServer2012
    /// Microsoft SQL Server 2014
    | SqlServer2014
    /// Microsoft SQL Server 2016
    | SqlServer2016
    /// Microsoft SQL Server 2017
    | SqlServer2017
    /// Microsoft SQL Server 2019
    | SqlServer2019
    /// Microsoft SQL Server 2022
    | SqlServer2022
    /// PostgreSQL 9.3+
    | PostgreSQL
    /// PostgreSQL 15+
    | PostgreSQL15
    /// MySQL using MySqlConnector
    | MySql
    /// Oracle Database
    | Oracle
    /// Firebird
    | Firebird
    /// IBM DB2
    | DB2

/// Represents a database connection configuration.
/// The connection string is a `LongString`: provider connection strings with TLS, pooling and
/// timeout settings often exceed the 255 characters a `ShortString` allows.
type Connection =
    { ConnectionString: Model.Data.LongString
      DBType: DBType }

let api (config: IConfiguration) (loggerFactory: ILoggerFactory) (connection: Connection option) (clusterName: Model.Data.ShortString) =
    let mergedConfig =
        match connection with
        | Some conn ->
            // Read embedded hocon resource
            let assembly = Reflection.Assembly.GetExecutingAssembly()
            let resourceName = "FCQRS.default.hocon"
            use stream =
                match assembly.GetManifestResourceStream resourceName with
                | null -> failwith "Could not find embedded resource: FCQRS.default.hocon"
                | s -> s
            use reader = new IO.StreamReader(stream)
            let hoconTemplate = reader.ReadToEnd()

            // Replace placeholders with Linq2Db provider names
            let dbTypeString =
                match conn.DBType with
                | Sqlite -> "SQLite.MS"
                | SqlServer2012 -> "SqlServer.2012"
                | SqlServer2014 -> "SqlServer.2014"
                | SqlServer2016 -> "SqlServer.2016"
                | SqlServer2017 -> "SqlServer.2017"
                | SqlServer2019 -> "SqlServer.2019"
                | SqlServer2022 -> "SqlServer.2022"
                | PostgreSQL -> "PostgreSQL.9.3"
                | PostgreSQL15 -> "PostgreSQL.15"
                | MySql -> "MySqlConnector"
                | Oracle -> "Oracle.Managed"
                | Firebird -> "Firebird"
                | DB2 -> "DB2"

            let connectionStringValue = conn.ConnectionString |> ValueLens.Value

            // The value lands inside a quoted HOCON string in default.hocon, so the
            // two characters the HOCON tokenizer treats specially must be escaped —
            // otherwise legitimate connection strings (Windows paths, SqlServer
            // named instances like localhost\SQLEXPRESS, passwords containing ")
            // break config parsing at startup.
            let hoconSafeConnectionString =
                connectionStringValue.Replace("\\", "\\\\").Replace("\"", "\\\"")

            let hoconString =
                hoconTemplate
                    .Replace("${connection-string}", hoconSafeConnectionString)
                    .Replace("${db-type}", dbTypeString)

            // Create new configuration builder with hocon string merged with existing config
            // Add embedded HOCON first, then user config to allow overrides
            let configBuilder = ConfigurationBuilder()
            configBuilder.Add(HoconStringConfigurationSource(hoconString)) |> ignore
            configBuilder.AddConfiguration config |> ignore
            configBuilder.Build() :> IConfiguration
        | None ->
            config

    // FromObject must receive the `config` node itself (its `akka` child becomes
    // the HOCON root); asking for "config:akka" would unwrap one level too deep.
    let akkaConfig: ExpandoObject =
        unbox<_> (mergedConfig.GetSectionAsDynamic("config"))

    let akkaConfiguration = Configuration.ConfigurationFactory.FromObject akkaConfig

    let clusterNameValue = clusterName |> ValueLens.Value
    let system = System.create clusterNameValue akkaConfiguration

    Cluster.Get(system).SelfAddress |> Cluster.Get(system).Join

    let mediator = DistributedPubSub.Get(system).Mediator

    let mat = ActorMaterializer.Create system

    let subscribeForCommand command =
        subscribeForCommand system (typed mediator) command

    { new IActor with
        /// <summary>
        /// Gets the mediator actor reference which serves as the central hub for publish/subscribe messaging.
        /// This mediator is used to broadcast and route messages across the cluster, enabling distributed coordination.
        /// </summary>
        member _.Mediator = mediator
        
        /// <summary>
        /// Gets the actor materializer instance used to run Akka Streams.
        /// This materializer is essential for processing streaming data and handling asynchronous event flows within actors.
        /// </summary>
        member _.Materializer = mat
        
        /// <summary>
        /// Gets the underlying actor system that manages actor lifecycles, message dispatch, and cluster membership.
        /// </summary>
        member _.System = system
        
        /// <summary>
        /// Provides a time provider based on the system's scheduler, useful for timestamping messages and scheduling tasks.
        /// </summary>
        member _.TimeProvider = new AkkaTimeProvider(system)
        
        /// <summary>
        /// Gets the logger factory used to create loggers for detailed diagnostics and operational logging.
        /// This factory aids in capturing context-rich log entries across the actor system.
        /// </summary>
        member _.LoggerFactory = loggerFactory

        /// <summary>
        /// Gets the configuration used by the actor system.
        /// This configuration provides access to application settings and Akka configuration.
        /// </summary>
        member _.Configuration = config

        /// <summary>
        /// Subscribes for a command by instantiating a temporary actor that listens for a specific command.
        /// This dynamic subscription mechanism allows decoupled command handling by setting up an independent listener.
        /// </summary>
        /// <remarks>
        /// Under the hood, this method creates an actor that awaits a subscription acknowledgment before forwarding the command.
        /// Such a pattern is common in systems that separate command dispatch from command processing.
        /// </remarks>
        member _.SubscribeForCommand command = subscribeForCommand command
        
        /// <summary>
        /// Stops the actor system gracefully by terminating all actors and releasing associated resources.
        /// This method should be invoked during system shutdown to ensure a clean termination.
        /// </summary>
        member _.Stop() = system.Terminate()
        
        /// <summary>
        /// Creates a command subscription by using a provided factory to generate an entity reference,
        /// while applying a filter predicate to ensure only relevant commands are processed.
        /// </summary>
        /// <remarks>
        /// This approach encapsulates the wiring required to connect a command source with its handler,
        /// including setting up filters, delay mechanisms, and processing pipelines.
        /// </remarks>
        member this.CreateCommandSubscription factory cid id command filter metadata =
            createCommandSubscription this factory cid id command filter metadata
        
        /// <summary>
        /// Initializes a persistent actor with the defined configuration, initial state, unique name, command handler, and event applier.
        /// The actor leverages event sourcing to persist its state, enabling state recovery after failures or restarts.
        /// </summary>
        /// <remarks>
        /// This method orchestrates the conversion of incoming commands to events, then applies those events to update the state.
        /// Such a mechanism is key to implementing CQRS patterns, where events represent the source of truth for state changes.
        /// </remarks>
        /// <example>
        /// <code lang="fsharp">
        /// // Example: Initialize an actor that handles user management.
        /// // The command handler maps commands (e.g. Login, Register) to domain events,
        /// // while the event applier incorporates these events into the current state.
        /// let userActor = 
        ///     actorApi.InitializeActor(
        ///         config, 
        ///         userInitialState, 
        ///         "UserActor", 
        ///         userCommandHandler, 
        ///         userEventApplier)
        /// </code>
        /// </example>
        member this.InitializeActor initialState name handleCommand apply snapshotPolicy passivationPolicy =
            let toEvent mid ci sender metadata version event = toEvent system.Scheduler (Some mid) ci sender version metadata event
            init config loggerFactory initialState name toEvent this handleCommand apply snapshotPolicy passivationPolicy None

        member this.InitializeActorWithRunner initialState name handleCommand apply snapshotPolicy passivationPolicy effectRunner =
            let toEvent mid ci sender metadata version event = toEvent system.Scheduler (Some mid) ci sender version metadata event
            init config loggerFactory initialState name toEvent this handleCommand apply snapshotPolicy passivationPolicy effectRunner
        
        /// <summary>
        /// Initializes a saga to manage a long-running business process across multiple actors.
        /// The saga coordinates state transitions, side effects, and inter-actor communications.
        /// </summary>
        /// <remarks>
        /// Sagas are used to implement complex workflows that require orchestrating multiple steps,
        /// and this method bootstraps such a saga using event sourcing principles.
        /// </remarks>
        /// <example>
        /// <code lang="fsharp">
        /// // Example: Initialize an order processing saga.
        /// let orderSaga = 
        ///     actorApi.InitializeSaga(
        ///         config, 
        ///         initialOrderSagaState, 
        ///         orderEventHandler, 
        ///         orderSideEffects, 
        ///         orderStateApplier, 
        ///         "OrderSaga")
        /// </code>
        /// </example>
        member this.InitializeSaga
            (initialState: SagaState<'SagaState, 'State>)
            handleEvent
            applySideEffects
            apply
            name
            snapshotPolicy : EntityFac<obj> =
            Saga.init this initialState handleEvent applySideEffects apply name snapshotPolicy
        
        /// <summary>
        /// Registers the start rules: which sagas each stored event starts.
        /// </summary>
        /// <remarks>
        /// Before an aggregate stores an event, it evaluates these rules, sends each matching saga
        /// its starting message, and waits without holding a thread until every such saga is ready.
        /// An event that starts no saga is stored at once. Call this once per actor system.
        /// </remarks>
        member _.InitializeSagaStarter (rules: (obj -> list<(string -> IEntityRef<obj>) * PrefixConversion * obj>)) : unit =
            SagaStarter.Internal.init system rules

        member _.InitializeSagaStarter (rules: (obj -> list<(string -> IEntityRef<obj>)>)) : unit =
            let fullRules evt =
                rules evt
                |> List.map (fun factory -> (factory, PrefixConversion(Some id), evt))
            SagaStarter.Internal.init system fullRules
    }
