/// C# interoperability helpers for FCQRS
/// Provides simpler APIs for consuming FCQRS from C#
module FCQRS.CSharp

open System
open System.Threading.Tasks
open System.Runtime.CompilerServices
open FCQRS.Model.Data
open FCQRS.Common

/// C# delegate for command handlers - returns Task<Event<TEvent>>
type Handler<'TCmd, 'TEvent when 'TEvent: not null> = delegate of filter: Func<'TEvent, bool> * cid: CID * aggregateId: AggregateId * command: 'TCmd -> Task<Event<'TEvent>>

/// The two C#-facing pieces of a wired aggregate: a Factory for entity refs
/// (used by sagas to target the aggregate) and a Handler to send a command and
/// await its event (used by the delivery layer). Returned by InitAggregate.
type AggregateRefs<'TCommand, 'TEvent when 'TEvent: not null> =
    { Factory: Func<string, Akkling.Cluster.Sharding.IEntityRef<obj>>
      Handler: Handler<'TCommand, 'TEvent> }

/// Obsolete: nested in a module, so its [<Extension>] AsTask was never discoverable
/// as an extension from C# (hence the duplicate ToTask). Use the top-level
/// `AsTask` extension instead: `using FCQRS;` then `someAsync.AsTask()`.
[<System.Obsolete("Use the top-level AsTask extension (using FCQRS;); this type will be removed in a future preview.")>]
type AsyncExtensions =
    static member AsTask(computation: Async<'T>) : Task<'T> =
        Async.StartAsTask computation
    static member ToTask(computation: Async<'T>) : Task<'T> =
        Async.StartAsTask computation

/// Factory methods for creating FCQRS's strongly-typed value/identifier types
/// (CID, AggregateId, MessageId, ShortString, LongString, Version) from C#.
type Values =
    /// Create a ShortString from a string (throws on failure)
    static member CreateShortString(s: string) : ShortString =
        match ValueLens.TryCreate<ShortString, _, _> s with
        | Ok v -> v
        | Error e -> failwithf "Failed to create ShortString: %A" e

    /// Create a CID from a string (throws on failure)
    static member CreateCID(s: string) : CID =
        let shortString = Values.CreateShortString s
        ValueLens.Create shortString

    /// Create a new CID from a GUID v7
    static member NewCID() : CID =
        Guid.CreateVersion7().ToString() |> Values.CreateCID

    /// Create an AggregateId from a string (throws on failure)
    static member CreateAggregateId(s: string) : AggregateId =
        let shortString = Values.CreateShortString s
        ValueLens.Create shortString

    /// Create a MessageId from a string (throws on failure)
    static member CreateMessageId(s: string) : MessageId =
        let shortString = Values.CreateShortString s
        ValueLens.Create shortString

    /// Create a new MessageId from a GUID v7
    static member NewMessageId() : MessageId =
        Guid.CreateVersion7().ToString() |> Values.CreateMessageId

    /// Create a Version from a non-negative int64 (throws on failure)
    static member CreateVersion(v: int64) : Version =
        match ValueLens.TryCreate<Version, _, _> v with
        | Ok ver -> ver
        | Error e -> failwithf "Failed to create Version: %A" e

    /// Try to create a ShortString (returns Result instead of throwing)
    static member TryCreateShortString(s: string) : Result<ShortString, ModelError list> =
        ValueLens.TryCreate<ShortString, _, _> s

    /// Try to create a LongString (returns Result instead of throwing)
    static member TryCreateLongString(s: string) : Result<LongString, ModelError list> =
        ValueLens.TryCreate<LongString, _, _> s

    /// Create a LongString from a string (throws on failure)
    static member CreateLongString(s: string) : LongString =
        match ValueLens.TryCreate<LongString, _, _> s with
        | Ok v -> v
        | Error e -> failwithf "Failed to create LongString: %A" e

    /// Try to create a ShortString, C# Try-pattern style: returns success + out value.
    static member TryCreateShortString(s: string, [<System.Runtime.InteropServices.Out>] result: byref<ShortString>) : bool =
        match ValueLens.TryCreate<ShortString, _, _> s with
        | Ok v -> result <- v; true
        | Error _ -> false

    /// Try to create a LongString, C# Try-pattern style: returns success + out value.
    static member TryCreateLongString(s: string, [<System.Runtime.InteropServices.Out>] result: byref<LongString>) : bool =
        match ValueLens.TryCreate<LongString, _, _> s with
        | Ok v -> result <- v; true
        | Error _ -> false

/// Obsolete alias for <see cref="T:FCQRS.CSharp.Values"/>. Renamed because these
/// are factories for FCQRS's value/identifier types, not general-purpose helpers.
/// Will be removed in a future preview.
[<System.Obsolete("Renamed to Values; this alias will be removed in a future preview.")>]
type Helpers =
    static member CreateShortString(s: string) : ShortString = Values.CreateShortString s
    static member CreateCID(s: string) : CID = Values.CreateCID s
    static member NewCID() : CID = Values.NewCID()
    static member CreateAggregateId(s: string) : AggregateId = Values.CreateAggregateId s
    static member CreateMessageId(s: string) : MessageId = Values.CreateMessageId s
    static member NewMessageId() : MessageId = Values.NewMessageId()
    static member CreateVersion(v: int64) : Version = Values.CreateVersion v
    static member TryCreateShortString(s: string) : Result<ShortString, ModelError list> = Values.TryCreateShortString s
    static member TryCreateLongString(s: string) : Result<LongString, ModelError list> = Values.TryCreateLongString s
    static member CreateLongString(s: string) : LongString = Values.CreateLongString s

/// C#-friendly factory methods for constructing an F# Result (Ok/Error) from C#.
type FSharpResults =
    /// Create a successful result
    static member Ok<'T, 'E>(value: 'T) : Result<'T, 'E> =
        Ok value

    /// Create an error result
    static member Error<'T, 'E>(error: 'E) : Result<'T, 'E> =
        Error error

/// Obsolete alias for <see cref="T:FCQRS.CSharp.FSharpResults"/>. Renamed off the
/// generic 'Results' (which also collides with Microsoft.AspNetCore.Http.Results).
[<System.Obsolete("Renamed to FSharpResults; this alias will be removed in a future preview.")>]
type Results =
    static member Ok<'T, 'E>(value: 'T) : Result<'T, 'E> = FSharpResults.Ok<'T, 'E> value
    static member Error<'T, 'E>(error: 'E) : Result<'T, 'E> = FSharpResults.Error<'T, 'E> error

/// Obsolete alias — the bool/out Try-create overloads now live on <see cref="T:FCQRS.CSharp.Values"/>.
[<System.Obsolete("Use Values.TryCreateShortString / Values.TryCreateLongString; this alias will be removed in a future preview.")>]
type StringTypes =
    static member TryCreateShortString(s: string, [<System.Runtime.InteropServices.Out>] result: byref<ShortString>) : bool =
        Values.TryCreateShortString(s, &result)
    static member TryCreateLongString(s: string, [<System.Runtime.InteropServices.Out>] result: byref<LongString>) : bool =
        Values.TryCreateLongString(s, &result)

/// C#-friendly factory methods for EventAction
type EventActions =
    /// Create a PersistEvent action (event will be persisted and published)
    static member Persist<'TEvent when 'TEvent: not null>(event: 'TEvent) : EventAction<'TEvent> =
        EventAction.PersistEvent event

    /// Create a DeferEvent action (event is returned but not persisted - for errors/rejections)
    static member Defer<'TEvent when 'TEvent: not null>(event: 'TEvent) : EventAction<'TEvent> =
        EventAction.DeferEvent event

    /// Create an IgnoreEvent action (command is ignored, no event produced)
    static member Ignore<'TEvent when 'TEvent: not null>() : EventAction<'TEvent> =
        EventAction.IgnoreEvent

/// C#-friendly builders for the Command/Event envelopes that the pure
/// handleCommand/applyEvent functions expect. Intended for unit tests: the
/// envelope's plumbing fields (a fresh MessageId/CID, a UTC timestamp, no
/// sender, empty metadata) are filled in for you, so a test supplies only the
/// payload — and, for events, the aggregate version. The framework builds these
/// envelopes itself at runtime; tests are the one place you build them by hand.
type TestEnvelope =
    /// Wrap a command payload in a Command envelope, stamping CreationDate from
    /// the given TimeProvider. Pass a FakeTimeProvider to test time-dependent
    /// decision logic (e.g. sliding-window quotas) deterministically.
    static member Command<'T>(details: 'T, timeProvider: TimeProvider) : Command<'T> =
        { CommandDetails = details
          CreationDate = timeProvider.GetUtcNow().UtcDateTime
          Id = Values.NewMessageId()
          Sender = None
          CorrelationId = Values.NewCID()
          Metadata = Map.empty }

    /// Wrap a command payload in a Command envelope using the system clock.
    static member Command<'T>(details: 'T) : Command<'T> =
        TestEnvelope.Command<'T>(details, TimeProvider.System)

    /// Wrap an event payload in an Event envelope carrying the given version,
    /// stamping CreationDate from the given TimeProvider.
    static member Event<'T when 'T: not null>(details: 'T, version: int64, timeProvider: TimeProvider) : Event<'T> =
        { EventDetails = details
          CreationDate = timeProvider.GetUtcNow().UtcDateTime
          Id = Values.NewMessageId()
          Sender = None
          CorrelationId = Values.NewCID()
          Version = Values.CreateVersion version
          Metadata = Map.empty }

    /// Wrap an event payload in an Event envelope using the system clock.
    static member Event<'T when 'T: not null>(details: 'T, version: int64) : Event<'T> =
        TestEnvelope.Event<'T>(details, version, TimeProvider.System)

/// C#-friendly class for defining saga starters (uses class for C# object initializer syntax)
[<AllowNullLiteral>]
type SagaDefinition() =
    /// Factory function to create entity reference from entity ID
    member val Factory: Func<string, Akkling.Cluster.Sharding.IEntityRef<obj>> | null = null with get, set
    /// How to derive saga entity ID from source entity ID.
    /// Defaults to identity (originatorId~Saga~correlationId). The previous default of
    /// `PrefixConversion None` produced saga names without the ~Saga~ marker, which breaks
    /// originator/saga name resolution and the SagaStarter Continue→SagaCheckDone handshake.
    member val PrefixConversion: PrefixConversion = PrefixConversion (Some id) with get, set
    /// The event to send to start the saga
    member val StartingEvent: obj | null = null with get, set

/// C#-friendly factory for PrefixConversion
type PrefixConversions =
    /// Identity conversion - uses originator prefix with saga suffix
    /// This creates saga IDs like: originatorId~Saga~correlationId
    static member Identity = PrefixConversion (Some id)
    /// Custom conversion function
    static member Custom(f: Func<string, string>) = PrefixConversion (Some f.Invoke)

/// C#-friendly Actor API
type ActorApi =
    /// Create the actor system with SQLite connection
    static member Create(
        configuration: Microsoft.Extensions.Configuration.IConfiguration,
        loggerFactory: Microsoft.Extensions.Logging.ILoggerFactory,
        sqliteConnectionString: string,
        clusterName: string) : IActor =
        let connString = Values.CreateShortString sqliteConnectionString
        let connection : Actor.Connection = { ConnectionString = connString; DBType = Actor.DBType.Sqlite }
        let name = Values.CreateShortString clusterName
        Actor.api configuration loggerFactory (Some connection) name

/// C#-friendly Query API
type QueryApi =
    /// Initialize the query subscription (F# list version)
    static member Init(
        actorApi: IActor,
        lastOffset: int,
        eventHandler: Func<int64, obj, IMessageWithCID list>) : FCQRS.Query.ISubscribe<IMessageWithCID> =
        let handler offset evt = eventHandler.Invoke(offset, evt)
        Query.init actorApi lastOffset handler

    /// Initialize the query subscription (C# IList version - converts to F# list
    /// internally). An overload of Init, distinguished by the handler's return type.
    static member Init(
        actorApi: IActor,
        lastOffset: int,
        eventHandler: Func<int64, obj, System.Collections.Generic.IList<IMessageWithCID>>) : FCQRS.Query.ISubscribe<IMessageWithCID> =
        let handler offset evt = eventHandler.Invoke(offset, evt) |> List.ofSeq
        Query.init actorApi lastOffset handler

    /// Obsolete alias — InitWithList is now just an overload of Init.
    [<System.Obsolete("Use Init (it's now an overload); this alias will be removed in a future preview.")>]
    static member InitWithList(
        actorApi: IActor,
        lastOffset: int,
        eventHandler: Func<int64, obj, System.Collections.Generic.IList<IMessageWithCID>>) : FCQRS.Query.ISubscribe<IMessageWithCID> =
        QueryApi.Init(actorApi, lastOffset, eventHandler)

/// Low-level wiring over an IActor: register the saga-starter, aggregates and
/// actors, and send commands. Most members are plain static helpers (you call
/// ActorWiring.Foo(actor, …)); a couple are genuine extension methods. For app
/// composition, prefer the host-builder API (AddFcqrs/AddAggregate/AddSaga).
[<Extension>]
type ActorWiring =
    /// Initialize saga starter with no sagas (for simple scenarios)
    [<Extension>]
    static member InitializeSagaStarterEmpty(actor: IActor) : unit =
        actor.InitializeSagaStarter(fun _ -> ([] : list<(string -> Akkling.Cluster.Sharding.IEntityRef<obj>) * PrefixConversion * obj>))

    /// Static helper for C# where extension may not resolve
    static member InitSagaStarterEmpty(actor: IActor) : unit =
        actor.InitializeSagaStarter(fun _ -> ([] : list<(string -> Akkling.Cluster.Sharding.IEntityRef<obj>) * PrefixConversion * obj>))

    /// C#-friendly InitializeSagaStarter that accepts Func returning IList of SagaDefinition
    static member InitSagaStarter(
        actor: IActor,
        eventHandler: Func<obj, System.Collections.Generic.IList<SagaDefinition>>) : unit =
        let handler evt =
            eventHandler.Invoke(evt)
            |> Seq.map (fun def -> ((nonNull def.Factory).Invoke, def.PrefixConversion, nonNull def.StartingEvent))
            |> List.ofSeq
        actor.InitializeSagaStarter(handler)

    /// C#-friendly simplified InitializeSagaStarter where you just return factories
    static member InitSagaStarterSimple(
        actor: IActor,
        eventHandler: Func<obj, System.Collections.Generic.IList<Func<string, Akkling.Cluster.Sharding.IEntityRef<obj>>>>) : unit =
        let handler evt =
            eventHandler.Invoke(evt)
            |> Seq.map (fun factory -> factory.Invoke)
            |> List.ofSeq
        actor.InitializeSagaStarter(handler)

    /// C#-friendly InitializeActor that accepts Func delegates instead of F# functions
    static member InitActor<'TState, 'TCommand, 'TEvent when 'TEvent: not null>(
        actor: IActor,
        initialState: 'TState,
        entityName: string,
        handleCommand: Func<Command<'TCommand>, 'TState, EventAction<'TEvent>>,
        applyEvent: Func<Event<'TEvent>, 'TState, 'TState>) : Akkling.Cluster.Sharding.EntityFac<obj> =
        let cmdHandler cmd state = handleCommand.Invoke(cmd, state)
        let evtApplier evt state = applyEvent.Invoke(evt, state)
        actor.InitializeActor initialState entityName cmdHandler evtApplier

    /// One-call aggregate wiring: registers the aggregate (sharding region) and
    /// returns its Factory + Handler. Collapses the Init/Factory/Handler trio an
    /// aggregate would otherwise hand-roll. Call it for EVERY aggregate you want
    /// live — registration is the act of calling this, not a side effect.
    static member InitAggregate<'TState, 'TCommand, 'TEvent when 'TEvent: not null>(
        actor: IActor,
        initialState: 'TState,
        entityName: string,
        handleCommand: Func<Command<'TCommand>, 'TState, EventAction<'TEvent>>,
        applyEvent: Func<Event<'TEvent>, 'TState, 'TState>) : AggregateRefs<'TCommand, 'TEvent> =
        let fac = ActorWiring.InitActor<'TState, 'TCommand, 'TEvent>(actor, initialState, entityName, handleCommand, applyEvent)
        let factory = Func<string, Akkling.Cluster.Sharding.IEntityRef<obj>>(fun entityId -> fac.RefFor DEFAULT_SHARD entityId)
        let handler =
            Handler<'TCommand, 'TEvent>(fun filter cid aggregateId command ->
                ActorWiring.SendCommandAsync<'TEvent, 'TCommand>(actor, factory, cid, aggregateId, command, filter))
        { Factory = factory; Handler = handler }

    /// Send a command and wait for the event (C# friendly)
    [<Extension>]
    static member SendCommandAsync<'TEvent, 'TCommand when 'TEvent: not null>(
        actor: IActor,
        entityFactory: Func<string, Akkling.Cluster.Sharding.IEntityRef<obj>>,
        cid: CID,
        aggregateId: AggregateId,
        command: 'TCommand,
        filter: Func<'TEvent, bool>) : Task<Event<'TEvent>> =
        let factory = fun s -> entityFactory.Invoke(s)
        let filterF = fun e -> filter.Invoke(e)
        actor.CreateCommandSubscription factory cid aggregateId command filterF None
        |> Async.StartAsTask

    /// C#-friendly CreateCommandSubscription that returns FSharpAsync (for use with Handler delegate)
    static member CreateCommand<'TEvent, 'TCommand when 'TEvent: not null>(
        actor: IActor,
        entityFactory: Func<string, Akkling.Cluster.Sharding.IEntityRef<obj>>,
        cid: CID,
        aggregateId: AggregateId,
        command: 'TCommand,
        filter: Func<'TEvent, bool>) : Async<Event<'TEvent>> =
        let factory = fun s -> entityFactory.Invoke(s)
        let filterF = fun e -> filter.Invoke(e)
        actor.CreateCommandSubscription factory cid aggregateId command filterF None

/// Obsolete alias for <see cref="T:FCQRS.CSharp.ActorWiring"/>. Renamed because
/// most members are static wiring helpers, not extension methods.
[<System.Obsolete("Renamed to ActorWiring; this alias will be removed in a future preview.")>]
[<Extension>]
type IActorExtensions =
    [<Extension>]
    static member InitializeSagaStarterEmpty(actor: IActor) : unit =
        ActorWiring.InitializeSagaStarterEmpty actor
    static member InitSagaStarterEmpty(actor: IActor) : unit =
        ActorWiring.InitSagaStarterEmpty actor
    static member InitSagaStarter(actor: IActor, eventHandler: Func<obj, System.Collections.Generic.IList<SagaDefinition>>) : unit =
        ActorWiring.InitSagaStarter(actor, eventHandler)
    static member InitSagaStarterSimple(actor: IActor, eventHandler: Func<obj, System.Collections.Generic.IList<Func<string, Akkling.Cluster.Sharding.IEntityRef<obj>>>>) : unit =
        ActorWiring.InitSagaStarterSimple(actor, eventHandler)
    static member InitActor<'TState, 'TCommand, 'TEvent when 'TEvent: not null>(actor: IActor, initialState: 'TState, entityName: string, handleCommand: Func<Command<'TCommand>, 'TState, EventAction<'TEvent>>, applyEvent: Func<Event<'TEvent>, 'TState, 'TState>) : Akkling.Cluster.Sharding.EntityFac<obj> =
        ActorWiring.InitActor<'TState, 'TCommand, 'TEvent>(actor, initialState, entityName, handleCommand, applyEvent)
    static member InitAggregate<'TState, 'TCommand, 'TEvent when 'TEvent: not null>(actor: IActor, initialState: 'TState, entityName: string, handleCommand: Func<Command<'TCommand>, 'TState, EventAction<'TEvent>>, applyEvent: Func<Event<'TEvent>, 'TState, 'TState>) : AggregateRefs<'TCommand, 'TEvent> =
        ActorWiring.InitAggregate<'TState, 'TCommand, 'TEvent>(actor, initialState, entityName, handleCommand, applyEvent)
    [<Extension>]
    static member SendCommandAsync<'TEvent, 'TCommand when 'TEvent: not null>(actor: IActor, entityFactory: Func<string, Akkling.Cluster.Sharding.IEntityRef<obj>>, cid: CID, aggregateId: AggregateId, command: 'TCommand, filter: Func<'TEvent, bool>) : Task<Event<'TEvent>> =
        ActorWiring.SendCommandAsync<'TEvent, 'TCommand>(actor, entityFactory, cid, aggregateId, command, filter)
    static member CreateCommand<'TEvent, 'TCommand when 'TEvent: not null>(actor: IActor, entityFactory: Func<string, Akkling.Cluster.Sharding.IEntityRef<obj>>, cid: CID, aggregateId: AggregateId, command: 'TCommand, filter: Func<'TEvent, bool>) : Async<Event<'TEvent>> =
        ActorWiring.CreateCommand<'TEvent, 'TCommand>(actor, entityFactory, cid, aggregateId, command, filter)

/// Extension methods for ISubscribe
[<Extension>]
type ISubscribeExtensions =
    /// C#-friendly Subscribe that accepts Func instead of FSharpFunc
    [<Extension>]
    static member SubscribeFor<'T when 'T :> FCQRS.Model.Data.IMessageWithCID>(
        subs: Query.ISubscribe<'T>,
        filter: Func<'T, bool>,
        take: int) : Query.IAwaitableDisposable =
        subs.Subscribe((fun e -> filter.Invoke(e)), take)

    /// C#-friendly Subscribe that matches by CID only
    [<Extension>]
    static member SubscribeFor<'T when 'T :> FCQRS.Model.Data.IMessageWithCID>(
        subs: Query.ISubscribe<'T>,
        cid: FCQRS.Model.Data.CID,
        take: int) : Query.IAwaitableDisposable =
        subs.Subscribe(cid, take)

    /// C#-friendly Subscribe that matches by CID and additional filter
    [<Extension>]
    static member SubscribeFor<'T when 'T :> FCQRS.Model.Data.IMessageWithCID>(
        subs: Query.ISubscribe<'T>,
        cid: FCQRS.Model.Data.CID,
        filter: Func<'T, bool>,
        take: int) : Query.IAwaitableDisposable =
        subs.Subscribe(cid, (fun e -> filter.Invoke(e)), take)

// =============================================================================
// SAGA C# INTEROP
// =============================================================================

open Akkling.Cluster.Sharding
open Common.SagaBuilder

/// C#-friendly result type for saga side effects (uses class for C# object initializer syntax)
[<AllowNullLiteral>]
type SagaSideEffectResult<'TState>() =
    member val Transition: SagaTransition<'TState> = SagaTransition.Stay with get, set
    member val Commands: System.Collections.Generic.IList<ExecuteCommand> = System.Collections.Generic.List<ExecuteCommand>() with get, set

    /// Convert commands to F# list for internal use
    member this.CommandsList = this.Commands |> List.ofSeq

/// C#-friendly helpers for SagaState
type SagaStates =
    /// Create a new SagaState with updated Data
    static member WithData<'TSagaData, 'TState>(sagaState: SagaState<'TSagaData, 'TState>, newData: 'TSagaData) : SagaState<'TSagaData, 'TState> =
        { sagaState with Data = newData }

    /// Create a new SagaState with updated State
    static member WithState<'TSagaData, 'TState>(sagaState: SagaState<'TSagaData, 'TState>, newState: 'TState) : SagaState<'TSagaData, 'TState> =
        { sagaState with State = newState }

/// C#-friendly factory methods for SagaTransition
type SagaTransitions =
    /// Stay in the current state (no transition)
    static member Stay<'TState>() : SagaTransition<'TState> =
        SagaTransition.Stay

    /// Transition to a new state
    static member NextState<'TState>(newState: 'TState) : SagaTransition<'TState> =
        SagaTransition.NextState newState

    /// Stop the saga (completes the saga lifecycle)
    static member StopSaga<'TState>() : SagaTransition<'TState> =
        SagaTransition.StopSaga

/// C#-friendly factory methods for saga EventAction
type SagaEventActions =
    /// Create a state change event
    static member StateChanged<'TState when 'TState : not null>(newState: 'TState) : EventAction<'TState> =
        EventAction.StateChangedEvent newState

    /// Event was not handled
    static member Unhandled<'TState when 'TState : not null>() : EventAction<'TState> =
        EventAction.UnhandledEvent

    /// Ignore the event
    static member Ignore<'TState when 'TState : not null>() : EventAction<'TState> =
        EventAction.IgnoreEvent

/// C#-friendly saga command targeting
type SagaCommands =
    /// Create a command to send to the originator aggregate
    static member ToOriginator(
        factory: Func<string, IEntityRef<obj>>,
        command: obj) : ExecuteCommand =
        { TargetActor = FactoryAndName { Factory = factory.Invoke; Name = Originator }
          Command = command
          DelayInMs = None }

    /// Create a command to send to a named aggregate
    static member ToAggregate(
        factory: Func<string, IEntityRef<obj>>,
        aggregateId: string,
        command: obj) : ExecuteCommand =
        { TargetActor = FactoryAndName { Factory = factory.Invoke; Name = Name aggregateId }
          Command = command
          DelayInMs = None }

    /// Create a command to send to an actor ref
    static member ToActor(
        actorRef: Akkling.ActorRefs.IActorRef<obj>,
        command: obj) : ExecuteCommand =
        { TargetActor = ActorRef actorRef
          Command = command
          DelayInMs = None }

    /// Create a delayed command
    static member ToOriginatorDelayed(
        factory: Func<string, IEntityRef<obj>>,
        command: obj,
        delayMs: int64,
        taskName: string) : ExecuteCommand =
        { TargetActor = FactoryAndName { Factory = factory.Invoke; Name = Originator }
          Command = command
          DelayInMs = Some (delayMs, taskName) }

/// Low-level C# saga API (mirrors ActorApi/QueryApi). For most sagas, prefer the
/// Saga&lt;_,_,_&gt; base class, which wraps this.
type SagaApi =
    /// Initialize a saga with C# delegates
    /// 'TEvent - The triggering event type (from the originator aggregate)
    /// 'TSagaData - Cross-cutting saga data
    /// 'TSagaState - User-defined saga states
    static member Init<'TEvent, 'TSagaData, 'TSagaState when 'TSagaState : not null and 'TEvent : not null>(
        actorApi: IActor,
        sagaData: 'TSagaData,
        handleEvent: Func<obj, SagaState<'TSagaData, 'TSagaState option>, EventAction<'TSagaState>>,
        applySideEffects: Func<SagaState<'TSagaData, 'TSagaState>, bool, SagaSideEffectResult<'TSagaState>>,
        apply: Func<SagaState<'TSagaData, SagaStateWrapper<'TSagaState, 'TEvent>>, SagaState<'TSagaData, SagaStateWrapper<'TSagaState, 'TEvent>>>,
        originatorFactory: Func<string, IEntityRef<obj>>,
        sagaName: string) : EntityFac<obj> =

        // Convert C# handleEvent to F#
        let fsharpHandleEvent (evt: obj) (state: SagaState<'TSagaData, 'TSagaState option>) : EventAction<'TSagaState> =
            handleEvent.Invoke(evt, state)

        // Convert C# applySideEffects to F#
        let fsharpApplySideEffects (state: SagaState<'TSagaData, 'TSagaState>) (recovering: bool) : SagaTransition<'TSagaState> * ExecuteCommand list =
            let result = applySideEffects.Invoke(state, recovering)
            (result.Transition, result.CommandsList)

        // Convert C# apply to F#
        let fsharpApply (state: SagaState<'TSagaData, SagaStateWrapper<'TSagaState, 'TEvent>>) =
            apply.Invoke(state)

        // Convert C# factory to F#
        let fsharpFactory = fun (s: string) -> originatorFactory.Invoke(s)

        SagaBuilder.init
            actorApi
            sagaData
            fsharpHandleEvent
            fsharpApplySideEffects
            fsharpApply
            fsharpFactory
            sagaName

    /// Initialize a saga with simplified apply - just takes data and unwrapped state, returns new data
    static member Init<'TEvent, 'TSagaData, 'TSagaState when 'TSagaState : not null and 'TEvent : not null>(
        actorApi: IActor,
        sagaData: 'TSagaData,
        handleEvent: Func<obj, SagaState<'TSagaData, 'TSagaState option>, EventAction<'TSagaState>>,
        applySideEffects: Func<SagaState<'TSagaData, 'TSagaState>, bool, SagaSideEffectResult<'TSagaState>>,
        apply: Func<'TSagaData, 'TSagaState, 'TSagaData>,
        originatorFactory: Func<string, IEntityRef<obj>>,
        sagaName: string) : EntityFac<obj> =

        // Wrap the simplified apply into the full signature
        let fullApply (sagaState: SagaState<'TSagaData, SagaStateWrapper<'TSagaState, 'TEvent>>) =
            match sagaState.State with
            | SagaStateWrapper.UserDefined userState ->
                let newData = apply.Invoke(sagaState.Data, userState)
                { sagaState with Data = newData }
            | _ -> sagaState

        SagaApi.Init<'TEvent, 'TSagaData, 'TSagaState>(
            actorApi, sagaData, handleEvent, applySideEffects,
            Func<_, _>(fullApply), originatorFactory, sagaName)

    /// Initialize a saga without apply (no data updates based on state)
    static member Init<'TEvent, 'TSagaData, 'TSagaState when 'TSagaState : not null and 'TEvent : not null>(
        actorApi: IActor,
        sagaData: 'TSagaData,
        handleEvent: Func<obj, SagaState<'TSagaData, 'TSagaState option>, EventAction<'TSagaState>>,
        applySideEffects: Func<SagaState<'TSagaData, 'TSagaState>, bool, SagaSideEffectResult<'TSagaState>>,
        originatorFactory: Func<string, IEntityRef<obj>>,
        sagaName: string) : EntityFac<obj> =

        SagaApi.Init<'TEvent, 'TSagaData, 'TSagaState>(
            actorApi, sagaData, handleEvent, applySideEffects,
            Func<_, _, _>(fun data _ -> data), originatorFactory, sagaName)

    /// Initialize a saga with simplified signatures - no FSharpOption, typed events
    /// handleEvent: (event, data, currentState) -> EventAction
    /// applySideEffects: (data, state, recovering) -> SagaSideEffectResult
    static member InitSimple<'TEvent, 'TSagaData, 'TSagaState when 'TSagaState : not null and 'TEvent : not null>(
        actorApi: IActor,
        sagaData: 'TSagaData,
        handleEvent: Func<Event<'TEvent>, 'TSagaData, 'TSagaState, EventAction<'TSagaState>>,
        applySideEffects: Func<'TSagaData, 'TSagaState, bool, SagaSideEffectResult<'TSagaState>>,
        apply: Func<'TSagaData, 'TSagaState, 'TSagaData>,
        originatorFactory: Func<string, IEntityRef<obj>>,
        sagaName: string) : EntityFac<obj> =

        // Wrap simplified handleEvent - unwrap FSharpOption and cast event
        let wrappedHandleEvent (evt: obj) (sagaState: SagaState<'TSagaData, 'TSagaState option>) =
            match evt with
            | :? Event<'TEvent> as typedEvent ->
                let currentState = sagaState.State |> Option.defaultValue Unchecked.defaultof<'TSagaState>
                handleEvent.Invoke(typedEvent, sagaState.Data, currentState)
            | _ -> EventAction.UnhandledEvent

        // Wrap simplified applySideEffects
        let wrappedApplySideEffects (sagaState: SagaState<'TSagaData, 'TSagaState>) (recovering: bool) =
            applySideEffects.Invoke(sagaState.Data, sagaState.State, recovering)

        SagaApi.Init<'TEvent, 'TSagaData, 'TSagaState>(
            actorApi, sagaData,
            Func<_, _, _>(wrappedHandleEvent),
            Func<_, _, _>(wrappedApplySideEffects),
            apply, originatorFactory, sagaName)

    /// Initialize a saga with simplified signatures and no apply
    static member InitSimple<'TEvent, 'TSagaData, 'TSagaState when 'TSagaState : not null and 'TEvent : not null>(
        actorApi: IActor,
        sagaData: 'TSagaData,
        handleEvent: Func<Event<'TEvent>, 'TSagaData, 'TSagaState, EventAction<'TSagaState>>,
        applySideEffects: Func<'TSagaData, 'TSagaState, bool, SagaSideEffectResult<'TSagaState>>,
        originatorFactory: Func<string, IEntityRef<obj>>,
        sagaName: string) : EntityFac<obj> =

        SagaApi.InitSimple<'TEvent, 'TSagaData, 'TSagaState>(
            actorApi, sagaData, handleEvent, applySideEffects,
            Func<_, _, _>(fun data _ -> data), originatorFactory, sagaName)

    /// Get the factory for a saga
    static member Factory(
        actorApi: IActor,
        sagaEntityFac: EntityFac<obj>,
        entityId: string) : IEntityRef<obj> =
        sagaEntityFac.RefFor DEFAULT_SHARD entityId

/// C#-friendly abstract base for an event-sourced aggregate. A concrete
/// aggregate supplies only InitialState, EntityName, HandleCommand (the
/// decision) and ApplyEvent (the fold); the base provides the wiring (Init).
/// Designed to be subclassed from C#.
[<AbstractClass>]
type Aggregate<'TState, 'TCommand, 'TEvent when 'TEvent: not null>() =
    abstract member InitialState: 'TState
    abstract member EntityName: string
    abstract member HandleCommand: Command<'TCommand> * 'TState -> EventAction<'TEvent>
    abstract member ApplyEvent: Event<'TEvent> * 'TState -> 'TState

    /// Register the aggregate and hand back its Factory + Handler.
    member this.Init(actorApi: IActor) : AggregateRefs<'TCommand, 'TEvent> =
        ActorWiring.InitAggregate<'TState, 'TCommand, 'TEvent>(
            actorApi,
            this.InitialState,
            this.EntityName,
            Func<Command<'TCommand>, 'TState, EventAction<'TEvent>>(fun c s -> this.HandleCommand(c, s)),
            Func<Event<'TEvent>, 'TState, 'TState>(fun e s -> this.ApplyEvent(e, s)))

/// C#-friendly abstract base for a saga. A concrete saga supplies InitialData,
/// SagaName, Originator (the aggregate it starts from), HandleEvent and
/// ApplySideEffects; the base provides the wiring (Init/Factory) and the small
/// transition DSL. Designed to be subclassed from C#.
[<AbstractClass>]
type Saga<'TEvent, 'TSagaData, 'TState when 'TEvent: not null and 'TState: not null>() =
    abstract member InitialData: 'TSagaData
    abstract member SagaName: string
    abstract member Originator: Func<string, IEntityRef<obj>>
    abstract member HandleEvent: obj * SagaState<'TSagaData, 'TState option> -> EventAction<'TState>
    abstract member ApplySideEffects: SagaState<'TSagaData, 'TState> * bool -> SagaSideEffectResult<'TState>

    /// Transition / event DSL — over TState.
    static member StateChanged(next: 'TState) : EventAction<'TState> = SagaEventActions.StateChanged<'TState>(next)
    static member Unhandled() : EventAction<'TState> = SagaEventActions.Unhandled<'TState>()
    static member Stay() : SagaTransition<'TState> = SagaTransitions.Stay<'TState>()
    static member NextState(next: 'TState) : SagaTransition<'TState> = SagaTransitions.NextState<'TState>(next)
    static member StopSaga() : SagaTransition<'TState> = SagaTransitions.StopSaga<'TState>()

    /// Register the saga; calling this IS the registration.
    member this.Init(actorApi: IActor) : EntityFac<obj> =
        SagaApi.Init<'TEvent, 'TSagaData, 'TState>(
            actorApi,
            this.InitialData,
            Func<obj, SagaState<'TSagaData, 'TState option>, EventAction<'TState>>(fun e s -> this.HandleEvent(e, s)),
            Func<SagaState<'TSagaData, 'TState>, bool, SagaSideEffectResult<'TState>>(fun s r -> this.ApplySideEffects(s, r)),
            this.Originator,
            this.SagaName)

    /// The factory the saga-starter spawns instances from.
    member this.Factory(actorApi: IActor) : Func<string, IEntityRef<obj>> =
        let fac = this.Init(actorApi)
        Func<string, IEntityRef<obj>>(fun entityId -> fac.RefFor DEFAULT_SHARD entityId)
