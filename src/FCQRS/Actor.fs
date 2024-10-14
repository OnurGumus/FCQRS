[<System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage>]
module FCQRS.Actor

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
    (set: 'TState -> _)
    (state: 'TState)
    (applyNewState: Event<'TEvent> -> 'TState -> 'TState)
    (body: BodyInput<'TEvent> -> _) : Effect<obj> =

    
    let mediatorS = retype mediator
    let publishEvent event =
        SagaStarter.publishEvent logger mailbox mediator event event.CorrelationId
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

            let state = applyNewState event state

            if (version >= 30L && version % 30L = 0L) then
                return! state |> set <@> SaveSnapshot(state)
            else
                return! state |> set

        | Recovering mailbox (:? Common.Event<'TEvent> as event) ->
            let state = applyNewState event state
            return! state |> set
        | _ ->
                let bodyInput = {
                    Message = msg
                    State = state
                    PublishEvent = publishEvent
                    SendToSagaStarter = SagaStarter.toSendMessage mediatorS mailbox.Self
                    Mediator = mediator
                    Log = log
                }
                return! (body bodyInput)
    }

let private defaultTag = ImmutableHashSet.Create("default")

type Tagger =
    interface IWriteEventAdapter with
        member _.Manifest _ = ""
        member _.ToJournal evt = evt //box <| Tagged(evt, defaultTag)

    public new() = { }


type MyEventAdapter =
    interface IEventAdapter with
        member this.FromJournal(evt: obj, manifest: string) : IEventSequence = EventSequence.Single(evt)
        member this.Manifest(evt: obj) : string = ""
        member this.ToJournal(evt: obj) : obj = box <| Tagged(evt, defaultTag)

    public new() = { }

[<Interface>]
type IActor =
    abstract Mediator: Akka.Actor.IActorRef
    abstract Materializer: ActorMaterializer
    abstract System: ActorSystem
    abstract SubscribeForCommand: Common.CommandHandler.Command<'a, 'b> -> Async<Common.Event<'b>>
    abstract Stop: unit -> System.Threading.Tasks.Task
    abstract LoggerFactory: ILoggerFactory
    abstract TimeProvider: TimeProvider

let createCommandSubscription (actorApi: IActor) factory (cidValue) (id: string) command filter =
    let corID = id |> Uri.EscapeDataString |> SagaStarter.toNewCid (cidValue)
    let actor = factory id

    let commonCommand: Command<_> = {
        CommandDetails = command
        Id = Some(Guid.NewGuid().ToString())
        CreationDate = actorApi.System.Scheduler.Now.UtcDateTime
        CorrelationId = corID
    }

    let e = { Cmd = commonCommand; EntityRef = actor; Filter = filter }

    let ex = Execute e
    ex |> actorApi.SubscribeForCommand

let api (config: IConfiguration) (loggerFactory: ILoggerFactory) =
    let (akkaConfig: ExpandoObject) =
        unbox<_> (config.GetSectionAsDynamic("config:akka"))

    let config = Akka.Configuration.ConfigurationFactory.FromObject akkaConfig

    let system = System.create "cluster-system" config
    let logger = loggerFactory.CreateLogger("Actor")


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
        member _.Stop() = system.Terminate() }

         