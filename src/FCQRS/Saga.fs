module Saga

open FCQRS
open Akkling
open Akkling.Persistence
open Akka
open Common
open Actor
open Akka.Cluster.Sharding
open Common.SagaStarter
open Akka.Event
open Microsoft.Extensions.Logging
open Akkling.Cluster.Sharding
open Microsoft.FSharp.Reflection


type SagaState<'SagaData,'State> = { Data: 'SagaData; State: 'State }
type TargetName = Name of string | Originator
type FactoryAndName = { Factory:   obj;Name :TargetName }
type TargetActor=
        | FactoryAndName of FactoryAndName
        | Sender

type ExecuteCommand = { TargetActor: TargetActor; Command : obj;  }
type Effect = 
    | ResumeFirstEvent
    | Stop
    | NoEffect

type NextState = obj option


let toStateChange state = state |> StateChanged |> box |> Persist :> Effect<_> |> Some

let createCommand (mailbox:Eventsourced<_>) (command:'TCommand) cid = {
    CommandDetails = command
    CreationDate = mailbox.System.Scheduler.Now.UtcDateTime
    CorrelationId = cid
    Id = None
}


let actorProp<'SagaData,'TEvent,'Env,'State> (loggerFactory:ILoggerFactory) initialState name handleEvent applySideEffects2  apply (actorApi: IActor)  (mediator: IActorRef<_>) (mailbox: Eventsourced<obj>) =
    let cid = (mailbox.Self.Path.Name |> SagaStarter.toRawGuid)
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
                                | Originator -> cid |> toOriginatorName
                            let factory = factory :?> (string -> IEntityRef<obj>)
                            factory  name
                    
                        | Sender -> mailbox.Sender() 
                targetActor.Tell(finalCommand, mailbox.Self.Underlying :?> IActorRef)
                
            match effect with
            | NoEffect -> 
                newState
            |  ResumeFirstEvent -> 
                SagaStarter.cont mediator; 
                newState
            | Stop  -> 
                let poision = Akka.Cluster.Sharding.Passivate <| Actor.PoisonPill.Instance 
                mailbox.Parent() <! poision
                log.Info("SubscriptionsSaga Completed");
                newState
                
          
    let rec set   (sagaState: SagaState<'SagaData,'State>) =

        let body (msg: obj) =
            actor {
                match msg, sagaState with
                | msg, state ->
                    let state = handleEvent msg state
                    match state with
                    | Some newState ->
                        return! newState
                    | None -> return! sagaState |> set
            }
        let wrapper = fun (s:'State) -> { Data = sagaState.Data; State = s }
        runSaga mailbox logger mediator set  sagaState applySideEffects apply wrapper body

    set initialState

let init (env: _) (actorApi: IActor) initialState handleEvent applySideEffects apply name=
    (AkklingHelpers.entityFactoryFor actorApi.System shardResolver name
        <| propsPersist (actorProp env  initialState name handleEvent applySideEffects  apply  actorApi (typed actorApi.Mediator))
        <| true)
