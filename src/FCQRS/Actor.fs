[<System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage>]
module rec FCQRS.Actor

open System.Collections.Immutable
open Akka.Streams
open Akka.Persistence.Journal
open Akka.Actor
open Akka.Cluster
open Akka.Cluster.Tools.PublishSubscribe
open Akkling
open Microsoft.Extensions.Configuration
open Common.DynamicConfig
open System.Dynamic
open Akkling.Persistence
open Akka
open Common
open Akka.Event
open AkklingHelpers
open System
open Microsoft.Extensions.Logging
open AkkaTimeProvider
open FCQRS.Model.Data
open Akkling.Cluster.Sharding

type State<'InnerState>={
    Version: int64
    State: 'InnerState
}
    with interface ISerializable
    
type BodyInput<'TEvent> ={
    Message: obj
    State: obj
    PublishEvent:Event<'TEvent> -> unit
    SendToSagaStarter: Event<'TEvent> -> obj
    Mediator: IActorRef<Publish>
    Log : ILogger
}
let runActor<'TEvent, 'TState>
    (logger: ILogger)
    (mailbox: Eventsourced<obj>)
    mediator
    (set: State<'TState> -> _)
    (state: State<'TState>)
    (applyNewState: Event<'TEvent> -> 'TState -> 'TState)
    (body: BodyInput<'TEvent> -> _) : Effect<obj> =

    
    let mediatorS = retype mediator
    let publishEvent event =
        SagaStarter.publishEvent logger mailbox mediator event (event.CorrelationId |> ValueLens.Value |> ValueLens.Value)
    actor {
        let log = logger
        let! msg = mailbox.Receive()


        log.LogInformation("Actor:{@name} Message: {MSG}", mailbox.Self.Path.ToString(), msg)

        match msg with
        | PersistentLifecycleEvent _
        | :? Persistence.SaveSnapshotSuccess
        | LifecycleEvent _ -> return! state |> set

        | SnapshotOffer(snapState: obj) -> return! snapState |> unbox<_> |> set

        // actor level events will come here
        | Persisted mailbox (:? Common.Event<'TEvent> as event) ->
            let version = event.Version
            publishEvent event

            let innerState = applyNewState event (state.State)
            let newState = { Version = version; State = innerState }
            let state = newState

            if (version >= 30L && version % 30L = 0L) then
                return! state |> set <@> SaveSnapshot(state)
            else
                return! state |> set

        | Recovering mailbox (:? Common.Event<'TEvent> as event) ->
            let state = applyNewState event (state.State)
            let newState = { Version = event.Version; State = state }
            return! newState |> set
        | _ ->
                let starter =  SagaStarter.toSendMessage mediatorS mailbox.Self 
                let bodyInput = {
                    Message = msg
                    State = state
                    PublishEvent = publishEvent
                    SendToSagaStarter = starter
                    Mediator = mediator
                    Log = log
                }
                return! (body bodyInput)
    }


let private defaultTag = ImmutableHashSet.Create("default")

type Id = string option
type Version = int64

let  actorProp<'Command,'State,'Event,'Env> (loggerFactory:ILoggerFactory) handleCommand apply  (initialState:'State)  (name:string) (toEvent) (mediator: IActorRef<Publish>) (mailbox: Eventsourced<obj>)  =
    let logger = loggerFactory.CreateLogger(name)
    let rec set (state: State<'State>) =
        let body (bodyInput: BodyInput<'Event>) =
            let msg = bodyInput.Message

            actor {
                match msg, state with
                | :? Persistence.RecoveryCompleted, _ -> return! state |> set
                | :? (Common.Command<'Command>) as msg, _ ->
                    let toEvent = toEvent (msg.Id) (msg.CorrelationId)

                    match handleCommand msg state.State  with
                    | PersistEvent(event) ->
                        return! event |> toEvent (state.Version + 1L) |> bodyInput.SendToSagaStarter |> Persist
                    | DeferEvent( event) ->
                        return! seq {event  |> (toEvent (state.Version))  |> bodyInput.SendToSagaStarter } |> Defer
                    | PublishEvent(event)->
                        event |> bodyInput.SendToSagaStarter |> ignore  
                        return set state
                    | IgnoreEvent -> return set state
                    | StateChangedEvent _ 
                    | UnhandledEvent -> return Unhandled
                | _ ->
                    bodyInput.Log.LogWarning("Unhandled message: {msg}", msg)
                    return Unhandled
            }

        runActor logger mailbox mediator set state (apply:Event<_> -> 'State -> 'State) body
    let initialState = { Version = 0L; State = initialState }
    set  initialState


let init (env: _) initialState name toEvent (actorApi: IActor) handleCommand apply =
    let loggerFactory = env :> ILoggerFactory
    AkklingHelpers.entityFactoryFor actorApi.System shardResolver name
    <| propsPersist (actorProp loggerFactory handleCommand apply initialState  name  toEvent (typed actorApi.Mediator)) 
    <| false


type MyEventAdapter =
    interface IEventAdapter with
        member this.FromJournal(evt: obj, manifest: string) : IEventSequence = EventSequence.Single(evt)
        member this.Manifest(evt: obj) : string = ""
        member this.ToJournal(evt: obj) : obj =( box <| Tagged(evt, defaultTag) ) |> Unchecked.nonNull

    public new() = { }


let createCommandSubscription (actorApi: IActor) factory (cid:CID) (id: string) command filter =
    let actor = factory id

    let commonCommand: Command<_> = {
        CommandDetails = command
        Id = Some(Guid.NewGuid().ToString())
        CreationDate = actorApi.System.Scheduler.Now.UtcDateTime
        CorrelationId = cid
    }

    let e = { Cmd = commonCommand; EntityRef = actor; Filter = filter }

    let ex = Execute e
    ex |> actorApi.SubscribeForCommand

let api (config: IConfiguration) (loggerFactory: ILoggerFactory) =
    let (akkaConfig: ExpandoObject) =
        unbox<_> (config.GetSectionAsDynamic("config:akka"))

    let config = Akka.Configuration.ConfigurationFactory.FromObject akkaConfig

    let system = System.create "cluster-system" config

    Cluster.Get(system).SelfAddress |> Cluster.Get(system).Join

    let mediator = DistributedPubSub.Get(system).Mediator

    let mat = ActorMaterializer.Create(system)

    let subscribeForCommand command =
        Common.CommandHandler.subscribeForCommand system (typed mediator) command

    { new IActor with
        member _.Mediator = mediator
        member _.Materializer = mat
        member _.System = system
        member _.TimeProvider = new AkkaTimeProvider(system)
        member _.LoggerFactory = loggerFactory
        member _.SubscribeForCommand command = subscribeForCommand command
        member _.Stop() = system.Terminate()        
        member this.CreateCommandSubscription factory cid id command filter = 
                createCommandSubscription this factory cid id command filter

        member this.InitializeActor loggerFactory initialState name handleCommand apply = 
                let  toEvent v e =
                    Common.toEvent system.Scheduler v e
                init (loggerFactory) initialState name toEvent this handleCommand apply

        member this.InitializeSaga(env: ILoggerFactory) 
                (initialState: SagaState<'SagaState,'State>) 
                    (handleEvent: obj -> SagaState<'SagaState,'State> -> EventAction<'State>) 
                    (applySideEffects: SagaState<'SagaState,'State> -> SagaStarter.SagaStartingEvent<Event<'c>> option -> bool -> Effect * 'State option * ExecuteCommand list) 
                    (apply: SagaState<'SagaState,'State> -> SagaState<'SagaState,'State>) (name: string): EntityFac<obj> = 
                Saga.init env this initialState  handleEvent  applySideEffects apply name

        member this.InitializeSagStarter   (rules:(obj  -> list<(string -> IEntityRef<obj>) * PrefixConversion * obj>)): unit = 
            SagaStarter.init system mediator  rules
        
    }