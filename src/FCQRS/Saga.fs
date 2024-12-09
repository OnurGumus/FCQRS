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

let createCommand (mailbox:Eventsourced<_>) (command:'TCommand) cid = {
    CommandDetails = command
    CreationDate = mailbox.System.Scheduler.Now.UtcDateTime
    CorrelationId = cid
    Id = None
}




let runSaga<'TEvent, 'TState, 'TInnerState>
    (mailbox: Eventsourced<obj>)
    (log: ILogger)
    mediator
    (set: 'TState -> _)
    (state: 'TState)
    (applySideEffects: 'TState -> option<SagaStarter.SagaStartingEvent<'TEvent>> -> bool -> 'TInnerState option)
    (applyNewState: 'TState -> 'TState)
    (wrapper: 'TInnerState -> 'TState)
    body
    =
    let rec innerSet (startingEvent: option<SagaStarter.SagaStartingEvent<_>>, subscribed) =
        actor {
            let! msg = mailbox.Receive()
            log.LogInformation("Saga:{@name} SagaMessage: {MSG}", mailbox.Self.Path.ToString(), msg)

            match msg with
            | :? (SagaStarter.SagaStartingEvent<'TEvent>) when subscribed ->
                SagaStarter.cont mediator
                return! innerSet (startingEvent, subscribed)
            | :? (SagaStarter.SagaStartingEvent<'TEvent>) as e when startingEvent.IsNone ->
                return! innerSet ((Some e), subscribed)
            | :? Persistence.RecoveryCompleted ->
                SagaStarter.subscriber mediator mailbox
                log.LogInformation("Saga RecoveryCompleted")
                return! innerSet (startingEvent, subscribed)
            | Recovering mailbox (:? Common.SagaEvent<'TInnerState> as event) ->
                match event with
                | StateChanged s ->

                    let newState = applyNewState (wrapper s)
                    return! newState |> set

            | PersistentLifecycleEvent _
            | :? Akka.Persistence.SaveSnapshotSuccess
            | LifecycleEvent _ -> return! state |> set
            | SnapshotOffer(snapState: obj) -> return! snapState |> unbox<_> |> set
            | SagaStarter.SubscrptionAcknowledged mailbox _ ->
                // notify saga starter about the subscription completed
                let newState = applySideEffects state startingEvent true

                match newState with
                | Some newState ->
                    return! (newState |> StateChanged |> box |> Persist) <@> innerSet (startingEvent, true)
                | None -> return! state |> set <@> innerSet (startingEvent, true)

            | Deferred mailbox (obj)
            | Persisted mailbox (obj) ->
                match obj with
                | (:? SagaEvent<'TInnerState> as e) ->
                    match e with
                    | StateChanged originalState ->
                        let outerState = wrapper originalState
                        let newSagaState = applyNewState outerState

                        let newState = applySideEffects outerState None false


                        match newState with
                        | Some newState ->
                            return! (newSagaState |> set) <@> (newState |> StateChanged |> box |> Persist)
                        | None -> return! newSagaState |> set
                | other ->
                    log.LogInformation("Unknown event:{@event}, expecting :{@ev}", other.GetType(), typeof<SagaEvent<'TState>>)
                    return! state |> set
            | _ -> return! (body msg)
        }

    innerSet (None, false)

let actorProp<'SagaData,'TEvent,'Env,'State> (loggerFactory:ILoggerFactory) initialState name (handleEvent: _ -> _ -> EventAction<'State>) applySideEffects2  apply (actorApi: IActor)  (mediator: IActorRef<_>) (mailbox: Eventsourced<obj>) =
    let cid:CID = (mailbox.Self.Path.Name |> SagaStarter.toRawGuid) |> ValueLens.CreateAsResult |> Result.value
    let log = mailbox.UntypedContext.GetLogger()
    let logger = loggerFactory.CreateLogger(name)


    let applySideEffects  (sagaState: SagaState<'SagaData,'State>) (startingEvent: option<SagaStartingEvent<'TEvent>>) recovering =
            let effect, newState, (cmds:ExecuteCommand list) = applySideEffects2 sagaState startingEvent recovering
            for cmd in cmds do
                
                let baseType = 
                    let x =  cmd.Command.GetType().BaseType
                    if x = typeof<obj> then
                        cmd.Command.GetType()
                    else
                        x |> Unchecked.nonNull

                let command = createCommand mailbox cmd.Command cid
                let unboxx (msg: Command<obj>) =
                    let genericType =
                        (typedefof<Command<_>>).MakeGenericType([|baseType |])
            
                    FSharpValue.MakeRecord(genericType, [| msg.CommandDetails; msg.CreationDate; msg.Id; msg.CorrelationId |])
                let finalCommand = unboxx command
            
                let targetActor : ICanTell<_> 
                    = match cmd.TargetActor with
                        | FactoryAndName { Factory = factory; Name = n } -> 
                            let name = 
                                match n with
                                | Name n -> n
                                | Originator -> mailbox.Self.Path.Name |> toOriginatorName
                            let factory = factory :?> (string -> IEntityRef<obj>)
                            factory  name
                    
                        | Sender -> mailbox.Sender() 
                targetActor.Tell(finalCommand, mailbox.Self.Underlying :?> Akka.Actor.IActorRef)
                
            match effect with
            | NoEffect -> 
                newState
            |  ResumeFirstEvent -> 
                SagaStarter.cont mediator; 
                newState
            | StopActor  -> 
                let poision = Akka.Cluster.Sharding.Passivate <| Actor.PoisonPill.Instance 
                mailbox.Parent() <! poision
                log.Info("{name} Completed",name);
                newState
                
          
    let rec set   (sagaState: SagaState<'SagaData,'State>) =

        let body (msg: obj) =
            actor {
                match msg, sagaState with
                | msg, state ->
                    let state:EventAction<'State> = handleEvent msg state
                    match state with
                    | StateChangedEvent newState ->
                        let newState = newState |> toStateChange
                        return! newState
                    | IgnoreEvent -> return! sagaState |> set
                    | UnhandledEvent 
                    | PublishEvent _ 
                    | PersistEvent _ 
                    | DeferEvent _ -> return Unhandled
            }
        let wrapper = fun (s:'State) -> { Data = sagaState.Data; State = s }
        runSaga mailbox logger mediator set  sagaState applySideEffects apply wrapper body

    set initialState

let init (env: _) (actorApi: IActor) initialState handleEvent applySideEffects apply name=
    (AkklingHelpers.entityFactoryFor actorApi.System shardResolver name
        <| propsPersist (actorProp env  initialState name handleEvent applySideEffects  apply  actorApi (typed actorApi.Mediator))
        <| true)
