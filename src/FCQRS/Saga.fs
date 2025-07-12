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

type private SagaStartingEventWrapper<'TEvent> =
    | SagaStartingEventWrapper of SagaStartingEvent<'TEvent>
    interface ISerializable

let private runSaga<'TEvent, 'SagaData, 'State>
    snapshotVersionCount
    (mailbox: Eventsourced<obj>)
    (log: ILogger)
    mediator
    (set: _ -> ParentSaga<'SagaData, 'State> -> _)
    (state: ParentSaga<'SagaData, 'State>)
    (applySideEffects:
        ParentSaga<'SagaData, 'State> -> option<SagaStarter.SagaStartingEvent<'TEvent>> -> bool -> 'State option)
    (applyNewState: SagaState<'SagaData, 'State> -> SagaState<'SagaData, 'State>)
    (wrapper: 'State -> ParentSaga<'SagaData, 'State>)
    body
    innerStateDefaults
    =
    let rec innerSet (startingEvent: option<SagaStarter.SagaStartingEvent<_>>, subscribed) =
        let innerSetValue = startingEvent, subscribed

        actor {
            let! msg = mailbox.Receive()
            log.LogInformation("Saga:{@name} SagaMessage: {MSG}", mailbox.Self.Path.ToString(), msg)

            match msg with
            | :? Event<AbortedEvent> ->
                let poision = Akka.Cluster.Sharding.Passivate <| Actor.PoisonPill.Instance
                log.LogInformation("Aborting")
                mailbox.Parent() <! poision
                return! innerSet (startingEvent, subscribed)

            | :? Persistence.RecoveryCompleted ->
                subscriber mediator mailbox
                log.LogInformation "Saga RecoveryCompleted"
                return! innerSet (startingEvent, subscribed)
            | Recovering mailbox (:? SagaStartingEventWrapper<'TEvent> as SagaStartingEventWrapper event) ->
                return! innerSet (Some event, true)
            | Recovering mailbox (:? SagaEvent<'State> as event) ->
                match event with
                | StateChanged s ->
                    try
                        let newState = applyNewState (wrapper s).SagaState
                        let newState = { state with SagaState = newState }
                        return! newState |> set innerSetValue //<@> innerSet (startingEvent, true)
                    with ex ->
                        log.LogError(ex, "Fatal error during saga recovery for {0}. Terminating process to prevent restart loop.", mailbox.Self.Path.ToString())
                        System.Environment.FailFast "Process terminated due to saga error"
                        return! innerSet (startingEvent, subscribed)

            | PersistentLifecycleEvent _
            | :? Persistence.SaveSnapshotSuccess
            | LifecycleEvent _ -> return! innerSet (startingEvent, true)
            | SnapshotOffer(snapState: obj) -> return! snapState |> unbox<_> |> set innerSetValue
            | SubscriptionAcknowledged mailbox _ -> 
                // notify saga starter about the subscription completed
                let newState = applySideEffects state startingEvent true

                match newState with
                | Some newState ->
                    return! newState |> StateChanged |> box |> Persist <@> innerSet (startingEvent, true)
                | None -> return! state |> set innerSetValue <@> innerSet (startingEvent, true)

            | Deferred mailbox obj
            | Persisted mailbox obj ->
                match obj with
                | :? SagaStartingEventWrapper<'TEvent> as SagaStartingEventWrapper e -> return innerSet (Some e, true)
                | :? SagaEvent<'State> as e ->
                    match e with
                    | StateChanged originalState ->
                        try
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

            | :? (SagaStarter.SagaStartingEvent<'TEvent>) as e when startingEvent.IsNone ->
                return! SagaStartingEventWrapper e |> box |> Persist
            | :? (SagaStarter.SagaStartingEvent<'TEvent>) when subscribed ->
                cont mediator
                return! innerSet (startingEvent, subscribed)

            | _ -> return! body msg
        }

    innerSet innerStateDefaults

let private actorProp
    env
    initialState
    name
    (handleEvent: obj -> SagaState<'SagaData, 'State> -> EventAction<'State>)
    (applySideEffects2: SagaState<'SagaData, 'State> -> _ -> _)
    (apply: SagaState<'SagaData, 'State> -> SagaState<'SagaData, 'State>)
    (actorApi: IActor)
    (mediator: IActorRef<_>)
    (mailbox: Eventsourced<obj>)
    =
    let cid: CID =
        mailbox.Self.Path.Name |> SagaStarter.Internal.toRawGuid
        |> ValueLens.CreateAsResult
        |> Result.value

    let log = mailbox.UntypedContext.GetLogger()
    let loggerFactory = (env :> ILoggerFactoryWrapper).LoggerFactory
    let config = (env :> IConfigurationWrapper).Configuration
    let logger = loggerFactory.CreateLogger name

    let snapshotVersionCount =
        let s: string | null = config["config:akka:persistence:snapshot-version-count"]

        match s |> System.Int32.TryParse with
        | true, v -> v
        | _ -> 30

    let applySideEffects
        (sagaState: ParentSaga<'SagaData, 'State>)
        (startingEvent: option<SagaStartingEvent<'TEvent>>)
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

                    let metadata = 
                        match startingEvent with
                        | Some se -> 
                            match box se.Event with
                            | :? FCQRS.Model.Data.IMessage as msg -> 
                                printfn "🔍 Saga extracting metadata from startingEvent parameter: %A" msg.Metadata
                                msg.Metadata
                            | _ -> 
                                printfn "🔍 Saga starting event is not IMessage"
                                Map.empty
                        | None -> 
                            printfn "🔍 Saga startingEvent parameter is None - need to fix initSaga to pass it correctly"
                            Map.empty
                    printfn "🔍 Saga creating command with metadata: %A" metadata
                    let command = createCommand mailbox cmd.Command cid metadata

                    let unboxx (msg: Command<obj>) =
                        let genericType = typedefof<Command<_>>.MakeGenericType [| baseType |]

                        let actorId: ActorId option =
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
                        
                        log.Debug("SAGA_DEBUG: Using FCQRS.ObservingScheduler. Calling ScheduleTellOnceWithName with task name: {taskName}", taskNameForScheduler)
                        // IMPORTANT: Assumes FCQRS.ObservingScheduler.ObservingScheduler now has a method like:
                        // member this.ScheduleTellOnceWithName(delay, receiver, message, sender, taskName) : ICancelable
                        obs.ScheduleTellOnce(Some taskNameForScheduler, scheduleAtDelay, untypedReceiver, messageToSchedule, untypedSender)
                        |> ignore

                    | sch -> // Fallback for any other IScheduler type
                        log.Debug("SAGA_DEBUG: Using standard IScheduler. Calling standard ScheduleTellOnce.")
                        sch.ScheduleTellOnce(scheduleAtDelay, untypedReceiver, messageToSchedule, untypedSender)
                        |> ignore

                | None -> targetActor <! finalCommand
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

    set (None, false) initialState

let init
    env
    (actorApi: IActor)
    (initialState: SagaState<_, _>)
    (handleEvent: obj -> SagaState<'SagaData, 'State> -> _)
    (applySideEffects: SagaState<'SagaData, 'State> -> _ -> _)
    (apply: SagaState<'SagaData, 'State> -> SagaState<'SagaData, 'State>)
    name
    =
    let initialState =
        { Version = 0L
          SagaState = initialState }

    entityFactoryFor actorApi.System shardResolver name
     <| propsPersist (
         actorProp env initialState name handleEvent applySideEffects apply actorApi (typed actorApi.Mediator)
     )
     <| true

