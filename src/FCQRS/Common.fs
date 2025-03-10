[<System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage>]
module rec FCQRS.Common

open System
open Akkling
open Akkling.Persistence
open Akka.Cluster.Tools.PublishSubscribe
open Akka
open Akkling.Cluster.Sharding
open Akka.Actor
open Akka.Event
open Microsoft.Extensions.Logging
open FCQRS.Model.Data
open Akka.Streams
open SagaStarter
open Microsoft.Extensions.Configuration

type ISerializable = interface end

// Add the new interface for messages with CID
type IMessageWithCID =
    abstract member CID : CID

type Command<'CommandDetails> =
    { CommandDetails: 'CommandDetails
      CreationDate: DateTime
      Id: MessageId option
      Sender: ActorId option
      CorrelationId: CID }
      
    override this.ToString() = sprintf "%A" this

    interface ISerializable
    interface IMessageWithCID with
        member this.CID = this.CorrelationId

type Event<'EventDetails> =
    { EventDetails: 'EventDetails
      CreationDate: DateTime
      Id: MessageId option
      Sender: ActorId option
      CorrelationId: CID
      Version: Version }

    override this.ToString() = sprintf "%A" this

    interface ISerializable
    interface IMessageWithCID with
        member this.CID = this.CorrelationId

type ContinueOrAbort<'EventDetails> = ContinueOrAbort of Event<'EventDetails>    interface ISerializable
type AbortedEvent = AbortedEvent   interface ISerializable

type SagaEvent<'TState> =
    | StateChanged of 'TState
    interface ISerializable
    
type SagaStateWithVersion<'SagaData,'State> = 
        { SagaState : SagaState<'SagaData,'State>; Version: int64; }
        with interface ISerializable

type EventAction<'T> = 
    | PersistEvent of 'T
    | DeferEvent of 'T
    | PublishEvent of Event<'T>
    | IgnoreEvent
    | UnhandledEvent
    | StateChangedEvent of 'T

    type TargetName = Name of string | Originator
    type FactoryAndName = { Factory:   obj;Name :TargetName }
    type TargetActor =
            | FactoryAndName of FactoryAndName
            | ActorRef of obj
            | Sender
            | Self
    
    type ExecuteCommand = { TargetActor: TargetActor; Command : obj;  DelayInMs : int64 option }
    
    type Effect = 
        | ResumeFirstEvent
        | StopActor
        | NoEffect
    
type SagaState<'SagaData,'State> = 
        { Data: 'SagaData; State: 'State }
        with interface ISerializable

[<Interface>]
type IActor =
    abstract Mediator: Akka.Actor.IActorRef
    abstract Materializer: ActorMaterializer
    abstract System: ActorSystem
    abstract SubscribeForCommand: CommandHandler.Command<'a, 'b> -> Async<Common.Event<'b>>
    abstract Stop: unit -> System.Threading.Tasks.Task
    abstract LoggerFactory: ILoggerFactory
    abstract TimeProvider: TimeProvider
    abstract CreateCommandSubscription: (string -> IEntityRef<obj>) -> CID -> ActorId -> 'b -> ('c -> bool) -> Async<Event<'c>>
    abstract InitializeActor: #IConfiguration & #ILoggerFactory -> 'a -> string -> (Command<'c >-> 'a -> EventAction<'b>) -> (Event<'b> -> 'a -> 'a) -> EntityFac<obj>
    abstract InitializeSaga: #IConfiguration & #ILoggerFactory-> SagaState<'SagaState,'State>  -> (obj-> SagaState<'SagaState,'State>-> EventAction<'State>) -> 
        (SagaState<'SagaState,'State> -> option<SagaStartingEvent<Event<'c>>> -> bool -> Effect * option<'State> * ExecuteCommand list) -> 
        (SagaState<'SagaState,'State> -> SagaState<'SagaState,'State>) -> string ->EntityFac<obj>
    abstract InitializeSagaStarter: (obj  -> list<(string -> IEntityRef<obj>) * PrefixConversion * obj>) -> unit

let toEvent (sch: IScheduler) id ci sender version event   =
    { EventDetails = event
      Id = id
      CreationDate = sch.Now.UtcDateTime
      Sender = sender
      CorrelationId = ci
      Version = version }

[<Literal>]
let DEFAULT_SHARD = "default-shard"

[<Literal>]
let SAGA_Suffix = "~Saga~"

[<Literal>]
let CID_Separator = "~"

let shardResolver = fun _ -> DEFAULT_SHARD

type PrefixConversion = PrefixConversion of ((string -> string) option)

module SagaStarter =
    open Microsoft.FSharp.Reflection

    let toOriginatorName (name: string) =
        let index = name.IndexOf(SAGA_Suffix)
        if index > 0 then name.Substring(0, index) else name

    let toRawGuid (name: string) =
        let index = name.LastIndexOf(CID_Separator)
        name.Substring(index + 1).Replace(SAGA_Suffix, "")

    let toCidWithExisting (name: string) (existing: string) =
        let originator = name
        let guid = existing |> toRawGuid
        originator + CID_Separator + guid

    let toCid name =
        let originator = (name |> toOriginatorName)
        let guid = name |> toRawGuid
        originator + CID_Separator + guid

    let cidToSagaName (name: string) = name + SAGA_Suffix
    let isSaga (name: string) = name.Contains(SAGA_Suffix)

    [<Literal>]
    let SagaStarterName = "SagaStarter"

    [<Literal>]
    let SagaStarterPath = "/user/SagaStarter"

    type Command =
        | CheckSagas of obj * originator: Actor.IActorRef * cid: string
        | Continue

    type Event = SagaCheckDone

    type SagaStartingEvent<'T> =
        { Event: 'T }
        interface ISerializable

    type Message =
        | Command of Command
        | Event of Event

    let toCheckSagas (event, originator, cid) =
        (event |> box |> Unchecked.nonNull, originator, cid) |> CheckSagas |> Command

    let toSendMessage mediator (originator: IActorRef<_>) event =
        let cid = toCidWithExisting originator.Path.Name (event.CorrelationId |> ValueLens.Value |> ValueLens.Value)

        let message =
            Send(SagaStarterPath, (event, untyped originator, cid) |> toCheckSagas, true)

        mediator <? message |> Async.RunSynchronously |> ignore
        event |> box |> Unchecked.nonNull

    let publishEvent (logger:ILogger) (mailbox: Actor<_>) (mediator)  event (cid) =
        let sender = mailbox.Sender()
        
        let self = mailbox.Self
        logger.LogDebug("sender: {sender}", sender.Path.ToString())
        logger.LogDebug("Publishing event {event} from {self}", event, self.Path.ToString())

        if sender.Path.Name |> isSaga then

            let originatorName = sender.Path.Name |> toOriginatorName

            if originatorName <> self.Path.Name then
                sender <! event

        mediator <! Publish(self.Path.Name, event)
        mediator <! Publish(self.Path.Name + CID_Separator + cid, event)

    let cont (mediator) =
        mediator <! box (Send(SagaStarterPath, Continue |> Command, true))

    let subscriber (mediator: IActorRef<_>) (mailbox: Eventsourced<_>) =
        let topic = mailbox.Self.Path.Name |> toCid

        mediator <! box (Subscribe(topic, untyped mailbox.Self))

    let (|SubscriptionAcknowledged|_|) (context: Actor<obj>) (msg: obj) : obj option =
        let topic = context.Self.Path.Name |> toCid

        match msg with
        | :? SubscribeAck as s when s.Subscribe.Topic = topic -> Some msg
        | _ -> None

    let unboxx (msg: obj) =
        let genericType =
            typedefof<SagaStartingEvent<_>>.MakeGenericType [| msg.GetType() |]

        FSharpValue.MakeRecord(genericType, [| msg |])

    let actorProp
        (sagaCheck: obj -> (((string -> IEntityRef<obj>) * PrefixConversion * obj) list))
        (mailbox: Actor<_>)
        =
        let rec set (state: Map<string, (IActorRef * string list list)>) =

            let startSaga
                cid
                (originator: IActorRef)
                (list: ((string -> IEntityRef<obj>) * PrefixConversion * obj) list)
                =
                let sender = untyped <| mailbox.Sender()

                let sagas =
                    [ for (factory, prefix, e) in list do
                          let saga =
                              cid
                              |> fun name -> 
                                  match prefix with
                                  | PrefixConversion None -> name
                                  | PrefixConversion(Some f) -> 
                                      originator.Path.Name + SAGA_Suffix + f (name |> toRawGuid)
                              |> factory

                          let msg = unboxx e
                          saga <! msg //box (ShardRegion.StartEntity(saga.EntityId))

                          yield saga.EntityId ]

                let name = originator.Path.Name

                let state =
                    match state.TryFind name with
                    | None -> state.Add(name, (sender, [sagas]))
                    | Some(_, lists) -> state.Remove(name).Add(name, (sender, sagas ::lists ))

                state

            actor {
                match! mailbox.Receive() with
                | Command Continue ->
                    //check if all sagas are started. if so issue SagaCheckDone to originator else keep wait
                    let sender = untyped <| mailbox.Sender()
                    let originName = sender.Path.Name |> toOriginatorName
                    //weird bug cause an NRE with TryGet
                    let matchFound = state.ContainsKey originName

                    if not matchFound then
                        return! set state
                    else
                        let originator, subscribers = state.[originName]
                        let targetList = subscribers |> List.find (fun a -> a |> List.contains sender.Path.Name)
                        let newList = targetList |> List.filter (fun a -> a <> sender.Path.Name)
                        let subscibersWithoutTarget = subscribers |> List.filter (fun a -> a <> targetList)

                        if newList.IsEmpty then
                            originator.Tell(SagaCheckDone, untyped mailbox.Self)
                            return! set <| state.Remove originName
                        else
                            return! set <| state.Remove(originName).Add(originName, (originator, newList::subscibersWithoutTarget))


                | Command(CheckSagas(o, originator, cid)) ->
                    match sagaCheck o with
                    | [] ->
                        mailbox.Sender() <! SagaCheckDone
                        return! set state
                    | list -> return! set <| startSaga cid originator list

                | _ -> return! Unhandled
            }

        set Map.empty

    let init system mediator sagaCheck =
        let sagaStarter = spawn system <| SagaStarterName <| props (actorProp sagaCheck)
        typed mediator <! (sagaStarter |> untyped |> Put)

[<AutoOpen>]
module CommandHandler =

    let (|SubscriptionAcknowledged|_|) (msg: obj) =
        match msg with
        | :? SubscribeAck as s -> Some s
        | _ -> None

    type CommandDetails<'Command, 'Event> =
        { EntityRef: IEntityRef<obj>
          Cmd: Command<'Command>
          Filter: ('Event -> bool) }

    type State<'Command, 'Event> =
        { CommandDetails: CommandDetails<'Command, 'Event>
          Sender: IActorRef }

    type Command<'Command, 'Event> = Execute of CommandDetails<'Command, 'Event>

    let subscribeForCommand<'Command, 'Event> system mediator (command: Command<'Command, 'Event>) =
        let actorProp mediator (mailbox: Actor<obj>) =
            let log = mailbox.UntypedContext.GetLogger()
            let rec set (state: State<'Command, 'Event> option) =
                actor {
                    let! msg = mailbox.Receive()

                    match box msg |>Unchecked.nonNull with
                    | SubscriptionAcknowledged _ ->
                        let cmd = state.Value.CommandDetails.Cmd |> box
                        state.Value.CommandDetails.EntityRef <! cmd

                        return! set state
                    | :? Command<'Command, 'Event> as s ->
                        let sender = mailbox.Sender()

                        let cd =
                            match s with
                            | Execute cd ->
                                mediator
                                <! box (
                                    Subscribe(
                                        (cd.EntityRef.EntityId |> System.Uri.EscapeDataString)
                                        + CID_Separator
                                        + (cd.Cmd.CorrelationId |> ValueLens.Value |> ValueLens.Value),
                                        untyped mailbox.Self
                                    )
                                )
                                cd

                        return!
                            Some
                                { CommandDetails = cd
                                  Sender = untyped sender }
                            |> set

                    | :? (Event<'Event>) as e when e.CorrelationId = state.Value.CommandDetails.Cmd.CorrelationId ->
                        if state.Value.CommandDetails.Filter e.EventDetails then
                            state.Value.Sender.Tell e
                            return! Stop
                        else
                            log.Debug("Ignoring from subscriber message {msg}", msg)
                            return! set state
                    | LifecycleEvent _ -> return! Ignore
                    | _ ->
                        log.Error("Unexpected message {msg}", msg)
                        return! Ignore
                }

            set None

        async {
            let! res = spawnAnonymous system (props (actorProp mediator)) <? box command
            return box res |> nonNull :?> Event<'Event> 
        }
