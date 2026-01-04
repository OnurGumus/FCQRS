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

let private activitySource = new ActivitySource("FCQRS.Saga")

let private toStateChange state =
    state |> StateChanged |> box |> Persist :> Effect<obj>

let private createCommand (mailbox: Eventsourced<_>) (command: 'TCommand) cid metadata =
    { CommandDetails = command
      CreationDate = mailbox.System.Scheduler.Now.UtcDateTime
      CorrelationId = cid
      Id = Guid.CreateVersion7().ToString() |> ValueLens.CreateAsResult |> Result.value
      Sender = mailbox.Self.Path.Name |> ValueLens.CreateAsResult |> Result.value |> Some
      Metadata = metadata }

type private ParentSaga<'SagaData, 'State> = SagaStateWithVersion<'SagaData, 'State>

type private SagaStartingEventWrapper<'TEvent when 'TEvent : not null> =
    | SagaStartingEventWrapper of SagaStartingEvent<Event<'TEvent>>
    interface ISerializable

let private runSaga<'TEvent, 'SagaData, 'State when 'TEvent : not null and 'State : not null>
    snapshotVersionCount
    (mailbox: Eventsourced<obj>)
    (log: ILogger)
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
    =
    let rec innerSet (startingEvent: option<SagaStarter.SagaStartingEvent<Event<'TEvent>>>, subscribed, subscriptionAcked) =
        let innerSetValue = startingEvent, subscribed, subscriptionAcked

        actor {
            let! msg = mailbox.Receive()

            // Try to parse CID as W3C traceparent to recover trace context for OTEL
            // Handle both IMessage and SagaStartingEvent (which wraps an IMessage)
            let tryExtractCid (m: obj) =
                match m with
                | :? FCQRS.Model.Data.IMessage as im ->
                    Some (im.CID |> ValueLens.Value |> ValueLens.Value)
                | :? SagaStarter.SagaStartingEvent<FCQRS.Model.Data.IMessage> as se ->
                    Some (se.Event.CID |> ValueLens.Value |> ValueLens.Value)
                | _ ->
                    // Try to get Event property via reflection for generic SagaStartingEvent
                    let t = m.GetType()
                    if t.Name.StartsWith("SagaStartingEvent") then
                        match t.GetProperty("Event") with
                        | null -> None
                        | eventProp ->
                            match eventProp.GetValue(m) with
                            | :? FCQRS.Model.Data.IMessage as innerMsg ->
                                Some (innerMsg.CID |> ValueLens.Value |> ValueLens.Value)
                            | _ -> None
                    else None

            // Extract CID from the starting event (which has the original trace context)
            let getCidFromStartingEvent () =
                match startingEvent with
                | Some se ->
                    match box se with
                    | null -> None
                    | boxed -> tryExtractCid boxed
                | None -> None

            // Helper to create and emit a state change activity (only for state transitions)
            // Activity is kept alive until next state change, so sub-activities become children
            let emitStateChangeActivity (newState: 'State) =
                // Dispose previous activity if exists
                match currentSagaActivityRef.Value with
                | null -> ()
                | prev ->
                    prev.Dispose()
                    currentSagaActivityRef.Value <- null

                match getCidFromStartingEvent () with
                | Some cid ->
                    let mutable parentContext = Unchecked.defaultof<ActivityContext>
                    if ActivityContext.TryParse(cid, null, &parentContext) then
                        let sagaName = mailbox.Self.Path.Name
                        // Recursively extract innermost union case name
                        // This unwraps SagaStateWrapper.UserDefined to get actual user state
                        let rec getUnionCaseName (obj: obj) =
                            let t = obj.GetType()
                            if FSharpType.IsUnion(t) then
                                let case, fields = FSharpValue.GetUnionFields(obj, t)
                                // If case is "UserDefined" with one field, unwrap it
                                if case.Name = "UserDefined" && fields.Length = 1 then
                                    match fields.[0] with
                                    | null -> case.Name
                                    | field -> getUnionCaseName field
                                else
                                    case.Name
                            else
                                sprintf "%A" obj
                        let stateStr =
                            match box newState with
                            | null -> "null"
                            | boxed -> getUnionCaseName boxed
                        // Create activity and keep it alive (don't use 'use')
                        // This allows sub-activities (commands, events) to become children
                        match activitySource.StartActivity($"Saga:{stateStr}", ActivityKind.Internal, parentContext) with
                        | null -> ()
                        | act ->
                            act.SetTag("cid", cid) |> ignore
                            act.SetTag("saga.id", sagaName) |> ignore
                            act.SetTag("saga.state", stateStr) |> ignore
                            currentSagaActivityRef.Value <- act
                | None -> ()

            match msg with
            | :? Event<AbortedEvent> ->
                let poision = Akka.Cluster.Sharding.Passivate <| Actor.PoisonPill.Instance
                log.LogInformation("Aborting")
                mailbox.Parent() <! poision
                return! innerSet (startingEvent, subscribed, subscriptionAcked)

            | :? Persistence.RecoveryCompleted ->
                subscriber mediator mailbox
                log.LogInformation("Saga RecoveryCompleted")
                return! innerSet (startingEvent, subscribed, subscriptionAcked)
            | Recovering mailbox (:? SagaStartingEventWrapper<'TEvent> as SagaStartingEventWrapper event) ->
                return! innerSet (Some event, true, subscriptionAcked)
            | Recovering mailbox (:? SagaEvent<'State> as event) ->
                match event with
                | StateChanged s ->
                    try
                        let newState = applyNewState (wrapper s).SagaState
                        let newState = { state with SagaState = newState }
                        return! newState |> set innerSetValue
                    with ex ->
                        log.LogError(ex, "Fatal error during saga recovery for {0}. Terminating process to prevent restart loop.", mailbox.Self.Path.ToString())
                        System.Environment.FailFast "Process terminated due to saga error"
                        return! innerSet (startingEvent, subscribed, subscriptionAcked)

            | PersistentLifecycleEvent _
            | :? Persistence.SaveSnapshotSuccess
            | LifecycleEvent _ ->
                return! innerSet (startingEvent, true, subscriptionAcked)
            | SnapshotOffer(snapState: obj) ->
                return! snapState |> unbox<_> |> set innerSetValue
            | SubscriptionAcknowledged mailbox _ ->
                // notify saga starter about the subscription completed
                let nextInner = startingEvent, true, true

                match startingEvent with
                | Some _ ->
                    let newState = applySideEffects state startingEvent true

                    match newState with
                    | Some newState ->
                        // Activity will be emitted when state is persisted (in Persisted branch)
                        return! newState |> StateChanged |> box |> Persist <@> innerSet nextInner
                    | None ->
                        return! state |> set innerSetValue <@> innerSet nextInner
                | None ->
                    // Wait for starting event before applying side effects.
                    return! state |> set innerSetValue <@> innerSet nextInner

            | Deferred mailbox obj
            | Persisted mailbox obj ->
                match obj with
                | :? SagaStartingEventWrapper<'TEvent> as SagaStartingEventWrapper e ->
                    let nextInner = Some e, true, subscriptionAcked

                    if startingEvent.IsNone then
                        let newState = applySideEffects state (Some e) subscriptionAcked

                        match newState with
                        | Some newState ->
                            // Activity will be emitted when state is persisted (in Persisted branch)
                            return! newState |> StateChanged |> box |> Persist <@> innerSet nextInner
                        | None ->
                            return! state |> set innerSetValue <@> innerSet nextInner
                    else
                        return! innerSet nextInner
                | :? SagaEvent<'State> as e ->
                    match e with
                    | StateChanged originalState ->
                        try
                            // Emit activity for the persisted state change
                            emitStateChangeActivity originalState
                            let outerState = wrapper originalState

                            let newSagaState = applyNewState outerState.SagaState

                            let parentState =
                                { outerState with
                                    SagaState = newSagaState }

                            let newState = applySideEffects parentState startingEvent false
                            let version = parentState.Version + 1L

                            match newState with
                            | Some newState ->
                                let newSagaState: ParentSaga<_, _> =
                                    let newInnerState = parentState.SagaState
                                    let newInnerState = { newInnerState with State = newState }

                                    { parentState with
                                        SagaState = newInnerState }

                                // Note: Activity already emitted for originalState above
                                // The newState will trigger another Persisted event which will emit its activity

                                if version >= snapshotVersionCount && version % snapshotVersionCount = 0L then
                                    return! parentState |> set innerSetValue <@> SaveSnapshot parentState
                                else
                                    return!
                                        newSagaState |> set innerSetValue
                                        <@> (newState |> StateChanged |> box |> Persist)
                            | None ->
                                if version >= snapshotVersionCount && version % snapshotVersionCount = 0L then
                                    return! parentState |> set innerSetValue <@> SaveSnapshot parentState
                                else
                                    return! parentState |> set innerSetValue
                        with ex ->
                            log.LogError(ex, "Fatal error during saga persisted event handling for {0}. Terminating process to prevent restart loop.", mailbox.Self.Path.ToString())
                            System.Environment.Exit(-1)
                            return! state |> set innerSetValue


                | other ->
                    log.LogInformation(
                        "Unknown event:{@event}, expecting :{@ev}",
                        other.GetType(),
                        typeof<SagaEvent<'State>>
                    )

                    return! state |> set innerSetValue

            | :? (SagaStarter.SagaStartingEvent<Event<'TEvent>>) as e when startingEvent.IsNone ->
                return! SagaStartingEventWrapper e |> box |> Persist
            | :? (SagaStarter.SagaStartingEvent<Event<'TEvent>>) when subscribed ->
                cont mediator
                return! innerSet (startingEvent, subscribed, subscriptionAcked)
            | msg when msg.GetType().Name.StartsWith("SagaStartingEvent") ->
                return! innerSet (startingEvent, subscribed, subscriptionAcked)

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

    // Ref cell to hold the current saga state activity (kept alive across iterations)
    // This is at actorProp level so both runSaga and applySideEffects can access it
    let currentSagaActivityRef: (Activity | null) ref = ref null
    let snapshotVersionCount =
        let s: string | null = config["config:akka:persistence:snapshot-version-count"]

        match s |> System.Int32.TryParse with
        | true, v -> v
        | _ -> 30

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
                System.Environment.FailFast "Process terminated due to saga error"

                failwith "Process terminated due to saga error" // This line will never execute but satisfies the compiler

        for cmd in cmds do
            try
                let createFinalCommand cmd =
                    let baseType =
                        let x = cmd.Command.GetType().BaseType

                        if x = typeof<obj> then
                            cmd.Command.GetType()
                        else
                            x |> Unchecked.nonNull

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

                    | Sender -> mailbox.Sender(), createFinalCommand cmd
                    | ActorRef actor -> actor :?> ICanTell<_>, cmd.Command
                    | Self -> mailbox.Self, cmd

                match cmd.DelayInMs with
                | Some (delayValue, name) ->
                    let currentScheduler = mailbox.System.Scheduler 
                    let messageToSchedule = finalCommand
                    let scheduleAtDelay = System.TimeSpan.FromMilliseconds delayValue
                    
                    let untypedSender = mailbox.Self.Underlying :?> Akka.Actor.IActorRef
                    let untypedReceiver = targetActor.Underlying 

                    match currentScheduler with
                    | :? FCQRS.Scheduler.ObservingScheduler as obs ->
                        let taskNameForScheduler = name
                        
                        // IMPORTANT: Assumes FCQRS.ObservingScheduler.ObservingScheduler now has a method like:
                        // member this.ScheduleTellOnceWithName(delay, receiver, message, sender, taskName) : ICancelable
                        obs.ScheduleTellOnce(Some taskNameForScheduler, scheduleAtDelay, untypedReceiver, messageToSchedule, untypedSender)
                        |> ignore

                    | sch -> // Fallback for any other IScheduler type
                        sch.ScheduleTellOnce(scheduleAtDelay, untypedReceiver, messageToSchedule, untypedSender)
                        |> ignore

                | None ->
                    targetActor <! finalCommand
            with ex ->
                log.Error(ex, "Fatal error in saga command processing for {0}. Terminating process to prevent restart loop.", name)
                System.Environment.FailFast "Process terminated due to saga error"

        // Handle ResumeFirstEvent behavior internally when needed
        match transition, recovering with
        | Stay, false ->
            // This handles the old ResumeFirstEvent case
            cont mediator
            None
        | Stay, _ -> None
        | NextState newState, _ -> 
            Some newState
        | StopSaga, _ ->
            let poision = Cluster.Sharding.Passivate <| Actor.PoisonPill.Instance
            mailbox.Parent() <! poision
            log.Info("{0} Completed", name)
            None

    let rec set innerStateDefaults (sagaState: ParentSaga<'SagaData, 'State>) =

        let body (msg: obj) =
            actor {
                match msg, sagaState with
                | msg, state ->
                    try
                        let state: EventAction<'State> = handleEvent msg state.SagaState

                        match state with
                        | StateChangedEvent newState ->
                            let newState = newState |> toStateChange
                            return! newState
                        | IgnoreEvent -> return! sagaState |> set innerStateDefaults
                        | Stash _
                        | Unstash _
                        | UnstashAll _
                        | UnhandledEvent
                        | PublishEvent _
                        | PersistEvent _
                        | DeferEvent _ -> return Unhandled
                    with ex ->
                        log.Error(ex, "Fatal error in saga handleEvent for {0}. Terminating process to prevent restart loop.", name)
                        System.Environment.FailFast "Process terminated due to saga error"
                        return Unhandled // This line will never execute but satisfies the compiler
            }

        let wrapper =
            fun (s: 'State) ->
                { sagaState with
                    SagaState =
                        { Data = sagaState.SagaState.Data
                          State = s } }

        runSaga
            snapshotVersionCount
            mailbox
            logger
            mediator
            set
            sagaState
            applySideEffects
            apply
            wrapper
            body
            innerStateDefaults
            currentSagaActivityRef

    set (None, false, false) initialState

let init<'SagaData, 'State, 'TEvent when 'TEvent : not null and 'State : not null>
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
    =
    let initialState =
        { Version = 0L
          SagaState = initialState }

    entityFactoryFor actorApi.System shardResolver name
     <| propsPersist (
         actorProp initialState name handleEvent applySideEffects apply actorApi (typed actorApi.Mediator)
     )
     <| true
