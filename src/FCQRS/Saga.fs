module Saga

open FCQRS
open Akkling
open Akkling.Persistence
open Akka
open Common
open Akka.Cluster.Sharding
open Common.SagaStarter
open Akka.Event
open Microsoft.Extensions.Logging
open Akkling.Cluster.Sharding
open Microsoft.FSharp.Reflection
open FCQRS.Model.Data
open AkklingHelpers

type NextState = obj option

let toStateChange state = state |> StateChanged |> box |> Persist :> Effect<obj> 

let createCommand (mailbox: Eventsourced<_>) (command:'TCommand) cid = {
    CommandDetails = command
    CreationDate = mailbox.System.Scheduler.Now.UtcDateTime
    CorrelationId = cid
    Id = None
}

let runSaga<'TEvent,'SagaData,'State>
    (mailbox: Eventsourced<obj>)
    (log: ILogger)
    (mediator: IActorRef<_>)
    (set: SagaStateWithVersion<'SagaData,'State> -> Effect<obj>)
    (state: SagaStateWithVersion<'SagaData,'State>)
    (applySideEffects: SagaState<'SagaData,'State> -> option<SagaStartingEvent<'TEvent>> -> bool -> 'State option)
    (applyNewState: SagaState<'SagaData,'State> -> SagaState<'SagaData,'State>)
    (wrapper: 'State -> SagaState<'SagaData,'State>)
    (body: obj -> Effect<obj>)
    =
    let rec innerSet (startingEvent: option<SagaStartingEvent<'TEvent>>, subscribed) =
        actor {
            let! msg = mailbox.Receive()
            log.LogInformation("Saga:{@name} SagaMessage: {MSG}", mailbox.Self.Path.ToString(), msg)

            match msg with
            | :? (SagaStartingEvent<'TEvent>) when subscribed ->
                SagaStarter.cont mediator
                return! innerSet (startingEvent, subscribed)

            | :? (SagaStartingEvent<'TEvent>) as e when startingEvent.IsNone ->
                return! innerSet ((Some e), subscribed)

            | :? Persistence.RecoveryCompleted ->
                SagaStarter.subscriber mediator mailbox
                log.LogInformation("Saga RecoveryCompleted")
                return! innerSet (startingEvent, subscribed)

            | Recovering mailbox (:? SagaEvent<'State> as event) ->
                match event with
                | StateChanged s ->
                    let newState = applyNewState (wrapper s)
                    let wrappedState = { state with SagaState  = newState }
                    return! wrappedState |> set

            | PersistentLifecycleEvent _
            | :? Akka.Persistence.SaveSnapshotSuccess
            | LifecycleEvent _ -> return! state |> set

            | SnapshotOffer(snapState: obj) ->
                let snap = unbox<SagaStateWithVersion<'SagaData,'State>> snapState
                return! snap |> set

            | SagaStarter.SubscrptionAcknowledged mailbox _ ->
                let newState = applySideEffects state.SagaState startingEvent true
                match newState with
                | Some st ->
                    return! (st |> StateChanged |> box |> Persist) <@> innerSet (startingEvent, true)
                | None ->
                    return! state |> set <@> innerSet (startingEvent, true)

            | Deferred mailbox (obj)
            | Persisted mailbox (obj) ->
                match obj with
                | :? SagaEvent<'State> as e ->
                    match e with
                    | StateChanged originalState ->
                        let outerState = wrapper originalState
                        let newSagaState = applyNewState outerState
                        let newState = applySideEffects outerState None false
                        match newState with
                        | Some st ->
                            let updatedState = { outerState with State = st }
                            let withVersion = { SagaState = updatedState; Version = state.Version + 1L }
                            if (withVersion.Version >= 30L && withVersion.Version % 30L = 0L) then
                                return! (withVersion |> set) <@> (SaveSnapshot (box withVersion))
                            else
                                return! withVersion |> set
                        | None ->
                            let withVersion = { SagaState = newSagaState; Version = state.Version + 1L }
                            if (withVersion.Version >= 30L && withVersion.Version % 30L = 0L) then
                                // Use 'SaveSnapshot' to trigger a snapshot. The '<@>' operator chains effects.
                                return! (withVersion |> set) <@> (SaveSnapshot (box withVersion))
                            else
                                // No snapshot this time, just return the updated state
                                return! withVersion |> set

                | other ->
                    log.LogInformation("Unknown event:{@event}, expecting: {@ev}", other.GetType(), typeof<SagaEvent<'State>>)
                    return! state |> set

            | _ -> return! (body msg)
        }

    innerSet (None, false)


let actorProp<'SagaData,'State,'TEvent>
    (loggerFactory: ILoggerFactory)
    (initialState: SagaState<'SagaData,'State>)
    (name: string)
    (handleEvent: obj -> SagaState<'SagaData,'State> -> EventAction<'State>)
    (applySideEffects2: SagaState<'SagaData,'State> -> option<SagaStartingEvent<'TEvent>> -> bool -> Effect * option<'State> * list<ExecuteCommand>)
    (apply: SagaState<'SagaData,'State> -> SagaState<'SagaData,'State>)
    (mediator: IActorRef<_>)
    (mailbox: Eventsourced<obj>)
    =
    let cid: CID = (mailbox.Self.Path.Name |> SagaStarter.toRawGuid) |> ValueLens.CreateAsResult |> Result.value
    let log = mailbox.UntypedContext.GetLogger()
    let logger = loggerFactory.CreateLogger(name)

    let applySideEffects (sagaState: SagaState<'SagaData,'State>) (startingEvent: option<SagaStartingEvent<'TEvent>>) recovering =
        let effect, newState, cmds = applySideEffects2 sagaState startingEvent recovering
        for cmd in cmds do
            let baseType = 
                let x = cmd.Command.GetType().BaseType
                if x = typeof<obj> then cmd.Command.GetType() else x |> Unchecked.nonNull

            let command = createCommand mailbox cmd.Command cid
            let unboxx (msg: Command<obj>) =
                let genericType = (typedefof<Command<_>>).MakeGenericType([|baseType|])
                FSharpValue.MakeRecord(genericType, [| msg.CommandDetails; msg.CreationDate; msg.Id; msg.CorrelationId |])

            let finalCommand = unboxx command

            let targetActor: ICanTell<_> =
                match cmd.TargetActor with
                | FactoryAndName { Factory = factory; Name = n } ->
                    let name =
                        match n with
                        | Name n -> n
                        | Originator -> mailbox.Self.Path.Name |> toOriginatorName
                    let factory = factory :?> (string -> IEntityRef<obj>)
                    factory name
                | Sender -> mailbox.Sender()

            targetActor.Tell(finalCommand, mailbox.Self.Underlying :?> Akka.Actor.IActorRef)

        match effect with
        | NoEffect -> newState
        | ResumeFirstEvent ->
            SagaStarter.cont mediator
            newState
        | StopActor ->
            let poison = Akka.Cluster.Sharding.Passivate <| Actor.PoisonPill.Instance
            mailbox.Parent() <! poison
            log.Info("{name} Completed", name)
            newState

    let rec set (sagaState: SagaStateWithVersion<'SagaData,'State>) =
        let body (msg: obj) =
            actor {
                let stateEvent = handleEvent msg sagaState.SagaState
                match stateEvent with
                | StateChangedEvent newState ->
                    let toChange = toStateChange newState
                    return! toChange
                | IgnoreEvent -> return! sagaState |> set
                | UnhandledEvent
                | PublishEvent _
                | PersistEvent _
                | DeferEvent _ -> return Unhandled
            }

        let wrapper (s: 'State) = { Data = sagaState.SagaState.Data; State = s }
        runSaga<'TEvent,'SagaData,'State> mailbox logger mediator set sagaState applySideEffects apply wrapper body

    set { SagaState = initialState; Version = 0L }

let init<'SagaData,'State,'TEvent> 
    (env: ILoggerFactory) 
    (actorApi: IActor) 
    (initialState: SagaState<'SagaData,'State>) 
    (handleEvent: obj -> SagaState<'SagaData,'State> -> EventAction<'State>) 
    (applySideEffects: SagaState<'SagaData,'State> -> option<SagaStartingEvent<'TEvent>> -> bool -> Effect * option<'State> * list<ExecuteCommand>)
    (apply: SagaState<'SagaData,'State> -> SagaState<'SagaData,'State>)
    (name: string) =
    AkklingHelpers.entityFactoryFor actorApi.System shardResolver name
        (propsPersist (actorProp<'SagaData,'State,'TEvent> env initialState name handleEvent applySideEffects apply (typed actorApi.Mediator)))
        true
