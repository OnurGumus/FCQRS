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
// user's state) — shared by the state-change span and the flow log line. A C#
// union is named by its active case's type. Another state is named by its type,
// so a record's field values never reach a span name; an enum, number, or string
// keeps its value.
let rec private getUnionCaseName (obj: obj) =
    let t = obj.GetType()

    match unionCaseOf obj with
    | Some case -> case.GetType().Name
    | None when FSharpType.IsUnion(t) ->
        let case, fields = FSharpValue.GetUnionFields(obj, t)

        if case.Name = "UserDefined" && fields.Length = 1 then
            match fields.[0] with
            | null -> case.Name
            | field -> getUnionCaseName field
        else
            case.Name
    | None when t.IsEnum || t.IsPrimitive || t = typeof<string> -> sprintf "%A" obj
    | None -> t.Name

let private stateName (state: 'State) =
    match box state with
    | null -> "null"
    | boxed -> getUnionCaseName boxed

let private toStateChange (enteredAt: DateTime) state =
    StateChanged(state, enteredAt) |> box |> Persist :> Effect<obj>

/// Scheduled Self-message driving a saga expectation (retry ticks, exhaustion,
/// and exhaustion re-delivery). Tagged with the arm epoch — a per-actor counter
/// bumped on EVERY arm — so re-arming instantly stales whatever was armed before
/// it. The saga Version cannot serve here: arming does not cancel the previous
/// scheduled reminder, so two arms at one version (applySideEffects runs again
/// for the same state when the start handshake acknowledges) left two live
/// chains, each re-sending and re-arming for the life of the state.
/// Never persisted; it lives and dies with the scheduler.
type internal ExpectationReminder = { ArmedEpoch: int64 }

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

// `sender` is resolved once per actor (see selfAggregateId) rather than derived
// here: a saga's name is its originator's id plus "~Saga~" and the cid, so it can
// exceed the Sender field's length limit even when the originator's id was legal.
let private createCommand (mailbox: Eventsourced<_>) (sender: AggregateId option) (command: 'TCommand) cid metadata =
    { CommandDetails = command
      CreationDate = mailbox.System.Scheduler.Now.UtcDateTime
      CorrelationId = cid
      Id = Guid.CreateVersion7().ToString() |> ValueLens.CreateAsResult |> Result.value
      Sender = sender
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

/// Changes only FCQRS's event-bearing saga wrappers. Domain state and data are
/// retained as the original objects; this is not a general snapshot migration.
module internal ReadUpcasting =
    let private flags = Reflection.BindingFlags.Public ||| Reflection.BindingFlags.NonPublic

    let rec private constructed definition (actual: Type) =
        if actual.IsGenericType && actual.GetGenericTypeDefinition() = definition then Some actual
        else
            match actual.BaseType with
            | null -> None
            | parent -> constructed definition parent

    let isShape definition (value: obj) = constructed definition (value.GetType()) |> Option.isSome

    let private field name (value: obj) =
        match value.GetType().GetProperty(name, flags ||| Reflection.BindingFlags.Instance) with
        | null -> invalidOp $"The historical saga wrapper has no '{name}' field."
        | property -> property.GetValue(value) |> Unchecked.nonNull

    let private record typ (fields: (obj | null) array) = FSharpValue.MakeRecord(typ, fields, flags) |> Unchecked.nonNull

    let private union typ tag (fields: (obj | null) array) =
        let case = FSharpType.GetUnionCases(typ, flags) |> Array.find (fun case -> case.Tag = tag)
        FSharpValue.MakeUnion(case, fields, flags) |> Unchecked.nonNull

    let private startingEvent system (value: obj) =
        if not (isShape typedefof<SagaStartingEvent<_>> value) then
            invalidOp "A historical saga starting event has an unsupported shape."
        let event = field "Event" value |> EventUpcasting.Internal.upcastEvent system
        if not (isShape typedefof<Event<_>> event) then
            invalidOp "A historical saga starting event must contain an aggregate event."
        record (typedefof<SagaStartingEvent<_>>.MakeGenericType [| event.GetType() |]) [| event |]

    let private stateType system (typ: Type) =
        match constructed typedefof<SagaBuilder.SagaStateWrapper<_, _>> typ with
        | Some wrapped ->
            let arguments = wrapped.GetGenericArguments()
            typedefof<SagaBuilder.SagaStateWrapper<_, _>>.MakeGenericType
                [| arguments.[0]; EventUpcasting.Internal.targetType system arguments.[1] |]
        | None -> typ

    let private state system declaredType (value: obj) =
        match constructed typedefof<SagaBuilder.SagaStateWrapper<_, _>> declaredType with
        | Some wrapped ->
            let case, fields = FSharpValue.GetUnionFields(value, wrapped, flags)
            let fields: (obj | null) array =
                match case.Name with
                | "Started" -> [| startingEvent system (fields.[0] |> Unchecked.nonNull) |]
                | "NotStarted"
                | "UserDefined" -> fields
                | other -> invalidOp $"Unsupported historical saga state wrapper case '{other}'."
            union (stateType system wrapped) case.Tag fields
        | None -> value

    let private parent system (value: obj) =
        let typ =
            match constructed typedefof<SagaStateWithVersion<_, _>> (value.GetType()) with
            | Some typ -> typ
            | None -> invalidOp "A historical saga snapshot has an unsupported parent shape."
        let arguments = typ.GetGenericArguments()
        let nextStateType = stateType system arguments.[1]
        let original = field "SagaState" value
        let sagaState =
            record (typedefof<SagaState<_, _>>.MakeGenericType [| arguments.[0]; nextStateType |])
                [| field "Data" original; state system arguments.[1] (field "State" original) |]
        record (typedefof<SagaStateWithVersion<_, _>>.MakeGenericType [| arguments.[0]; nextStateType |])
            [| sagaState; field "Version" value; field "StateEnteredAt" value |]

    let upcastStored system (value: obj) =
        let typ = value.GetType()
        match constructed typedefof<SagaStartingEventWrapper<_>> typ with
        | Some wrapper ->
            let case, fields = FSharpValue.GetUnionFields(value, wrapper, flags)
            let event = startingEvent system (fields.[0] |> Unchecked.nonNull)
            let payloadType = (field "Event" event).GetType().GetGenericArguments().[0]
            union (typedefof<SagaStartingEventWrapper<_>>.MakeGenericType [| payloadType |]) case.Tag [| event |]
        | None ->
            match constructed typedefof<SagaEvent<_>> typ with
            | Some eventType ->
                let declaredState = eventType.GetGenericArguments().[0]
                let case, fields = FSharpValue.GetUnionFields(value, eventType, flags)
                if case.Name <> "StateChanged" || fields.Length <> 2 then
                    invalidOp "A historical saga state-change event has an unsupported shape."
                union (typedefof<SagaEvent<_>>.MakeGenericType [| stateType system declaredState |]) case.Tag
                    [| state system declaredState (fields.[0] |> Unchecked.nonNull); fields.[1] |]
            | None ->
                match constructed typedefof<SagaSnapshot<_, _, _>> typ with
                | Some snapshotType ->
                    let arguments = snapshotType.GetGenericArguments()
                    let targetEvent = EventUpcasting.Internal.targetType system arguments.[2]
                    let nextSnapshotType =
                        typedefof<SagaSnapshot<_, _, _>>.MakeGenericType
                            [| arguments.[0]; stateType system arguments.[1]; targetEvent |]
                    let starting = field "StartingEvent" value
                    let starting =
                        if isNull (box starting) then starting
                        else
                            let _, fields = FSharpValue.GetUnionFields(starting, starting.GetType(), flags)
                            let event = startingEvent system (fields.[0] |> Unchecked.nonNull)
                            let optionType =
                                match nextSnapshotType.GetProperty("StartingEvent", flags ||| Reflection.BindingFlags.Instance) with
                                | null -> invalidOp "The saga snapshot has no starting-event field."
                                | property -> property.PropertyType
                            union optionType 1 [| event |]
                    record nextSnapshotType [| parent system (field "Parent" value); starting |]
                | None when isShape typedefof<SagaStateWithVersion<_, _>> value -> parent system value
                | None -> EventUpcasting.Internal.upcastEvent system value

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
      /// The aggregates that sent this incarnation its starting message, which wait for
      /// its readiness. Transient: the persisted event and snapshot contracts stay unchanged.
      Coordinators: Akka.Actor.IActorRef list
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
        ParentSaga<'SagaData, 'State>
            -> option<SagaStarter.SagaStartingEvent<Event<'TEvent>>>
            -> bool
            -> (unit -> unit)
            -> 'State option)
    (applyNewState: SagaState<'SagaData, 'State> -> SagaState<'SagaData, 'State>)
    (wrapper: 'State -> ParentSaga<'SagaData, 'State>)
    body
    innerStateDefaults
    (currentSagaActivityRef: (Activity | null) ref)
    (cleanupOnStop: unit -> unit)
    (abortIsCurrent: int64 -> bool)
    =
    let upcastHistory (message: obj) =
        try
            let converted = ReadUpcasting.upcastStored mailbox.System message
            let requireShape definition (expected: Type) =
                if ReadUpcasting.isShape definition message && not (expected.IsInstanceOfType converted) then
                    invalidOp $"Saga '{mailbox.Self.Path.Name}' recovered {converted.GetType().FullName}, but requires {expected.FullName}. Register the complete originator-event upcast chain. Changing domain saga state or data requires a separate migration."
            requireShape typedefof<SagaStartingEventWrapper<_>> typeof<SagaStartingEventWrapper<'TEvent>>
            requireShape typedefof<SagaEvent<_>> typeof<SagaEvent<'State>>
            requireShape typedefof<SagaSnapshot<_, _, _>> typeof<SagaSnapshot<'SagaData, 'State, 'TEvent>>
            requireShape typedefof<SagaStateWithVersion<_, _>> typeof<SagaStateWithVersion<'SagaData, 'State>>
            converted
        with error ->
            log.LogError(error, "Fatal error upcasting saga history for {Saga}.", mailbox.Self.Path.ToString())
            fatalFailFast currentSagaActivityRef.Value "Process terminated due to saga event-upcast error" error
            failwith "unreachable"

    let rec innerSet (hs: Handshake<'TEvent>) =
        let { StartingEvent = startingEvent
              Subscribed = subscribed
              SubscriptionAcked = subscriptionAcked } =
            hs

        actor {
            let! msg = mailbox.Receive()
            let msg = if mailbox.IsRecovering() then upcastHistory msg else msg

            let signalReady handshake =
                acknowledgeReady
                    (untyped mailbox.Self)
                    handshake.Coordinators
                    handshake.Subscribed
                    handshake.SubscriptionAcked

            let rememberCoordinator () =
                let coordinator = untyped (mailbox.Sender())

                if hs.Coordinators |> List.contains coordinator then hs
                else { hs with Coordinators = coordinator :: hs.Coordinators }

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
            | :? Event<AbortedEvent> when not (abortIsCurrent state.Version) ->
                // The abort answers a recovery check this saga sent before it stored a newer
                // state, so it no longer describes this workflow.
                log.LogInformation("Saga {Saga} ignored an abort that answered an earlier recovery check.", mailbox.Self.Path.ToString())
                return! innerSet hs
            | :? Event<AbortedEvent> ->
                // Mark the state span Error before cleanupOnStop disposes it, so the
                // aborted saga is flagged in the trace rather than ending silently.
                match currentSagaActivityRef.Value with
                | null -> ()
                | act ->
                    act.SetStatus(ActivityStatusCode.Error, "Saga aborted: originator restart detected")
                    |> ignore

                cleanupOnStop ()
                // StopEntity, not PoisonPill: it waits for a save in flight.
                let passivate = Akka.Cluster.Sharding.Passivate(StopEntity)
                log.LogInformation("Aborting")
                mailbox.Parent() <! passivate
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
            | PersistentLifecycleEvent(PersistFailed(error, _, sequenceNr)) ->
                // Akka stops the saga after this callback; remember-entities recovers it from the journal.
                log.LogError(error, "Saga {Saga} could not persist at sequence {SequenceNr}; the saga stops.", mailbox.Self.Path.ToString(), sequenceNr)
                return! innerSet hs
            | PersistentLifecycleEvent(PersistRejected(error, _, sequenceNr)) ->
                // Akka keeps the saga running with this sequence number used, so its next event
                // would leave a journal gap that stops transactional projections. Crash, as a
                // serialization error does.
                log.LogError(error, "The journal rejected saga {Saga}'s event at sequence {SequenceNr}. Terminating the process.", mailbox.Self.Path.ToString(), sequenceNr)
                fatalFailFast currentSagaActivityRef.Value "Process terminated because the journal rejected a saga event" error
                return! innerSet hs
            | PersistentLifecycleEvent(ReplayFailed(error, _)) ->
                log.LogError(error, "Saga {Saga} could not recover from the journal; the saga stops.", mailbox.Self.Path.ToString())
                return! innerSet hs
            | :? Persistence.SaveSnapshotFailure as failure ->
                // A snapshot only shortens replay; the journal still holds the history.
                log.LogWarning(failure.Cause, "Saga {Saga} could not save a snapshot.", mailbox.Self.Path.ToString())
                return! innerSet hs
            // Shard hand-off, or the saga's own passivation. As an ordinary message it waits for
            // any save in flight, unlike PoisonPill; after a hand-off, remember-entities restarts
            // the saga on its next node.
            | :? StopEntity -> return! Stop
            | PersistentLifecycleEvent _
            | :? Persistence.SaveSnapshotSuccess
            | LifecycleEvent _ ->
                // Lifecycle noise must not mutate handshake state (this used to
                // force subscribed=true, masking the real RecoveryCompleted /
                // SubscriptionAcknowledged signals).
                return! innerSet hs
            | SnapshotOffer(snapState: obj) ->
                let snapState = upcastHistory snapState
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
                // Tell the aggregates that sent the starting message that this saga is ready.
                let nextInner =
                    { hs with SubscriptionAcked = true }

                // A recovered terminal state can stop in its side effects. Release
                // a live starter as soon as the persisted wrapper and subscription
                // are ready, before that StopSaga passivates the entity.
                signalReady nextInner

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
                    let newState = applySideEffects state startingEvent hs.Incarnation.IsRecovery (fun () -> signalReady nextInner)

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
                    let newState = applySideEffects state None true (fun () -> signalReady nextInner)

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

                    signalReady nextInner

                    if startingEvent.IsNone then
                        // A live wrapper persist is always a fresh start (replayed
                        // wrappers arrive through the Recovering branch), so this is
                        // never a recovery re-drive. Passing SubscriptionAcked here
                        // spuriously sent ContinueOrAbort when the mediator ack won
                        // the race against the wrapper persist.
                        let newState = applySideEffects state (Some e) false (fun () -> signalReady nextInner)

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
                                    mailbox.Self.Path.Name
                                    |> SagaStarter.Internal.entityIdOf
                                    |> SagaStarter.Internal.toRawGuid)

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

                            let newState = applySideEffects parentState startingEvent false (fun () -> signalReady hs)

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
                let nextInner = rememberCoordinator ()
                return! SagaStartingEventWrapper e |> box |> Persist <@> innerSet nextInner
            | :? (SagaStarter.SagaStartingEvent<Event<'TEvent>>) when subscribed ->
                let nextInner = rememberCoordinator ()
                signalReady nextInner
                return! innerSet nextInner
            | msg when msg.GetType().Name.StartsWith("SagaStartingEvent") ->
                // A starting event whose payload type is not this saga's 'TEvent:
                // the two cases above did not match it. Dropping it silently left
                // the originator's handshake unsatisfied, so it ran to its
                // timeout and fail-fasted the process with a
                // message about the timeout rather than about the real cause — a
                // saga registered against an event type it cannot receive. Name
                // the mismatch, then release the originator: a saga that never
                // runs is a loud misconfiguration, not a reason to kill the host.
                log.LogError(
                    "Saga {Saga} was started with {Actual}, but it only accepts SagaStartingEvent<Event<{Expected}>>. This saga will not run. Check the event type its registration starts on.",
                    mailbox.Self.Path.Name,
                    msg.GetType().Name,
                    typeof<'TEvent>.Name)

                cont (untyped mailbox.Self) [ untyped (mailbox.Sender()) ]
                return! innerSet hs

            | _ ->
                return! body hs msg
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
    // The saga's own entity id: its path name is the shard-escaped form, and every
    // name helper below parses entity ids (see SagaStarter.Internal.entityIdOf).
    let selfEntityId = mailbox.Self.Path.Name |> SagaStarter.Internal.entityIdOf

    let baseCid: CID =
        selfEntityId |> SagaStarter.Internal.toRawGuid
        |> ValueLens.CreateAsResult
        |> Result.value

    let log = mailbox.UntypedContext.GetLogger()
    let loggerFactory = actorApi.LoggerFactory
    let config = actorApi.Configuration
    let logger = loggerFactory.CreateLogger name
    let flowLogger = loggerFactory.CreateLogger Telemetry.MessageFlowCategory
    let flowCid = baseCid |> ValueLens.Value |> ValueLens.Value

    // The saga's own id, stamped as the Sender of every command it issues. A saga
    // name is <originator id>~Saga~<cid>, so it can overrun the field's length limit
    // even though the originator's id was itself legal — which used to throw inside
    // dispatchCommands and take the process down with it. Nothing routes on this
    // field (delivery uses actor refs), so degrade to None and say so once.
    let selfAggregateId: AggregateId option =
        match ValueLens.CreateAsResult selfEntityId with
        | Ok id -> Some id
        | Error _ ->
            log.Warning(
                "Saga name {0} is {1} characters, past the {2}-character limit of a command's Sender field; commands this saga issues will carry no sender. Keep aggregate ids under {3} characters when a saga starts from them.",
                selfEntityId,
                selfEntityId.Length,
                ShortStringMaxLength,
                ShortStringMaxLength - SAGA_Suffix.Length - 36)

            None

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
    // decided by the arm epoch below, not by this cell.
    let armedExpectationRef: (Expectation * DateTime) option ref = ref None
    // Bumped on every arm, and on cleanup, so exactly the most recent reminder is
    // live. An already-scheduled reminder cannot be cancelled reliably (a fired
    // schedule is a no-op to cancel), so staleness has to be decided on receipt.
    let armEpochRef: int64 ref = ref 0L
    // Latest starting event seen by applySideEffects, so the reminder path can
    // dispatch re-sends with the same metadata a state-entry dispatch carries.
    let lastStartingEventRef: option<SagaStarter.SagaStartingEvent<Event<'TEvent>>> ref = ref None
    // The saga version the last ContinueOrAbort of this incarnation answers for: the version
    // after any transition returned with it. An AbortedEvent applies only at that version.
    let continueOrAbortVersionRef: int64 option ref = ref None

    let cleanupOnStop () =
        disposeCurrentActivity ()
        armedExpectationRef.Value <- None
        armEpochRef.Value <- armEpochRef.Value + 1L
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
                    let command = createCommand mailbox selfAggregateId cmd.Command baseCid baseMetadata

                    let unboxx (msg: Command<obj>) =
                        let genericType = typedefof<Command<_>>.MakeGenericType [| baseType |]

                        let actorId: AggregateId option = selfAggregateId

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
                            | Originator -> selfEntityId |> toOriginatorName

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
                    | FactoryAndName { Name = Originator } -> selfEntityId |> toOriginatorName
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
    let armExpectationReminder (exp: Expectation) (entered: DateTime) (delayOverride: TimeSpan option) =
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
                    // must not retry in lockstep against the same shard. Stretch
                    // only (1.0-1.25x): a wake BEFORE its tick re-targets the same
                    // tick on recompute and re-sends twice for one scheduled retry.
                    TimeSpan.FromTicks(int64 (float baseDelay.Ticks * (1.0 + 0.25 * Random.Shared.NextDouble())))
                | FixedInterval _ -> baseDelay

        // Ceil, never truncate: a sub-millisecond floor wakes the reminder just
        // before its tick, which double-sends that tick (fire early, recompute,
        // fire again). At or after the tick, the recompute counts it done.
        let delayMs = max 1L (int64 (ceil delay.TotalMilliseconds))

        // Bump first: from here on, any reminder armed earlier is stale, whether or
        // not its schedule has already fired.
        armEpochRef.Value <- armEpochRef.Value + 1L
        let epoch = armEpochRef.Value

        dispatchCommands
            None
            (ResizeArray())
            [ { TargetActor = Self
                Command = { ArmedEpoch = epoch } :> obj
                DelayInMs = Some(delayMs, $"expectation:{name}:e{epoch}") } ]

    let applySideEffects
        (sagaState: ParentSaga<'SagaData, 'State>)
        (startingEvent: option<SagaStartingEvent<Event<'TEvent>>>)
        recovering
        (continueSaga: unit -> unit)
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

        let asksContinueOrAbort (cmd: ExecuteCommand) =
            let t = cmd.Command.GetType()
            t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<ContinueOrAbort<_>>

        // The answer applies to the state this call leaves the saga in. A NextState returned
        // with the check (the builder's NotStarted -> Started) is persisted before the answer
        // is processed, because persisting stashes incoming messages.
        if cmds |> List.exists asksContinueOrAbort then
            continueOrAbortVersionRef.Value <-
                Some(
                    match transition with
                    | NextState _ -> sagaState.Version + 1L
                    | _ -> sagaState.Version
                )

        dispatchCommands startingEvent selfDelayed cmds

        // Handle ResumeFirstEvent behavior internally when needed
        match transition, recovering with
        | Stay, false ->
            // This handles the old ResumeFirstEvent case
            continueSaga ()
            None
        | Stay, true ->
            // A saga resurrected mid-handshake (persist failure + remember-entities,
            // restart, rebalance) may never have told its originator it is ready, and
            // it has lost the originator's reference. An originator still waiting sends
            // the starting message again, which records the reference; signal here so
            // any recorded originator hears it. A repeated Continue is ignored.
            continueSaga ()
            None
        | StayExpecting exp, _ ->
            // Same handshake as Stay in both directions: a fresh entry signals
            // Continue, and a resurrected saga must re-signal it — skipping that
            // re-creates the originator deadlock documented on the Stay branch.
            continueSaga ()

            (match validateExpectation exp with
             | Some reason ->
                 log.Error("Invalid saga expectation in {0}: {1}. Terminating process to prevent restart loop.", name, reason)

                 fatalFailFast
                     currentSagaActivityRef.Value
                     "Process terminated due to saga error"
                     (InvalidOperationException reason)
             | None ->
                 // Effective entry: the persisted state-entry time. A saga expecting
                 // from its never-persisted initial state entered it with its
                 // journaled starting event, so it anchors at that event's creation
                 // time and recovery keeps the deadline. Only a saga recovered from a
                 // snapshot that carries no starting event anchors at now.
                 let entered =
                     if sagaState.StateEnteredAt <> DateTime.MinValue then
                         sagaState.StateEnteredAt
                     else
                         match startingEvent with
                         | Some started -> started.Event.CreationDate
                         | None -> mailbox.System.Scheduler.Now.UtcDateTime

                 armedExpectationRef.Value <- Some(exp, entered)
                 dispatchCommands startingEvent selfDelayed exp.Resend
                 armExpectationReminder exp entered None)

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
            // StopEntity, not PoisonPill: it waits for a save in flight.
            let passivate = Cluster.Sharding.Passivate(StopEntity)
            mailbox.Parent() <! passivate
            log.Info("{0} Completed", name)

            if messageFlowEnabled flowLogger then
                flowLogger.LogInformation(
                    "Saga {Saga} completed and stopped [cid: {CID}]",
                    mailbox.Self.Path.Name,
                    flowCid)

            None

    let rec set innerStateDefaults (sagaState: ParentSaga<'SagaData, 'State>) =

        // runSaga passes its current handshake with each message. Capturing the handshake
        // from when `set` ran would restore stale readiness flags on every IgnoreEvent or
        // expectation tick, so a re-delivered start would never be answered.
        let body (handshake: Handshake<'TEvent>) (msg: obj) =
            actor {
                match msg, sagaState with
                | (:? ExpectationReminder as reminder), state ->
                    match armedExpectationRef.Value with
                    | Some(exp, entered) when reminder.ArmedEpoch = armEpochRef.Value ->
                        let now = mailbox.System.Scheduler.Now.UtcDateTime
                        let elapsed = now - entered

                        if elapsed < exp.Deadline then
                            // Retry tick: re-send exactly the expectation's commands
                            // (never the state's other side effects) and arm the next
                            // wake. Duplicate delivery is covered by the same
                            // retry-safe contract recovery re-drives already require.
                            dispatchCommands lastStartingEventRef.Value (ResizeArray()) exp.Resend
                            armExpectationReminder exp entered None
                            return! state |> set handshake
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
                                    handleEvent (exhausted :> obj) state.SagaState
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

                                armExpectationReminder exp entered (Some exp.Deadline)
                                return! state |> set handshake
                    | _ ->
                        // Stale: superseded by a later arm (the epoch moved on) or
                        // already cleaned up. Fired schedules are no-ops to cancel,
                        // so staleness has to be decided here, on receipt.
                        return! sagaState |> set handshake
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
                        | IgnoreEvent -> return! sagaState |> set handshake
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
            (fun version ->
                match continueOrAbortVersionRef.Value with
                | Some asked -> asked = version
                | None -> true)

    set
        { StartingEvent = None
          Subscribed = false
          SubscriptionAcked = false
          Coordinators = []
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
    EventUpcasting.Internal.freeze actorApi.System
    let initialState =
        { Version = 0L
          SagaState = initialState
          // Sentinel: no state has been persisted yet. An expectation armed from
          // this initial state anchors at the starting event's creation time.
          StateEnteredAt = DateTime.MinValue }

    entityFactoryFor actorApi.System shardResolver name
     <| propsPersist (
         actorProp initialState name handleEvent applySideEffects apply snapshotPolicy actorApi (typed actorApi.Mediator)
     )
     // Sagas remember entities, which disables idle passivation in Akka.NET: a saga
     // ends at StopSaga or abort, never on a timer.
     <| PassivationPolicy.Default
     <| true
