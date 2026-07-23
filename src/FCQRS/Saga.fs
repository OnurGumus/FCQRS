module FCQRS.Saga

open System
open FCQRS
open Akkling
open Akkling.Persistence
open Akka
open Common
open Common.SagaStarter
open Akka.Event
open Microsoft.Extensions.Logging
open Akkling.Cluster.Sharding
open Microsoft.FSharp.Reflection
open FCQRS.Model.Data
open AkklingHelpers
open Microsoft.Extensions.Configuration
open System.Diagnostics

let private activitySource = new ActivitySource(Common.Telemetry.SagaActivitySourceName)

// Innermost union case name (unwraps SagaStateWrapper.UserDefined to the
// user's state) — shared by the state-change span and the flow log line.
let rec private getUnionCaseName (obj: obj) =
    let t = obj.GetType()

    if FSharpType.IsUnion(t) then
        let case, fields = FSharpValue.GetUnionFields(obj, t)

        if case.Name = "UserDefined" && fields.Length = 1 then
            match fields.[0] with
            | null -> case.Name
            | field -> getUnionCaseName field
        else
            case.Name
    else
        sprintf "%A" obj

let private stateName (state: 'State) =
    match box state with
    | null -> "null"
    | boxed -> getUnionCaseName boxed

let private toStateChange (enteredAt: DateTime) state =
    StateChanged(state, enteredAt) |> box |> Persist :> Effect<obj>

/// Scheduled Self-message driving a saga expectation (retry ticks, exhaustion,
/// and exhaustion re-delivery). Tagged with the saga Version at arming so a
/// reminder armed by an earlier state is dropped after any state transition.
/// Never persisted; it lives and dies with the scheduler.
type internal ExpectationReminder = { ArmedVersion: int64 }

/// Position within an expectation's schedule after `elapsed` time in the
/// state: (completed retry ticks, delay until the next wake). The next wake is
/// the earlier of the next retry tick and the deadline itself; None means the
/// deadline has already passed. Pure: recovery recomputes the position from
/// the persisted entry time, so a crash cannot reset the schedule.
let internal expectationPosition (exp: Expectation) (elapsed: TimeSpan) : int * TimeSpan option =
    let deadline = exp.Deadline

    let intervals =
        match exp.RetryEvery with
        | FixedInterval dt -> Seq.initInfinite (fun _ -> dt)
        | Backoff(initial, factor, max) ->
            initial
            |> Seq.unfold (fun (cur: TimeSpan) ->
                let nextTicks = min max.Ticks (int64 (float cur.Ticks * factor))
                Some(cur, TimeSpan.FromTicks nextTicks))

    let mutable attempts = 0
    let mutable cum = TimeSpan.Zero
    let mutable nextTick = None

    (let e = intervals.GetEnumerator()
     let mutable go = true

     while go && e.MoveNext() do
         cum <- cum + e.Current

         if cum >= deadline then go <- false
         elif cum <= elapsed then attempts <- attempts + 1
         else
             nextTick <- Some cum
             go <- false)

    if elapsed >= deadline then
        attempts, None
    else
        let wake =
            match nextTick with
            | Some t -> min t deadline
            | None -> deadline

        attempts, Some(wake - elapsed)

/// An expectation whose schedule cannot make progress is a programming error
/// surfaced at first use (expectations are runtime values, so there is no
/// registration point to validate at). Returns Some error, None when valid.
let internal validateExpectation (exp: Expectation) : string option =
    let scheduleOk =
        match exp.RetryEvery with
        | FixedInterval dt -> dt > TimeSpan.Zero
        | Backoff(initial, factor, max) -> initial > TimeSpan.Zero && factor >= 1.0 && max >= initial

    if exp.Deadline <= TimeSpan.Zero then
        Some "Expectation.Deadline must be positive"
    elif not scheduleOk then
        Some "Expectation.RetryEvery must have positive, non-shrinking intervals"
    elif exp.Resend |> List.exists (fun c -> c.DelayInMs.IsSome) then
        Some "Expectation.Resend commands must not carry DelayInMs; the retry schedule owns all timing"
    else
        None

let private createCommand (mailbox: Eventsourced<_>) (command: 'TCommand) cid metadata =
    { CommandDetails = command
      CreationDate = mailbox.System.Scheduler.Now.UtcDateTime
      CorrelationId = cid
      Id = Guid.CreateVersion7().ToString() |> ValueLens.CreateAsResult |> Result.value
      Sender = mailbox.Self.Path.Name |> ValueLens.CreateAsResult |> Result.value |> Some
      Metadata = metadata }

type private ParentSaga<'SagaData, 'State> = SagaStateWithVersion<'SagaData, 'State>

type internal SagaStartingEventWrapper<'TEvent when 'TEvent : not null> =
    | SagaStartingEventWrapper of SagaStartingEvent<Event<'TEvent>>
    interface ISerializable

// Snapshot payload. The SagaStartingEventWrapper is journal seq 1 and is never
// replayed past a snapshot, so the snapshot must carry the starting event
// itself — otherwise a saga recovered through a snapshot has startingEvent =
// None and skips its recovery re-drive (pending commands never re-issued).
type internal SagaSnapshot<'SagaData, 'State, 'TEvent when 'TEvent : not null> =
    { Parent: SagaStateWithVersion<'SagaData, 'State>
      StartingEvent: SagaStartingEvent<Event<'TEvent>> option }
    interface ISerializable

/// How this incarnation of the saga came to exist. Only a recovered saga
/// re-drives its side effects: a fresh start signals Continue through the
/// (Stay, false) branch of applySideEffects, and re-driving there instead
/// sends a spurious ContinueOrAbort to the originator. Two independent bools
/// could say "recovered from a snapshot but not recovered"; this cannot.
type internal Incarnation =
    | Fresh
    | RecoveredFromJournal
    | RecoveredFromSnapshot

    /// The `recovering` flag handed to applySideEffects.
    member this.IsRecovery = this <> Fresh

/// Saga-start handshake state, threaded through the receive loop's recursion.
/// The actor's mailbox already serializes access, so this is a parameter, not
/// mutable cells. Named fields rather than a positional tuple of bools: a
/// transposed argument here silently changes what the handshake means.
type internal Handshake<'TEvent when 'TEvent : not null> =
    { StartingEvent: SagaStartingEvent<Event<'TEvent>> option
      /// The starting event is journaled, so a re-delivery must re-signal
      /// Continue rather than be dropped.
      Subscribed: bool
      /// The mediator acknowledged the CID subscription.
      SubscriptionAcked: bool
      Incarnation: Incarnation }

let private runSaga<'TEvent, 'SagaData, 'State when 'TEvent : not null and 'State : not null>
    (snapshotEvery: int64 option)
    (mailbox: Eventsourced<obj>)
    (log: ILogger)
    (flowLogger: ILogger)
    mediator
    (set: _ -> ParentSaga<'SagaData, 'State> -> _)
    (state: ParentSaga<'SagaData, 'State>)
    (applySideEffects:
        ParentSaga<'SagaData, 'State> -> option<SagaStarter.SagaStartingEvent<Event<'TEvent>>> -> bool -> 'State option)
    (applyNewState: SagaState<'SagaData, 'State> -> SagaState<'SagaData, 'State>)
    (wrapper: 'State -> ParentSaga<'SagaData, 'State>)
    body
    innerStateDefaults
    (currentSagaActivityRef: (Activity | null) ref)
    (cleanupOnStop: unit -> unit)
    =
    let rec innerSet (hs: Handshake<'TEvent>) =
        let { StartingEvent = startingEvent
              Subscribed = subscribed
              SubscriptionAcked = subscriptionAcked } =
            hs

        actor {
            let! msg = mailbox.Receive()

            // Emit a state-change activity (kept alive until the next transition so
            // sub-activities become children). Parent: the starting event's metadata
            // traceparent first, the CID for backward compat second, none otherwise.
            let emitStateChangeActivity (newState: 'State) =
                // Dispose previous activity if exists
                match currentSagaActivityRef.Value with
                | null -> ()
                | prev ->
                    prev.Dispose()
                    currentSagaActivityRef.Value <- null

                if activitySource.HasListeners() then
                    let stateStr = stateName newState

                    let cidStr, parent =
                        match startingEvent with
                        | Some se ->
                            let msg = se.Event :> FCQRS.Model.Data.IMessage
                            let cidStr = msg.CID |> ValueLens.Value |> ValueLens.Value
                            cidStr, tryTraceContext msg.Metadata cidStr
                        | None -> "", None

                    let act =
                        match parent with
                        | Some p -> activitySource.StartActivity($"Saga:{stateStr}", ActivityKind.Internal, p)
                        | None -> activitySource.StartActivity($"Saga:{stateStr}", ActivityKind.Internal)

                    match act with
                    | null -> ()
                    | act ->
                        act.SetTag("cid", cidStr) |> ignore
                        act.SetTag("saga.id", mailbox.Self.Path.Name) |> ignore
                        act.SetTag("saga.state", stateStr) |> ignore
                        currentSagaActivityRef.Value <- act

            match msg with
            | :? Event<AbortedEvent> ->
                // Mark the state span Error before cleanupOnStop disposes it, so the
                // aborted saga is flagged in the trace rather than ending silently.
                match currentSagaActivityRef.Value with
                | null -> ()
                | act ->
                    act.SetStatus(ActivityStatusCode.Error, "Saga aborted: originator restart detected")
                    |> ignore

                cleanupOnStop ()
                let poision = Akka.Cluster.Sharding.Passivate <| Actor.PoisonPill.Instance
                log.LogInformation("Aborting")
                mailbox.Parent() <! poision
                return! innerSet hs

            | :? Persistence.RecoveryCompleted ->
                subscriber mediator mailbox
                log.LogInformation("Saga RecoveryCompleted")
                return! innerSet hs
            | Recovering mailbox (:? SagaStartingEventWrapper<'TEvent> as SagaStartingEventWrapper event) ->
                return!
                    innerSet
                        { hs with
                            StartingEvent = Some event
                            Subscribed = true
                            Incarnation = RecoveredFromJournal }
            | Recovering mailbox (:? SagaEvent<'State> as event) ->
                let hs = { hs with Incarnation = RecoveredFromJournal }

                match event with
                | StateChanged(s, enteredAt) ->
                    try
                        let newSagaState = applyNewState (wrapper s).SagaState
                        // Mirror the live path's per-event version bump so the recovered
                        // in-memory version matches and post-snapshot replay stays consistent.
                        // StateEnteredAt comes from the journaled event, not the wall clock,
                        // so expectation deadlines survive restarts unmoved.
                        let newState =
                            { state with
                                SagaState = newSagaState
                                Version = state.Version + 1L
                                StateEnteredAt = enteredAt }

                        return! newState |> set hs
                    with ex ->
                        log.LogError(ex, "Fatal error during saga recovery for {0}. Terminating process to prevent restart loop.", mailbox.Self.Path.ToString())
                        fatalFailFast currentSagaActivityRef.Value "Process terminated due to saga error" ex
                        return! innerSet hs

            | LifecycleEvent PostStop ->
                // Passivation and shard handoff stop the entity without aborting
                // it: cancel its pending delayed commands so they don't fire into
                // the resurrected incarnation, which re-drives and re-schedules
                // its own on recovery. Idempotent — the abort and StopSaga paths
                // already ran cleanupOnStop and emptied the list.
                cleanupOnStop ()
                return! innerSet hs
            | PersistentLifecycleEvent _
            | :? Persistence.SaveSnapshotSuccess
            | LifecycleEvent _ ->
                // Lifecycle noise must not mutate handshake state (this used to
                // force subscribed=true, masking the real RecoveryCompleted /
                // SubscriptionAcknowledged signals).
                return! innerSet hs
            | SnapshotOffer(snapState: obj) ->
                let hs = { hs with Incarnation = RecoveredFromSnapshot }

                // Subscribed = true in both branches: the wrapper is journal seq 1,
                // so any snapshot postdates it — the starting event was journaled.
                // Leaving it false made a snapshot-recovered resurrection drop the
                // re-delivered SagaStartingEvent without signalling Continue, which
                // deadlocked the originator's handshake into a process FailFast.
                match snapState with
                | :? SagaSnapshot<'SagaData, 'State, 'TEvent> as snap ->
                    // Restore the starting event alongside the state — the wrapper
                    // event predates the snapshot and will not be replayed.
                    return!
                        snap.Parent
                        |> set
                            { hs with
                                StartingEvent = snap.StartingEvent
                                Subscribed = true }
                | _ ->
                    // Pre-SagaSnapshot shape (older journals): no starting event
                    // available. The recovery re-drive still runs (see the
                    // SubscriptionAcknowledged branch); re-issued commands just
                    // lose the starting event's metadata.
                    return! snapState |> unbox<_> |> set { hs with Subscribed = true }
            | SubscriptionAcknowledged mailbox _ ->
                // notify saga starter about the subscription completed
                let nextInner =
                    { hs with
                        Subscribed = true
                        SubscriptionAcked = true }

                match startingEvent with
                | Some _ ->
                    // Only a genuinely recovered incarnation re-drives. On a fresh
                    // start this ack arrives while the saga sits in Started, and
                    // passing true here ran handleStartedState's recovery re-drive:
                    // a spurious ContinueOrAbort that either re-published the
                    // starting event (duplicate delivery to every same-CID
                    // subscriber) or, if the originator had already moved on,
                    // falsely aborted a live saga. The fresh-start Continue is
                    // signalled by the (Stay, false) branch of applySideEffects.
                    let newState = applySideEffects state startingEvent hs.Incarnation.IsRecovery

                    match newState with
                    | Some newState ->
                        // Activity will be emitted when state is persisted (in Persisted branch)
                        return!
                            StateChanged(newState, mailbox.System.Scheduler.Now.UtcDateTime)
                            |> box
                            |> Persist
                            <@> innerSet nextInner
                    | None ->
                        return! state |> set hs <@> innerSet nextInner
                | None when hs.Incarnation.IsRecovery ->
                    // Recovered through a snapshot that carried no starting event
                    // (pre-SagaSnapshot shape). The saga state is real and journaled,
                    // so the recovery re-drive must still run — re-issue pending
                    // commands and re-signal Continue — or the saga sits passive
                    // until poked from outside. IsRecovery, not = RecoveredFromSnapshot:
                    // events replayed after such a snapshot flip the incarnation to
                    // RecoveredFromJournal, and the re-drive must still run for them.
                    let newState = applySideEffects state None true

                    match newState with
                    | Some newState ->
                        return!
                            StateChanged(newState, mailbox.System.Scheduler.Now.UtcDateTime)
                            |> box
                            |> Persist
                            <@> innerSet nextInner
                    | None ->
                        return! state |> set hs <@> innerSet nextInner
                | None ->
                    // Wait for starting event before applying side effects.
                    return! state |> set hs <@> innerSet nextInner

            | Deferred mailbox obj
            | Persisted mailbox obj ->
                match obj with
                | :? SagaStartingEventWrapper<'TEvent> as SagaStartingEventWrapper e ->
                    let nextInner =
                        { hs with
                            StartingEvent = Some e
                            Subscribed = true }

                    if startingEvent.IsNone then
                        // A live wrapper persist is always a fresh start (replayed
                        // wrappers arrive through the Recovering branch), so this is
                        // never a recovery re-drive. Passing SubscriptionAcked here
                        // spuriously sent ContinueOrAbort when the mediator ack won
                        // the race against the wrapper persist.
                        let newState = applySideEffects state (Some e) false

                        match newState with
                        | Some newState ->
                            // Activity will be emitted when state is persisted (in Persisted branch)
                            return!
                                StateChanged(newState, mailbox.System.Scheduler.Now.UtcDateTime)
                                |> box
                                |> Persist
                                <@> innerSet nextInner
                        | None ->
                            return! state |> set hs <@> innerSet nextInner
                    else
                        return! innerSet nextInner
                | :? SagaEvent<'State> as e ->
                    match e with
                    | StateChanged(originalState, enteredAt) ->
                        try
                            if messageFlowEnabled flowLogger then
                                flowLogger.LogInformation(
                                    "Saga {Saga} changed state to {State} [cid: {CID}]",
                                    mailbox.Self.Path.Name,
                                    stateName originalState,
                                    mailbox.Self.Path.Name |> SagaStarter.Internal.toRawGuid)

                            // Emit activity for the persisted state change
                            emitStateChangeActivity originalState
                            let outerState = wrapper originalState

                            let newSagaState = applyNewState outerState.SagaState

                            // This Persisted callback means one StateChanged event was just
                            // journaled, so advance the saga version and STORE it. Previously the
                            // bumped value was only used in the snapshot check below and never
                            // persisted, so Version stayed 0 and the snapshot cadence never fired.
                            let version = outerState.Version + 1L

                            let parentState =
                                { outerState with
                                    SagaState = newSagaState
                                    Version = version
                                    // Anchor from the journaled event: expectation
                                    // deadlines for this state measure from here.
                                    StateEnteredAt = enteredAt }

                            let newState = applySideEffects parentState startingEvent false

                            let dueForSnapshot =
                                match snapshotEvery with
                                | Some every -> version >= every && version % every = 0L
                                | None -> false

                            match newState with
                            | Some newState ->
                                let newSagaState: ParentSaga<_, _> =
                                    let newInnerState = parentState.SagaState
                                    let newInnerState = { newInnerState with State = newState }

                                    { parentState with
                                        SagaState = newInnerState }

                                // newState triggers another Persisted event (which emits its own
                                // activity). At a snapshot boundary we additionally snapshot the
                                // just-confirmed state — without dropping this pending transition,
                                // which the previous code did.
                                let persistNext =
                                    StateChanged(newState, mailbox.System.Scheduler.Now.UtcDateTime)
                                    |> box
                                    |> Persist

                                if dueForSnapshot then
                                    return!
                                        newSagaState |> set hs
                                        <@> persistNext
                                        <@> SaveSnapshot { Parent = parentState; StartingEvent = startingEvent }
                                else
                                    return!
                                        newSagaState |> set hs
                                        <@> persistNext
                            | None ->
                                if dueForSnapshot then
                                    return! parentState |> set hs <@> SaveSnapshot { Parent = parentState; StartingEvent = startingEvent }
                                else
                                    return! parentState |> set hs
                        with ex ->
                            log.LogError(ex, "Fatal error during saga persisted event handling for {0}. Terminating process to prevent restart loop.", mailbox.Self.Path.ToString())
                            // FailFast, not Exit: Exit runs ProcessExit handlers (which can
                            // hang or flush bad state); the policy is an immediate kill.
                            fatalFailFast currentSagaActivityRef.Value "Process terminated due to saga error" ex
                            return! state |> set hs


                | other ->
                    log.LogInformation(
                        "Unknown event:{@event}, expecting :{@ev}",
                        other.GetType(),
                        typeof<SagaEvent<'State>>
                    )

                    return! state |> set hs

            | :? (SagaStarter.SagaStartingEvent<Event<'TEvent>>) as e when startingEvent.IsNone ->
                return! SagaStartingEventWrapper e |> box |> Persist
            | :? (SagaStarter.SagaStartingEvent<Event<'TEvent>>) when subscribed ->
                cont mediator
                return! innerSet hs
            | msg when msg.GetType().Name.StartsWith("SagaStartingEvent") ->
                return! innerSet hs

            | _ ->
                return! body msg
        }

    innerSet innerStateDefaults

let private actorProp<'SagaData, 'State, 'TEvent when 'TEvent : not null and 'State : not null>
    initialState
    name
    (handleEvent: obj -> SagaState<'SagaData, 'State> -> EventAction<'State>)
    (applySideEffects2:
        SagaState<'SagaData, 'State>
            -> option<SagaStartingEvent<Event<'TEvent>>>
            -> bool
            -> SagaTransition<'State> * ExecuteCommand list)
    (apply: SagaState<'SagaData, 'State> -> SagaState<'SagaData, 'State>)
    (snapshotPolicy: SnapshotPolicy)
    (actorApi: IActor)
    (mediator: IActorRef<_>)
    (mailbox: Eventsourced<obj>)
    =
    let baseCid: CID =
        mailbox.Self.Path.Name |> SagaStarter.Internal.toRawGuid
        |> ValueLens.CreateAsResult
        |> Result.value

    let log = mailbox.UntypedContext.GetLogger()
    let loggerFactory = actorApi.LoggerFactory
    let config = actorApi.Configuration
    let logger = loggerFactory.CreateLogger name
    let flowLogger = loggerFactory.CreateLogger Telemetry.MessageFlowCategory
    let flowCid = baseCid |> ValueLens.Value |> ValueLens.Value

    // Ref cell to hold the current saga state activity (kept alive across iterations)
    // This is at actorProp level so both runSaga and applySideEffects can access it
    let currentSagaActivityRef: (Activity | null) ref = ref null
    // How this incarnation started (fresh / replayed) is threaded through the
    // receive loop as Handshake.Incarnation, not held in a cell.
    // Cancelables for delayed commands the saga has scheduled, paired with the
    // wall-clock time at which the underlying schedule is guaranteed to have
    // fired (delay + buffer). Akka's ICancelable does not flip
    // IsCancellationRequested when a scheduled task simply fires, so we prune
    // by expiration to keep this list bounded for long-lived sagas. Cancelled
    // on termination so a passivated/aborted saga's still-pending delayed
    // messages don't fire into a resurrected entity in the wrong state.
    let pendingCancelablesRef: (Akka.Actor.ICancelable * DateTime) list ref = ref []
    let pendingExpiryBuffer = TimeSpan.FromSeconds(30.0)

    let disposeCurrentActivity () =
        // Dispose the in-flight saga-state activity so span context doesn't leak.
        match currentSagaActivityRef.Value with
        | null -> ()
        | act ->
            act.Dispose()
            currentSagaActivityRef.Value <- null

    let cancelScheduled (entries: (Akka.Actor.ICancelable * DateTime) list) =
        // Cancel scheduled delayed commands (fired ones are no-ops).
        for (c, _) in entries do
            try
                c.Cancel()
            with ex ->
                log.Debug(ex, "Error cancelling pending saga command during cleanup")

    // The expectation armed by the current state (and its effective entry time),
    // if any. In-memory on purpose: it dies with the timers it describes, and
    // recovery re-populates both from the same re-drive. Reminder staleness is
    // decided by the Version tag, not by this cell.
    let armedExpectationRef: (Expectation * DateTime) option ref = ref None
    // Latest starting event seen by applySideEffects, so the reminder path can
    // dispatch re-sends with the same metadata a state-entry dispatch carries.
    let lastStartingEventRef: option<SagaStarter.SagaStartingEvent<Event<'TEvent>>> ref = ref None

    let cleanupOnStop () =
        disposeCurrentActivity ()
        armedExpectationRef.Value <- None
        cancelScheduled pendingCancelablesRef.Value
        pendingCancelablesRef.Value <- []

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

    // Command dispatch, extracted from applySideEffects so the expectation
    // reminder path can re-send a state's Resend commands without re-invoking
    // the domain's applySideEffects.
    let dispatchCommands
        (startingEvent: option<SagaStartingEvent<Event<'TEvent>>>)
        (selfDelayed: ResizeArray<Akka.Actor.ICancelable * DateTime>)
        (cmds: ExecuteCommand list) =
        for cmd in cmds do
            try
                let createFinalCommand cmd =
                    let baseType =
                        let t = cmd.Command.GetType()
                        // A C# 15 `union` is a struct, so its BaseType is ValueType — the
                        // union type itself is what the aggregate's Command<_> expects.
                        // Abstract-record / F# DU cases instead carry BaseType = the DU type.
                        match t.BaseType with
                        | null -> t
                        | b when b = typeof<obj> || b = typeof<System.ValueType> -> t
                        | b -> b

                    let baseMetadata =
                        match startingEvent with
                        | Some se ->
                            match box se.Event with
                            | :? FCQRS.Model.Data.IMessage as msg ->
                                msg.Metadata
                            | _ ->
                                Map.empty
                        | None ->
                            Map.empty

                    // Keep baseCid for pub/sub routing - changing CID breaks saga event reception
                    let command = createCommand mailbox cmd.Command baseCid baseMetadata

                    let unboxx (msg: Command<obj>) =
                        let genericType = typedefof<Command<_>>.MakeGenericType [| baseType |]

                        let actorId: AggregateId option =
                            mailbox.Self.Path.Name |> ValueLens.CreateAsResult |> Result.value |> Some

                        FSharpValue.MakeRecord(
                            genericType,
                            [| msg.CommandDetails; msg.CreationDate; msg.Id; actorId; msg.CorrelationId; msg.Metadata |]
                        )

                    let finalCommand = unboxx command
                    finalCommand

                let (targetActor: ICanTell<_>), finalCommand =
                    match cmd.TargetActor with
                    | FactoryAndName { Factory = factory; Name = n } ->
                        let name =
                            match n with
                            | Name n -> n
                            | Originator -> mailbox.Self.Path.Name |> toOriginatorName

                        let factory = factory :?> (string -> IEntityRef<obj>)
                        factory name, createFinalCommand cmd

                    | Sender ->
                        // The ambient sender at side-effect time is the journal
                        // (Persisted re-injection) or the pub-sub mediator (ack
                        // re-drive), never the original trigger — warn so the
                        // misrouted command is at least visible in the logs.
                        log.Warning(
                            "TargetActor.Sender resolved to {0}, the ambient sender at side-effect time (journal or mediator), not the original trigger; the command will likely dead-letter. Target the originator via FactoryAndName instead.",
                            mailbox.Sender().Path)

                        mailbox.Sender(), createFinalCommand cmd
                    | ActorRef actor -> actor :?> ICanTell<_>, cmd.Command
                    | Self -> mailbox.Self, cmd.Command

                let targetStr =
                    match cmd.TargetActor with
                    | FactoryAndName { Name = Name n } -> n
                    | FactoryAndName { Name = Originator } -> mailbox.Self.Path.Name |> toOriginatorName
                    | Sender -> mailbox.Sender().Path.Name
                    | ActorRef _ -> "actorRef"
                    | Self -> mailbox.Self.Path.Name

                // The ContinueOrAbort handshake is framework plumbing, not part of
                // the application's message narrative — keep it out of the flow log.
                let isInternalHandshake =
                    let t = cmd.Command.GetType()

                    (t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<ContinueOrAbort<_>>)
                    || t = typeof<ExpectationReminder>

                if messageFlowEnabled flowLogger && not isInternalHandshake then
                    match cmd.DelayInMs with
                    | Some(delayValue, _) ->
                        flowLogger.LogInformation(
                            "Saga {Saga} scheduled command {Command} to {Target} (+{Delay}ms) [cid: {CID}]",
                            mailbox.Self.Path.Name,
                            payloadTag cmd.Command,
                            targetStr,
                            delayValue,
                            flowCid)
                    | None ->
                        flowLogger.LogInformation(
                            "Saga {Saga} sent command {Command} to {Target} [cid: {CID}]",
                            mailbox.Self.Path.Name,
                            payloadTag cmd.Command,
                            targetStr,
                            flowCid)

                // Timestamped marker on the long-lived state span: shows when within
                // the state each command left, without a child span per command.
                match currentSagaActivityRef.Value with
                | null -> ()
                | act ->
                    let tags = ActivityTagsCollection()
                    tags.Add("command.type", cmd.Command.GetType().Name)
                    tags.Add("target", targetStr)

                    let eventName =
                        match cmd.DelayInMs with
                        | Some(delayValue, _) ->
                            tags.Add("delay.ms", delayValue)
                            "command.scheduled"
                        | None -> "command.issued"

                    act.AddEvent(ActivityEvent(eventName, tags = tags)) |> ignore

                match cmd.DelayInMs with
                | Some (delayValue, name) ->
                    let currentScheduler = mailbox.System.Scheduler
                    let scheduleAtDelay = System.TimeSpan.FromMilliseconds delayValue

                    // IEntityRef.Tell wraps the message in a ShardEnvelope before
                    // hitting the shard region. Scheduling a raw message to the
                    // region (Underlying) bypasses that wrapper and the message
                    // extractor rejects it, so wrap it here instead. Without this,
                    // delayed commands to sharded aggregates are silently lost.
                    let untypedReceiver, messageToSchedule =
                        match targetActor with
                        | :? (Akkling.Cluster.Sharding.IEntityRef<obj>) as entityRef ->
                            entityRef.Underlying,
                            ({ ShardId = entityRef.ShardId
                               EntityId = entityRef.EntityId
                               Message = finalCommand }: Akkling.Cluster.Sharding.ShardEnvelope)
                            :> obj
                        | other -> other.Underlying, finalCommand

                    let untypedSender = mailbox.Self.Underlying :?> Akka.Actor.IActorRef

                    let cancelable: Akka.Actor.ICancelable =
                        match currentScheduler with
                        | :? FCQRS.Scheduler.ObservingScheduler as obs ->
                            obs.ScheduleTellOnce(Some name, scheduleAtDelay, untypedReceiver, messageToSchedule, untypedSender)
                        | sch ->
                            let c = new Akka.Actor.Cancelable(sch)
                            sch.ScheduleTellOnce(scheduleAtDelay, untypedReceiver, messageToSchedule, untypedSender, c)
                            c :> Akka.Actor.ICancelable

                    // Track so we can cancel on saga passivation / abort; also prune
                    // entries whose scheduled delay+buffer has elapsed so this list
                    // stays bounded for long-lived sagas.
                    let now = mailbox.System.Scheduler.Now.UtcDateTime
                    let expiresAt = now + scheduleAtDelay + pendingExpiryBuffer
                    let liveEntries =
                        pendingCancelablesRef.Value
                        |> List.filter (fun (_, exp) -> exp > now)
                    pendingCancelablesRef.Value <- (cancelable, expiresAt) :: liveEntries

                    // Track Self-targeted ones: StopSaga cancels these (see below).
                    match cmd.TargetActor with
                    | Self -> selfDelayed.Add((cancelable, expiresAt))
                    | _ -> ()

                | None ->
                    targetActor <! finalCommand
            with ex ->
                log.Error(ex, "Fatal error in saga command processing for {0}. Terminating process to prevent restart loop.", name)
                fatalFailFast currentSagaActivityRef.Value "Process terminated due to saga error" ex

    // Arm exactly one wake-up for the armed expectation: the next retry tick or
    // the deadline, whichever comes sooner, computed from the persisted entry
    // time. Ticks that elapsed while the saga was down are skipped, so a
    // recovery never fires a catch-up burst, and a crash loop cannot postpone
    // the deadline (the anchor is journaled, not the timer).
    let armExpectationReminder (exp: Expectation) (entered: DateTime) (version: int64) (delayOverride: TimeSpan option) =
        let now = mailbox.System.Scheduler.Now.UtcDateTime

        let delay =
            match delayOverride with
            | Some d -> d
            | None ->
                let _, next = expectationPosition exp (now - entered)

                let baseDelay =
                    match next with
                    | Some d -> d
                    | None -> TimeSpan.Zero // deadline already passed: wake immediately to exhaust

                match exp.RetryEvery with
                | Backoff _ ->
                    // Jitter: sagas released together by one infrastructure blip
                    // must not retry in lockstep against the same shard.
                    TimeSpan.FromTicks(int64 (float baseDelay.Ticks * (0.8 + 0.4 * Random.Shared.NextDouble())))
                | FixedInterval _ -> baseDelay

        let delayMs = max 1L (int64 delay.TotalMilliseconds)

        dispatchCommands
            None
            (ResizeArray())
            [ { TargetActor = Self
                Command = { ArmedVersion = version } :> obj
                DelayInMs = Some(delayMs, $"expectation:{name}:v{version}") } ]

    let applySideEffects
        (sagaState: ParentSaga<'SagaData, 'State>)
        (startingEvent: option<SagaStartingEvent<Event<'TEvent>>>)
        recovering
        : 'State option =
        let transition, (cmds: ExecuteCommand list) =
            try
                applySideEffects2 sagaState.SagaState startingEvent recovering
            with ex ->
                log.Error(ex, "Fatal error in saga applySideEffects2 for {0}. Terminating process to prevent restart loop.", name)
                fatalFailFast currentSagaActivityRef.Value "Process terminated due to saga error" ex

                failwith "Process terminated due to saga error" // This line will never execute but satisfies the compiler

        // The reminder path re-sends with the same metadata a state entry uses.
        lastStartingEventRef.Value <- startingEvent
        // Whatever the previous state armed is superseded by this invocation;
        // the StayExpecting branch below re-arms for the current state.
        armedExpectationRef.Value <- None

        // Capture before dispatching: on StopSaga only reminders scheduled by
        // EARLIER states are cancelled — delayed commands returned alongside
        // StopSaga are the saga's final commands and must still fire. The one
        // exception is Self-targeted delayed commands (tracked below): a
        // completed saga must not be resurrected by its own final message.
        let pendingBefore = pendingCancelablesRef.Value
        let selfDelayed = ResizeArray<Akka.Actor.ICancelable * DateTime>()

        dispatchCommands startingEvent selfDelayed cmds

        // Handle ResumeFirstEvent behavior internally when needed
        match transition, recovering with
        | Stay, false ->
            // This handles the old ResumeFirstEvent case
            cont mediator
            None
        | Stay, true ->
            // A saga resurrected mid-handshake (persist failure + remember-entities,
            // restart, rebalance) may never have sent Continue — and skipping it here
            // left the originator blocked in the SagaStarter ask while this saga's
            // ContinueOrAbort sat unprocessed in the originator's stalled mailbox:
            // a process-local deadlock. Continue is idempotent at the starter
            // (duplicates and untracked batches are tolerated), so always re-signal.
            cont mediator
            None
        | StayExpecting exp, _ ->
            // Same handshake as Stay in both directions: a fresh entry signals
            // Continue, and a resurrected saga must re-signal it — skipping that
            // re-creates the originator deadlock documented on the Stay branch.
            cont mediator

            (match validateExpectation exp with
             | Some reason ->
                 log.Error("Invalid saga expectation in {0}: {1}. Terminating process to prevent restart loop.", name, reason)

                 fatalFailFast
                     currentSagaActivityRef.Value
                     "Process terminated due to saga error"
                     (InvalidOperationException reason)
             | None ->
                 // Effective entry: the persisted state-entry time. A saga expecting
                 // from its never-persisted initial state anchors at now instead
                 // (recovery then re-anchors at recovery time — best effort there).
                 let entered =
                     if sagaState.StateEnteredAt = DateTime.MinValue then
                         mailbox.System.Scheduler.Now.UtcDateTime
                     else
                         sagaState.StateEnteredAt

                 armedExpectationRef.Value <- Some(exp, entered)
                 dispatchCommands startingEvent selfDelayed exp.Resend
                 armExpectationReminder exp entered sagaState.Version None)

            None
        | NextState newState, _ ->
            Some newState
        | StopSaga, _ ->
            disposeCurrentActivity ()

            if selfDelayed.Count > 0 then
                log.Warning(
                    "Saga {0} returned StopSaga with {1} delayed Self command(s); cancelling them — a completed saga must not be resurrected by its own final message. Target another entity instead.",
                    name,
                    selfDelayed.Count)

            // Cancel pending delayed commands from earlier states (the workflow is
            // done — its reminders must not fire into a resurrected entity) and any
            // Self-targeted ones just scheduled, but NOT the other commands returned
            // with StopSaga: they are the saga's final act and must still be delivered.
            cancelScheduled (pendingBefore @ List.ofSeq selfDelayed)
            pendingCancelablesRef.Value <- []
            let poision = Cluster.Sharding.Passivate <| Actor.PoisonPill.Instance
            mailbox.Parent() <! poision
            log.Info("{0} Completed", name)

            if messageFlowEnabled flowLogger then
                flowLogger.LogInformation(
                    "Saga {Saga} completed and stopped [cid: {CID}]",
                    mailbox.Self.Path.Name,
                    flowCid)

            None

    let rec set innerStateDefaults (sagaState: ParentSaga<'SagaData, 'State>) =

        let body (msg: obj) =
            actor {
                match msg, sagaState with
                | (:? ExpectationReminder as reminder), state ->
                    match armedExpectationRef.Value with
                    | Some(exp, entered) when reminder.ArmedVersion = state.Version ->
                        let now = mailbox.System.Scheduler.Now.UtcDateTime
                        let elapsed = now - entered

                        if elapsed < exp.Deadline then
                            // Retry tick: re-send exactly the expectation's commands
                            // (never the state's other side effects) and arm the next
                            // wake. Duplicate delivery is covered by the same
                            // retry-safe contract recovery re-drives already require.
                            dispatchCommands lastStartingEventRef.Value (ResizeArray()) exp.Resend
                            armExpectationReminder exp entered state.Version None
                            return! state |> set innerStateDefaults
                        else
                            let attempts, _ = expectationPosition exp elapsed

                            let exhausted: ExpectationExhausted =
                                { StateName = stateName state.SagaState.State
                                  EnteredAt = entered
                                  Attempts = attempts }

                            if messageFlowEnabled flowLogger then
                                flowLogger.LogInformation(
                                    "Saga {Saga} expectation exhausted in {State} after {Attempts} attempt(s) [cid: {CID}]",
                                    mailbox.Self.Path.Name,
                                    exhausted.StateName,
                                    attempts,
                                    flowCid)

                            // handleEvent exceptions are fatal by policy, but for this
                            // framework-injected message a domain without a matching
                            // case (MatchFailureException) must not kill the process:
                            // downgrade to unhandled and re-deliver below.
                            let action =
                                try
                                    handleEvent (box exhausted) state.SagaState
                                with ex ->
                                    log.Error(
                                        ex,
                                        "Saga {0} threw while handling ExpectationExhausted for state {1}; treating as unhandled.",
                                        name,
                                        exhausted.StateName)

                                    UnhandledEvent

                            match action with
                            | StateChangedEvent newState -> return! newState |> toStateChange now
                            | _ ->
                                log.Error(
                                    "Saga {0} left ExpectationExhausted for state {1} unhandled; re-delivering in {2}. Handle it with a transition to a failure or compensation state.",
                                    name,
                                    exhausted.StateName,
                                    exp.Deadline)

                                armExpectationReminder exp entered state.Version (Some exp.Deadline)
                                return! state |> set innerStateDefaults
                    | _ ->
                        // Stale: armed by an earlier state (the version moved on) or
                        // already cleaned up. Fired schedules are no-ops to cancel.
                        return! sagaState |> set innerStateDefaults
                | msg, state ->
                    try
                        let state: EventAction<'State> = handleEvent msg state.SagaState

                        if messageFlowEnabled flowLogger then
                            flowLogger.LogInformation(
                                "Saga {Saga} received {Event}, decided {Decision} [cid: {CID}]",
                                mailbox.Self.Path.Name,
                                logPayload msg,
                                payloadTag state,
                                flowCid)

                        match state with
                        | StateChangedEvent newState ->
                            let newState = newState |> toStateChange mailbox.System.Scheduler.Now.UtcDateTime
                            return! newState
                        | IgnoreEvent -> return! sagaState |> set innerStateDefaults
                        | Stash _
                        | Unstash _
                        | UnstashAll _
                        | UnhandledEvent
                        | PublishEvent _
                        | PersistEvent _
                        | PersistAllEvents _
                        | PersistAndSnapshot _
                        | DeferEvent _
                        // RunAsync is an aggregate-only effect (self-dispatch);
                        // sagas orchestrate via commands, so it is not valid here.
                        | RunAsync _ -> return Unhandled
                    with ex ->
                        log.Error(ex, "Fatal error in saga handleEvent for {0}. Terminating process to prevent restart loop.", name)
                        fatalFailFast currentSagaActivityRef.Value "Process terminated due to saga error" ex
                        return Unhandled // This line will never execute but satisfies the compiler
            }

        let wrapper =
            fun (s: 'State) ->
                { sagaState with
                    SagaState =
                        { Data = sagaState.SagaState.Data
                          State = s } }

        runSaga
            snapshotEvery
            mailbox
            logger
            flowLogger
            mediator
            set
            sagaState
            applySideEffects
            apply
            wrapper
            body
            innerStateDefaults
            currentSagaActivityRef
            cleanupOnStop

    set
        { StartingEvent = None
          Subscribed = false
          SubscriptionAcked = false
          Incarnation = Fresh }
        initialState

let internal init<'SagaData, 'State, 'TEvent when 'TEvent : not null and 'State : not null>
    (actorApi: IActor)
    (initialState: SagaState<_, _>)
    (handleEvent: obj -> SagaState<'SagaData, 'State> -> EventAction<'State>)
    (applySideEffects:
        SagaState<'SagaData, 'State>
            -> option<SagaStartingEvent<Event<'TEvent>>>
            -> bool
            -> SagaTransition<'State> * ExecuteCommand list)
    (apply: SagaState<'SagaData, 'State> -> SagaState<'SagaData, 'State>)
    name
    (snapshotPolicy: SnapshotPolicy)
    =
    let initialState =
        { Version = 0L
          SagaState = initialState
          // Sentinel: no state has been persisted yet. An expectation armed from
          // this initial state anchors at arming time instead.
          StateEnteredAt = DateTime.MinValue }

    entityFactoryFor actorApi.System shardResolver name
     <| propsPersist (
         actorProp initialState name handleEvent applySideEffects apply snapshotPolicy actorApi (typed actorApi.Mediator)
     )
     <| true
