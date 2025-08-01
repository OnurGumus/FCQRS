/// <summary>
///  Contains common types like Events and Commands
/// </summary>
///
/// <namespacedoc>
///   <summary>Functionality for Write Side.</summary>
/// </namespacedoc>
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
open SagaStarter // Contains internal types, careful exposure
open Microsoft.Extensions.Configuration

/// Marker interface for types that can be serialized by Akka.NET.
type ISerializable = interface end

type ILoggerFactoryWrapper =
    abstract LoggerFactory: ILoggerFactory

type IConfigurationWrapper =
    abstract Configuration: IConfiguration


/// Represents a command to be processed by an aggregate actor.
/// <typeparam name="'CommandDetails">The specific type of the command payload.</typeparam>
type Command<'CommandDetails> =
    { /// The specific details or payload of the command.
      CommandDetails: 'CommandDetails
      /// The timestamp when the command was created.
      CreationDate: DateTime
      /// A unique identifier for the message.
      Id: MessageId
      /// An optional identifier for the actor that sent the command.
      Sender: ActorId option
      /// The correlation ID used to track the command through the system.
      CorrelationId: CID
      /// Metadata associated with the command.
      Metadata: Map<string, string> }

    override this.ToString() = sprintf "%A" this

    interface ISerializable
    interface FCQRS.Model.Data.IMessage with
        member this.CID = this.CorrelationId
        member this.Id = this.Id
        member this.CreationDate = this.CreationDate
        member this.Sender = this.Sender
        member this.Metadata = this.Metadata

/// Represents an event generated by an aggregate actor as a result of processing a command.
/// <typeparam name="'EventDetails">The specific type of the event payload.</typeparam>
type Event<'EventDetails> =
    { /// The specific details or payload of the event.
      EventDetails: 'EventDetails
      /// The timestamp when the event was created.
      CreationDate: DateTime
      /// A unique identifier for the message.
      Id: MessageId
      /// An optional identifier for the actor that generated the event.
      Sender: ActorId option
      /// The correlation ID linking the event back to the originating command.
      CorrelationId: CID
      /// The version number of the aggregate after this event was applied.
      Version: Version
      /// Metadata associated with the event.
      Metadata: Map<string, string> }

    override this.ToString() = sprintf "%A" this

    interface ISerializable
    interface FCQRS.Model.Data.IMessage with
        member this.CID = this.CorrelationId
        member this.Id = this.Id
        member this.CreationDate = this.CreationDate
        member this.Sender = this.Sender
        member this.Metadata = this.Metadata

[<Literal>]
let DEFAULT_SHARD = "default-shard"
// Internal types related to saga flow control

type  ContinueOrAbort<'EventDetails> = ContinueOrAbort of Event<'EventDetails>    interface ISerializable
type  AbortedEvent = AbortedEvent   interface ISerializable

[<AutoOpen>]
module internal Internal =
    type  SagaEvent<'TState> =
        | StateChanged of 'TState
        interface ISerializable


    type SagaStateWithVersion<'SagaData,'State> =
            { SagaState : SagaState<'SagaData,'State>; Version: int64; }
            with interface ISerializable
    
    // Internal constants for saga naming conventions
    [<Literal>]
    let  SAGA_Suffix = "~Saga~"
    [<Literal>]
    let  CID_Separator = "~"
    
    // Internal default shard resolver
    let  shardResolver = fun _ -> DEFAULT_SHARD
    let  toEvent (sch: IScheduler) (id: MessageId option) ci sender version metadata event   =
        let messageId = 
            match id with
            | Some existingId -> existingId
            | None -> 
                Guid.CreateVersion7().ToString() 
                |> ValueLens.CreateAsResult 
                |> Result.value
        { EventDetails = event
          Id = messageId
          CreationDate = sch.Now.UtcDateTime
          Sender = sender
          CorrelationId = ci
          Version = version
          Metadata = metadata }
    

/// Defines the possible actions an aggregate or saga actor can take after processing a command or event.
/// <typeparam name="'T">The type of the event payload associated with the action (e.g., for PersistEvent).</typeparam>
type EventAction<'T> =
    /// Persist the event to the journal. The actor's state will be updated using the event handler *after* persistence succeeds.
    | PersistEvent of 'T
    /// Defer the event. It will be stashed and processed later, potentially after other events.
    | DeferEvent of 'T
    /// Publish the event immediately to the mediator without persisting it. The actor's state is not updated.
    | PublishEvent of Event<'T>
    /// Ignore the event or command completely. No persistence, publishing, or state update occurs.
    | IgnoreEvent
    /// Indicate that the command or event could not be handled in the current state.
    | UnhandledEvent
    /// Indicate that the state of a saga has changed (used internally by sagas for persistence).
    | StateChangedEvent of 'T
    | Stash of EventAction<'T>
    | Unstash of EventAction<'T>
    | UnstashAll of EventAction<'T>

/// Represents the name identifying a target actor for a command, typically used within sagas.
type TargetName =
    /// Identify the target by its string name (entity ID).
    | Name of string
    /// Identify the target as the originator actor of the current saga process.
    | Originator

/// Represents the information needed to locate or create a target actor, typically used within sagas.
type FactoryAndName =
    { /// The factory function (or entity ref creator) used to potentially create the actor.
      Factory:   obj // Typically (string -> IEntityRef<obj>)
      /// The name identifier for the target actor.
      Name :TargetName }

/// Represents the target of a command execution triggered by a saga.
type TargetActor =
    /// Specifies the target using a factory function and name.
    | FactoryAndName of FactoryAndName
    /// Specifies the target using its direct IActorRef (usually boxed as obj).
    | ActorRef of obj // Typically IActorRef
    /// Specifies the target as the original sender of the message that triggered the current saga step.
    | Sender
    /// Specifies the target as the current saga actor itself.
    | Self

/// Represents a command to be executed, often scheduled or triggered by a saga.
type ExecuteCommand =
    { /// The target actor for the command.
      TargetActor: TargetActor
      /// The command message to send (boxed).
      Command : obj
      /// An optional delay in milliseconds before sending the command.
      DelayInMs : (int64 * string) option }

/// Represents the next state transition for a saga after processing an event or timeout.
type SagaTransition<'State> =
    /// The saga should stop and terminate
    | StopSaga
    /// The saga should stay in current state without changes
    | Stay
    /// The saga should transition to a new state
    | NextState of 'State

/// Represents the state of a saga instance.
/// <typeparam name="'SagaData">The type of the custom data held by the saga.</typeparam>
/// <typeparam name="'State">The type representing the saga's current state machine state (e.g., an enum or DU).</typeparam>
type SagaState<'SagaData,'State> =
    { /// The custom data associated with this saga instance.
      Data: 'SagaData
      /// The current state machine state of the saga.
      State: 'State }
    with interface ISerializable

/// Defines the core functionalities and context provided by the FCQRS environment to actors.
/// This interface provides access to essential Akka.NET services and FCQRS initialization methods.
[<Interface>]
type IActor =
    /// Gets the reference to the distributed pub/sub mediator actor.
    abstract Mediator: Akka.Actor.IActorRef
    /// Gets the Akka Streams materializer.
    abstract Materializer: ActorMaterializer
    /// Gets the hosting ActorSystem.
    abstract System: ActorSystem
    /// Subscribes to the result of a command sent to another actor.
    abstract SubscribeForCommand: CommandHandler.Command<'a, 'b> -> Async<Common.Event<'b>>
    /// Stops the actor system gracefully.
    abstract Stop: unit -> System.Threading.Tasks.Task
    /// Gets the logger factory.
    abstract LoggerFactory: ILoggerFactory
    /// Gets the time provider.
    abstract TimeProvider: TimeProvider
    /// Creates a command subscription to wait for a specific event from a target actor.
    /// Sends the command and asynchronously returns the first matching event received.
    /// <param name="factory">Entity factory function for the target actor type.</param>
    /// <param name="cid">Correlation ID for tracking.</param>
    /// <param name="id">Entity ID of the target actor.</param>
    /// <param name="command">The command payload to send.</param>
    /// <param name="filter">A predicate function to select the desired event.</param>
    /// <param name="metadata">Optional metadata to include with the command.</param>
    /// <returns>An async computation yielding the target event.</returns>
    abstract CreateCommandSubscription: (string -> IEntityRef<obj>) -> CID -> ActorId -> 'b -> ('c -> bool) -> Map<string, string> option -> Async<Event<'c>>
    /// Initializes a sharded, persistent aggregate actor.
    /// <param name="cfg">Environment configuration (IConfiguration & ILoggerFactory).</param>
    /// <param name="initialState">The initial state for new aggregate instances.</param>
    /// <param name="name">The shard type name for this aggregate.</param>
    /// <param name="handleCommand">The command handler function: `Command -> State -> EventAction`.</param>
    /// <param name="apply">The event handler function: `Event -> State -> State`.</param>
    /// <returns>An entity factory (`EntityFac<obj>`) for creating instances of this actor.</returns>
    abstract InitializeActor: #IConfigurationWrapper & #ILoggerFactoryWrapper -> 'a -> string -> (Command<'c >-> 'a -> EventAction<'b>) -> (Event<'b> -> 'a -> 'a) -> EntityFac<obj>
    /// Initializes a sharded, persistent saga actor.
    /// <param name="cfg">Environment configuration (IConfiguration & ILoggerFactory).</param>
    /// <param name="initialState">The initial state (`SagaState`) for new saga instances.</param>
    /// <param name="handleEvent">The event handler function: `Event -> SagaState -> EventAction`.</param>
    /// <param name="applySideEffects">Function determining side effects based on state transitions: `SagaState -> Option<StartingEvent> -> bool -> SagaTransition<NewState> * ExecuteCommand list`.</param>
    /// <param name="applyStateChange">Function to apply internal state changes: `SagaState -> SagaState`.</param>
    /// <param name="name">The shard type name for this saga.</param>
    /// <returns>An entity factory (`EntityFac<obj>`) for creating instances of this saga.</returns>
    abstract InitializeSaga: #IConfigurationWrapper & #ILoggerFactoryWrapper-> SagaState<'SagaState,'State>  -> (obj-> SagaState<'SagaState,'State>-> EventAction<'State>) ->
        (SagaState<'SagaState,'State> -> option<SagaStartingEvent<Event<'c>>> -> bool -> SagaTransition<'State> * ExecuteCommand list) ->
        (SagaState<'SagaState,'State> -> SagaState<'SagaState,'State>) -> string ->EntityFac<obj>
    /// Initializes the Saga Starter actor, configuring which events trigger which sagas.
    /// <param name="eventHandler">A function mapping a received event object to a list of saga definitions to start: `obj -> list<(Factory * Prefix * StartingEvent)>`.</param>
    abstract InitializeSagaStarter: (obj  -> list<(string -> IEntityRef<obj>) * PrefixConversion * obj>) -> unit

// Internal helper to create Event records

/// Default shard name used if no specific sharding strategy is provided.


/// Represents a potential transformation to apply to an entity ID prefix, used in saga routing.
/// Allows sagas to be co-located or routed differently based on the originator's ID structure.
type PrefixConversion = PrefixConversion of ((string -> string) option)

/// Contains types and functions for building and initializing sagas
module SagaBuilder =
    /// Standard recovery logic for Started state that all sagas should use
    /// Handles the version checking handshake with the originator aggregate
    let handleStartedState recovering (startingEvent: option<SagaStartingEvent<_>>) (originatorFactory: string -> IEntityRef<obj>) =
        match recovering with
        | true ->
            let startingEvent = startingEvent.Value.Event
            let originator = FactoryAndName { Factory =  originatorFactory; Name = Originator }
            Stay,
            [ { TargetActor = originator
                Command = ContinueOrAbort startingEvent
                DelayInMs = None } ]
        | false ->
            Stay, []
    
    /// Standard wrapper for saga states that includes NotStarted/Started
    type SagaStateWrapper<'UserState, 'TEvent> =
        | NotStarted
        | Started of SagaStartingEvent<Event<'TEvent>>
        | UserDefined of 'UserState
    
    /// Creates initial saga state with NotStarted
    let createInitialState<'SagaData, 'UserState, 'TEvent> (data: 'SagaData) : SagaState<'SagaData, SagaStateWrapper<'UserState, 'TEvent>> =
        { State = NotStarted; Data = data }
    
    /// Wraps user's applySideEffects to handle NotStarted/Started automatically
    let wrapApplySideEffects<'SagaData, 'UserState, 'TEvent>
        (userApplySideEffects: SagaState<'SagaData, 'UserState> -> bool -> SagaTransition<'UserState> * ExecuteCommand list)
        (originatorFactory: string -> IEntityRef<obj>)
        (sagaState: SagaState<'SagaData, SagaStateWrapper<'UserState, 'TEvent>>)
        (startingEvent: option<SagaStartingEvent<Event<'TEvent>>>)
        (recovering: bool)
        : SagaTransition<SagaStateWrapper<'UserState, 'TEvent>> * ExecuteCommand list =
        match sagaState.State with
        | NotStarted -> NextState(Started startingEvent.Value), []
        | Started _ -> 
            let transition, commands = handleStartedState recovering startingEvent originatorFactory
            transition, commands
        | UserDefined userState ->
            let userSagaState = { Data = sagaState.Data; State = userState }
            let transition, commands = userApplySideEffects userSagaState recovering
            match transition with
            | StopSaga -> StopSaga, commands
            | Stay -> Stay, commands
            | NextState newState -> NextState(UserDefined newState), commands
    
    /// Wraps user's handleEvent to skip NotStarted but allow Started states
    let wrapHandleEvent<'SagaData, 'UserState, 'TEvent>
        (userHandleEvent: obj -> SagaState<'SagaData, 'UserState option> -> EventAction<'UserState>)
        (event: obj)
        (sagaState: SagaState<'SagaData, SagaStateWrapper<'UserState, 'TEvent>>)
        : EventAction<SagaStateWrapper<'UserState, 'TEvent>> =
        match sagaState.State with
        | NotStarted -> UnhandledEvent
        | Started _ -> 
            // Allow user code to handle events in Started state to transition to user-defined states
            // For Started->UserDefined transitions, pass None since no user state exists yet
            let userSagaState = { Data = sagaState.Data; State = None }
            match userHandleEvent event userSagaState with
            | StateChangedEvent newState -> StateChangedEvent (UserDefined newState)
            | _ -> UnhandledEvent
        | UserDefined userState ->
            let userSagaState = { Data = sagaState.Data; State = Some userState }
            match userHandleEvent event userSagaState with
            | StateChangedEvent newState -> StateChangedEvent (UserDefined newState)
            | _ -> UnhandledEvent
    
    /// High-level saga initialization that handles all wrapping automatically
    let init<'SagaData, 'UserState, 'TEvent, 'Env when 'Env :> IConfigurationWrapper and 'Env :> ILoggerFactoryWrapper>
        (actorApi: IActor)
        (env: 'Env)
        (sagaData: 'SagaData)
        (userHandleEvent: obj -> SagaState<'SagaData, 'UserState option> -> EventAction<'UserState>)
        (userApplySideEffects: SagaState<'SagaData, 'UserState> -> bool -> SagaTransition<'UserState> * ExecuteCommand list)
        (userApply: SagaState<'SagaData, SagaStateWrapper<'UserState, 'TEvent>> -> SagaState<'SagaData, SagaStateWrapper<'UserState, 'TEvent>>)
        (originatorFactory: string -> IEntityRef<obj>)
        (sagaName: string)
        =
        let initialState = createInitialState<'SagaData, 'UserState, 'TEvent> sagaData
        let handleEvent = wrapHandleEvent userHandleEvent
        let applySideEffects = wrapApplySideEffects userApplySideEffects originatorFactory
        
        actorApi.InitializeSaga
            env
            initialState
            handleEvent
            applySideEffects
            userApply
            sagaName

/// Contains types and functions related to the Saga Starter actor (internal implementation detail).
module SagaStarter =
    open Microsoft.FSharp.Reflection

    [<AutoOpen>]
    module Internal =
    // Internal helpers for manipulating saga/originator names and CIDs
        let internal toOriginatorName (name: string) =
            let index = name.IndexOf(SAGA_Suffix)
            if index > 0 then name.Substring(0, index) else name

        let internal toRawGuid (name: string) =
            let index = name.LastIndexOf(CID_Separator)
            name.Substring(index + 1).Replace(SAGA_Suffix, "")

        let internal toCidWithExisting (name: string) (existing: string) =
            let originator = name
            let guid = existing |> toRawGuid
            originator + CID_Separator + guid

        let internal toCid name =
            let originator = (name |> toOriginatorName)
            let guid = name |> toRawGuid
            originator + CID_Separator + guid

        let internal cidToSagaName (name: string) = name + SAGA_Suffix
        let internal isSaga (name: string) = name.Contains(SAGA_Suffix)

        // Internal constants for Saga Starter actor
        [<Literal>]
        let internal SagaStarterName = "SagaStarter"
        [<Literal>]
        let internal SagaStarterPath = "/user/SagaStarter"

        // Internal messages for Saga Starter
        type internal Command =
            | CheckSagas of obj * originator: Actor.IActorRef * cid: string
            | Continue
        type internal Event = SagaCheckDone



        type internal Message =
            | Command of Command
            | Event of Event

        // Internal helpers for Saga Starter communication
        let internal toCheckSagas (event, originator, cid) =
            (event |> box |> Unchecked.nonNull, originator, cid) |> CheckSagas |> Command

        let internal toSendMessage mediator (originator: IActorRef<_>) event =
            let cid = toCidWithExisting originator.Path.Name (event.CorrelationId |> ValueLens.Value |> ValueLens.Value)
            let message = Send(SagaStarterPath, (event, untyped originator, cid) |> toCheckSagas, true)
            mediator <? message |> Async.RunSynchronously |> ignore // Fire-and-forget ask
            event |> box |> Unchecked.nonNull

        let internal publishEvent (logger:ILogger) (mailbox: Actor<_>) (mediator) event (cid) =
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

        let internal cont (mediator) =
            mediator <! box (Send(SagaStarterPath, Continue |> Command, true))

        let internal subscriber (mediator: IActorRef<_>) (mailbox: Eventsourced<_>) =
            let topic = mailbox.Self.Path.Name |> toCid
            mediator <! box (Subscribe(topic, untyped mailbox.Self))

        let internal (|SubscriptionAcknowledged|_|) (context: Actor<obj>) (msg: obj) : obj option =
            let topic = context.Self.Path.Name |> toCid
            match msg with
            | :? SubscribeAck as s when s.Subscribe.Topic = topic -> Some msg
            | _ -> None

        let internal unboxx (msg: obj) =
            let genericType = typedefof<SagaStartingEvent<_>>.MakeGenericType [| msg.GetType() |]
            FSharpValue.MakeRecord(genericType, [| msg |])

        // Internal implementation of the Saga Starter actor
        let internal actorProp
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
                        | Some(_, lists) -> state.Remove(name).Add(name, (sender, sagas :: lists ))
                    state
                actor {
                    match! mailbox.Receive() with
                    | Command Continue ->
                        let sender = untyped <| mailbox.Sender()
                        let originName = sender.Path.Name |> toOriginatorName
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

        let internal init system mediator sagaCheck =
            let sagaStarter = spawn system <| SagaStarterName <| props (actorProp sagaCheck)
            typed mediator <! (sagaStarter |> untyped |> Put)

    /// Wraps an event that is intended to start a saga.
    /// This is typically the message sent to a saga actor upon its creation.
    /// <typeparam name="'T">The type of the starting event payload.</typeparam>
    type SagaStartingEvent<'T> =
        { /// The actual event payload that triggers the saga.
          Event: 'T }
        interface ISerializable

/// (Internal) Contains the implementation for command subscriptions.
[<AutoOpen>]
module CommandHandler =
    [<AutoOpen>]
    module Internal = 
    // Internal active pattern for subscription acknowledgements
        let  (|SubscriptionAcknowledged|_|) (msg: obj) =
            match msg with
            | :? SubscribeAck as s -> Some s
            | _ -> None

        type  State<'Command, 'Event> =
                { CommandDetails: CommandDetails<'Command, 'Event>
                  Sender: IActorRef } // The actor waiting for the response

        let  subscribeForCommand<'Command, 'Event> system mediator (command: Command<'Command, 'Event>) =
                    let actorProp mediator (mailbox: Actor<obj>) =
                        let log = mailbox.UntypedContext.GetLogger()
                        let rec set (state: State<'Command, 'Event> option) =
                            actor {
                                let! msg = mailbox.Receive()
                                match box msg |> Unchecked.nonNull with
                                // On SubscribeAck, send the actual command to the target entity
                                | SubscriptionAcknowledged _ ->
                                    let cmd = state.Value.CommandDetails.Cmd |> box
                                    state.Value.CommandDetails.EntityRef <! cmd
                                    return! set state
                                // When receiving the initial Execute command, store details and subscribe
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
                                // When receiving an Event, check correlation ID and filter
                                | :? (Event<'Event>) as e when e.CorrelationId = state.Value.CommandDetails.Cmd.CorrelationId ->
                                    if state.Value.CommandDetails.Filter e.EventDetails then
                                        state.Value.Sender.Tell e // Send event back to original asker
                                        return! Stop // Stop the temporary subscription actor
                                    else
                                        log.Debug("Ignoring filtered event from subscriber: {msg}", msg)
                                        return! set state // Continue waiting
                                | LifecycleEvent _ -> return! Ignore // Ignore actor lifecycle events
                                | _ ->
                                    log.Error("Unexpected message in subscriber: {msg}", msg)
                                    return! Ignore // Ignore other unexpected messages
                            }
                        set None // Initial state is None
            
                    // Spawn the temporary actor and send it the initial Execute command
                    async {
                        let! res = spawnAnonymous system (props (actorProp mediator)) <? box command
                        return box res |> nonNull :?> Event<'Event> // Return the awaited event
                    }
            

    // Internal types for command subscription actor state -> Made public for Command DU
    type CommandDetails<'Command, 'Event> =
        { EntityRef: IEntityRef<obj>
          Cmd: Command<'Command> // The original Command object
          Filter: ('Event -> bool) }

    /// Represents the message sent to the internal subscription mechanism.
    /// <typeparam name="'Command">The type of the command payload.</typeparam>
    /// <typeparam name="'Event">The type of the expected event payload.</typeparam>
    type Command<'Command, 'Event> = Execute of CommandDetails<'Command, 'Event>

   