/// C# interoperability helpers for FCQRS
/// Provides simpler APIs for consuming FCQRS from C#
module FCQRS.CSharp

open System
open System.Threading.Tasks
open System.Runtime.CompilerServices
open Microsoft.Extensions.Logging
open FCQRS.Model.Data
open FCQRS.Common

/// C# delegate for command handlers - returns Task<Event<TEvent>>
type Handler<'TCmd, 'TEvent when 'TEvent: not null> = delegate of filter: Func<'TEvent, bool> * cid: CID * aggregateId: AggregateId * command: 'TCmd -> Task<Event<'TEvent>>

/// C# delegate for an aggregate's entity-ref factory: an id -> its sharded actor
/// ref. sp.AggregateFactory&lt;T&gt;() returns one; sagas use it to target an aggregate.
type AggregateFactory = delegate of string -> Akkling.Cluster.Sharding.IEntityRef<obj>

/// The two C#-facing pieces of a wired aggregate: a Factory for entity refs
/// (used by sagas to target the aggregate) and a Handler to send a command and
/// await its event (used by the delivery layer). Returned by InitAggregate.
type AggregateRefs<'TCommand, 'TEvent when 'TEvent: not null> =
    { Factory: AggregateFactory
      Handler: Handler<'TCommand, 'TEvent> }

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

/// C#-friendly factory methods for constructing an F# Result (Ok/Error) from C#.
type FSharpResults =
    /// Create a successful result
    static member Ok<'T, 'E>(value: 'T) : Result<'T, 'E> =
        Ok value

    /// Create an error result
    static member Error<'T, 'E>(error: 'E) : Result<'T, 'E> =
        Error error


/// Fluent registrar for stable journal type names (see JournalTypes).
type JournalTypeMapBuilder internal () =
    /// Map a payload type to its stable journal name (plus optional aliases).
    member this.Type<'T>(name: string, [<ParamArray>] aliases: string[]) : JournalTypeMapBuilder =
        JournalTypes.Map(typeof<'T>, name, aliases)
        this

/// C#-friendly factory methods for EventAction
type EventActions =
    /// Create a PersistEvent action (event will be persisted and published)
    static member Persist<'TEvent when 'TEvent: not null>(event: 'TEvent) : EventAction<'TEvent> =
        EventAction.PersistEvent event

    /// Create a DeferEvent action. The event is published and folded but not
    /// persisted. Rejection and idempotent-reply folds should preserve state,
    /// because recovery cannot replay a deferred event.
    static member Defer<'TEvent when 'TEvent: not null>(event: 'TEvent) : EventAction<'TEvent> =
        EventAction.DeferEvent event

    /// Persist the event when `shouldPersist` is true; otherwise Defer it - the
    /// event is still published and folded but is not written to the journal or
    /// sent through a projection. Its fold should preserve the current state.
    /// The idempotent "emit this verdict, but only
    /// write it once" shape, e.g. re-approving an already-approved aggregate:
    /// `PersistConditionally(state.Approval != Approved, new Approved(id))`.
    static member PersistConditionally<'TEvent when 'TEvent: not null>(shouldPersist: bool, event: 'TEvent) : EventAction<'TEvent> =
        if shouldPersist then EventAction.PersistEvent event else EventAction.DeferEvent event

    /// Create a PersistAndSnapshot action: persist the event, then save a manual
    /// snapshot once it is durable - independent of the SnapshotPolicy cadence.
    static member PersistAndSnapshot<'TEvent when 'TEvent: not null>(event: 'TEvent) : EventAction<'TEvent> =
        EventAction.PersistAndSnapshot event

    /// Create a PersistAllEvents action: all events from this command persist as
    /// one journal AtomicWrite (all-or-nothing), with sequential versions. State
    /// updates and publishes happen only after the whole batch is durable.
    static member PersistAll<'TEvent when 'TEvent: not null>([<ParamArray>] events: 'TEvent[]) : EventAction<'TEvent> =
        EventAction.PersistAllEvents(List.ofArray events)

    /// Create an IgnoreEvent action (command is ignored, no event produced)
    static member Ignore<'TEvent when 'TEvent: not null>() : EventAction<'TEvent> =
        EventAction.IgnoreEvent

    /// Create a RunAsync effect (a "mini saga" without persistence) from an
    /// INSPECTABLE description object. The runner registered via
    /// `InitAggregateWithEffects` executes it and self-dispatches the resulting
    /// command. EPHEMERAL (not journaled, so a crash mid-flight loses it) and
    /// TOTAL (the runner must map failure to a command). See EventAction.RunAsync.
    static member Dispatch<'TEvent when 'TEvent: not null>(description: obj) : EventAction<'TEvent> =
        EventAction.RunAsync description

/// Diagnostics helper: a short, readable case name for logging. Unwraps a
/// Command&lt;_&gt;/Event&lt;_&gt; envelope to its payload (via IEnvelope), then a
/// C# `union` to its active case (its generated `.Value`), and falls back to the
/// type name, so F# DUs and plain payloads work too. Reflection-based; meant for
/// logs, not hot paths.
type Describe =
    /// A short case name, e.g. "CreateOrUpdate" for a DocumentCommand.CreateOrUpdate
    /// (or a Command/Event wrapping one). Returns "&lt;none&gt;" for null.
    static member Case(value: obj | null) : string =
        match value with
        | null -> "<none>"
        | v ->
            // Envelope -> payload (a no-op for a bare payload).
            let payload =
                match v with
                | :? IEnvelope as e -> e.Payload
                | _ -> v
            // C# union -> active case via its generated .Value (a no-op otherwise).
            let case =
                match payload.GetType().GetProperty "Value" with
                | null -> payload
                | prop -> match prop.GetValue payload with null -> payload | inner -> inner
            case.GetType().Name

/// C#-friendly builders for the Command/Event envelopes that the pure
/// handleCommand/applyEvent functions expect. Intended for unit tests: the
/// envelope's plumbing fields (a fresh MessageId/CID, a UTC timestamp, no
/// sender, empty metadata) are filled in for you, so a test supplies only the
/// payload and, for events, the aggregate version. The framework builds these
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
    member val Factory: AggregateFactory | null = null with get, set
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
        lastOffset: int64,
        eventHandler: Func<int64, obj, IMessageWithCID list>) : FCQRS.Query.ISubscribe =
        let handler offset evt = eventHandler.Invoke(offset, evt)
        Query.init actorApi lastOffset handler |> Query.asDefaultSubscribe

    /// Initialize the query subscription (C# IList version - converts to F# list
    /// internally). An overload of Init, distinguished by the handler's return type.
    static member Init(
        actorApi: IActor,
        lastOffset: int64,
        eventHandler: Func<int64, obj, System.Collections.Generic.IList<IMessageWithCID>>) : FCQRS.Query.ISubscribe =
        let handler offset evt = eventHandler.Invoke(offset, evt) |> List.ofSeq
        Query.init actorApi lastOffset handler |> Query.asDefaultSubscribe

    /// Initialize the query subscription with a single-event handler: the
    /// handler just updates the read model (returns void); each aggregate event
    /// is then published to subscribers as-is. Use a list-returning overload
    /// when notifications must be filtered or transformed.
    static member Init(
        actorApi: IActor,
        lastOffset: int64,
        eventHandler: Action<int64, obj>) : FCQRS.Query.ISubscribe =
        let handler = Query.autoPublish (fun offset evt -> eventHandler.Invoke(offset, evt))
        Query.init actorApi lastOffset handler |> Query.asDefaultSubscribe

    /// Initialize the query subscription with a filtered single-event handler:
    /// the handler updates the read model and returns Publish/Suppress to say
    /// whether this event wakes subscribers. Between the void Action overload
    /// (publish all) and the list-returning one (full control).
    static member Init(
        actorApi: IActor,
        lastOffset: int64,
        eventHandler: Func<int64, obj, Notify>) : FCQRS.Query.ISubscribe =
        let handler = Query.filterPublish (fun offset evt -> eventHandler.Invoke(offset, evt))
        Query.init actorApi lastOffset handler |> Query.asDefaultSubscribe

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

    /// C#-friendly InitializeSagaStarter that accepts Func returning IList of SagaDefinition.
    /// A null list means "no sagas". Null Factory/StartingEvent members fail with a
    /// named error: this handler runs inside the SagaStarter during the saga-start
    /// handshake, where a bare NRE used to surface 30s later as a misleading
    /// handshake-timeout FailFast.
    static member InitSagaStarter(
        actor: IActor,
        eventHandler: Func<obj, System.Collections.Generic.IList<SagaDefinition>>) : unit =
        let handler evt =
            match eventHandler.Invoke(evt) with
            | null -> []
            | defs ->
                defs
                |> Seq.map (fun def ->
                    if isNull (box def) then
                        invalidOp "InitSagaStarter handler returned a list containing a null SagaDefinition."

                    if isNull (box def.Factory) then
                        invalidOp $"SagaDefinition.Factory is null for starting event type {evt.GetType().Name}. Set Factory when constructing the SagaDefinition."

                    if isNull (box def.StartingEvent) then
                        invalidOp $"SagaDefinition.StartingEvent is null for starting event type {evt.GetType().Name}. Set StartingEvent when constructing the SagaDefinition."

                    (def.Factory.Invoke, def.PrefixConversion, def.StartingEvent))
                |> List.ofSeq

        actor.InitializeSagaStarter(handler)

    /// C#-friendly simplified InitializeSagaStarter where you just return factories.
    /// A null list means "no sagas"; a null factory fails with a named error.
    static member InitSagaStarterSimple(
        actor: IActor,
        eventHandler: Func<obj, System.Collections.Generic.IList<AggregateFactory>>) : unit =
        let handler evt =
            match eventHandler.Invoke(evt) with
            | null -> []
            | factories ->
                factories
                |> Seq.map (fun factory ->
                    if isNull (box factory) then
                        invalidOp "InitSagaStarterSimple handler returned a list containing a null factory."

                    factory.Invoke)
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
        actor.InitializeActor initialState entityName cmdHandler evtApplier SnapshotPolicy.Default

    /// InitActor with an explicit per-aggregate snapshot policy.
    static member InitActor<'TState, 'TCommand, 'TEvent when 'TEvent: not null>(
        actor: IActor,
        initialState: 'TState,
        entityName: string,
        handleCommand: Func<Command<'TCommand>, 'TState, EventAction<'TEvent>>,
        applyEvent: Func<Event<'TEvent>, 'TState, 'TState>,
        snapshotPolicy: SnapshotPolicy) : Akkling.Cluster.Sharding.EntityFac<obj> =
        let cmdHandler cmd state = handleCommand.Invoke(cmd, state)
        let evtApplier evt state = applyEvent.Invoke(evt, state)
        actor.InitializeActor initialState entityName cmdHandler evtApplier snapshotPolicy

    /// One-call aggregate wiring: registers the aggregate (sharding region) and
    /// returns its Factory + Handler. Collapses the Init/Factory/Handler trio an
    /// aggregate would otherwise hand-roll. Call it for EVERY aggregate you want
    /// live. Registration is the act of calling this, not a side effect.
    static member InitAggregate<'TState, 'TCommand, 'TEvent when 'TEvent: not null>(
        actor: IActor,
        initialState: 'TState,
        entityName: string,
        handleCommand: Func<Command<'TCommand>, 'TState, EventAction<'TEvent>>,
        applyEvent: Func<Event<'TEvent>, 'TState, 'TState>) : AggregateRefs<'TCommand, 'TEvent> =
        ActorWiring.InitAggregate<'TState, 'TCommand, 'TEvent>(actor, initialState, entityName, handleCommand, applyEvent, SnapshotPolicy.Default)

    /// InitAggregate with an explicit per-aggregate snapshot policy.
    static member InitAggregate<'TState, 'TCommand, 'TEvent when 'TEvent: not null>(
        actor: IActor,
        initialState: 'TState,
        entityName: string,
        handleCommand: Func<Command<'TCommand>, 'TState, EventAction<'TEvent>>,
        applyEvent: Func<Event<'TEvent>, 'TState, 'TState>,
        snapshotPolicy: SnapshotPolicy) : AggregateRefs<'TCommand, 'TEvent> =
        let fac = ActorWiring.InitActor<'TState, 'TCommand, 'TEvent>(actor, initialState, entityName, handleCommand, applyEvent, snapshotPolicy)
        let factory = AggregateFactory(fun entityId -> fac.RefFor DEFAULT_SHARD entityId)
        let handler =
            Handler<'TCommand, 'TEvent>(fun filter cid aggregateId command ->
                ActorWiring.SendCommandAsync<'TEvent, 'TCommand>(actor, factory, cid, aggregateId, command, filter))
        { Factory = factory; Handler = handler }

    /// InitActor whose decide can return `EventActions.Dispatch(...)` (the
    /// RunAsync effect). `runner` maps a boxed effect description to a Task of
    /// the boxed command self-dispatched back to the aggregate. It MUST be total
    /// catch every failure into a command (try/catch in the Task); an escaping
    /// exception fail-fasts the process, like a throwing fold. The in-flight work
    /// is NOT journaled (ephemeral). See EventAction.RunAsync.
    static member InitActorWithRunner<'TState, 'TCommand, 'TEvent when 'TEvent: not null>(
        actor: IActor,
        initialState: 'TState,
        entityName: string,
        handleCommand: Func<Command<'TCommand>, 'TState, EventAction<'TEvent>>,
        applyEvent: Func<Event<'TEvent>, 'TState, 'TState>,
        runner: Func<obj, Task<obj>>,
        snapshotPolicy: SnapshotPolicy) : Akkling.Cluster.Sharding.EntityFac<obj> =
        let cmdHandler cmd state = handleCommand.Invoke(cmd, state)
        let evtApplier evt state = applyEvent.Invoke(evt, state)
        let boxedRunner: obj -> Async<obj> = fun description -> runner.Invoke description |> Async.AwaitTask
        actor.InitializeActorWithRunner initialState entityName cmdHandler evtApplier snapshotPolicy (Some boxedRunner)

    /// InitAggregate whose decide can return `EventActions.Dispatch(...)`. See
    /// InitActorWithRunner. Returns Factory + Handler like InitAggregate.
    static member InitAggregateWithEffects<'TState, 'TCommand, 'TEvent when 'TEvent: not null>(
        actor: IActor,
        initialState: 'TState,
        entityName: string,
        handleCommand: Func<Command<'TCommand>, 'TState, EventAction<'TEvent>>,
        applyEvent: Func<Event<'TEvent>, 'TState, 'TState>,
        runner: Func<obj, Task<obj>>,
        snapshotPolicy: SnapshotPolicy) : AggregateRefs<'TCommand, 'TEvent> =
        let fac =
            ActorWiring.InitActorWithRunner<'TState, 'TCommand, 'TEvent>(
                actor, initialState, entityName, handleCommand, applyEvent, runner, snapshotPolicy)
        let factory = AggregateFactory(fun entityId -> fac.RefFor DEFAULT_SHARD entityId)
        let handler =
            Handler<'TCommand, 'TEvent>(fun filter cid aggregateId command ->
                ActorWiring.SendCommandAsync<'TEvent, 'TCommand>(actor, factory, cid, aggregateId, command, filter))
        { Factory = factory; Handler = handler }

    /// Send a command and wait for the event (C# friendly)
    [<Extension>]
    static member SendCommandAsync<'TEvent, 'TCommand when 'TEvent: not null>(
        actor: IActor,
        entityFactory: AggregateFactory,
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
        entityFactory: AggregateFactory,
        cid: CID,
        aggregateId: AggregateId,
        command: 'TCommand,
        filter: Func<'TEvent, bool>) : Async<Event<'TEvent>> =
        let factory = fun s -> entityFactory.Invoke(s)
        let filterF = fun e -> filter.Invoke(e)
        actor.CreateCommandSubscription factory cid aggregateId command filterF None

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
        factory: AggregateFactory,
        command: obj) : ExecuteCommand =
        { TargetActor = FactoryAndName { Factory = factory.Invoke; Name = Originator }
          Command = command
          DelayInMs = None }

    /// Create a command to send to a named aggregate
    static member ToAggregate(
        factory: AggregateFactory,
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

    /// Create a command to the saga itself (raw; lands in HandleEvent), for example a timeout.
    static member ToSelf(command: obj) : ExecuteCommand =
        { TargetActor = Self; Command = command; DelayInMs = None }

    /// Create a delayed command to a specific aggregate instance.
    static member ToAggregateAfter(
        factory: AggregateFactory,
        entityId: string,
        command: obj,
        delayMs: int64,
        taskName: string) : ExecuteCommand =
        { TargetActor = FactoryAndName { Factory = factory.Invoke; Name = Name entityId }
          Command = command
          DelayInMs = Some (delayMs, taskName) }

    /// Create a delayed command to a concrete actor ref.
    static member ToActorAfter(
        actorRef: Akkling.ActorRefs.IActorRef<obj>,
        command: obj,
        delayMs: int64,
        taskName: string) : ExecuteCommand =
        { TargetActor = ActorRef actorRef
          Command = command
          DelayInMs = Some (delayMs, taskName) }

    /// Schedule a message to the saga itself after a delay, the idiomatic saga timeout.
    static member ToSelfAfter(
        command: obj,
        delayMs: int64,
        taskName: string) : ExecuteCommand =
        { TargetActor = Self
          Command = command
          DelayInMs = Some (delayMs, taskName) }

    /// Create a delayed command
    static member ToOriginatorAfter(
        factory: AggregateFactory,
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
        originatorFactory: AggregateFactory,
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
            SnapshotPolicy.Default

    /// Initialize a saga with an explicit per-saga snapshot policy.
    static member Init<'TEvent, 'TSagaData, 'TSagaState when 'TSagaState : not null and 'TEvent : not null>(
        actorApi: IActor,
        sagaData: 'TSagaData,
        handleEvent: Func<obj, SagaState<'TSagaData, 'TSagaState option>, EventAction<'TSagaState>>,
        applySideEffects: Func<SagaState<'TSagaData, 'TSagaState>, bool, SagaSideEffectResult<'TSagaState>>,
        apply: Func<SagaState<'TSagaData, SagaStateWrapper<'TSagaState, 'TEvent>>, SagaState<'TSagaData, SagaStateWrapper<'TSagaState, 'TEvent>>>,
        originatorFactory: AggregateFactory,
        sagaName: string,
        snapshotPolicy: SnapshotPolicy) : EntityFac<obj> =

        let fsharpHandleEvent (evt: obj) (state: SagaState<'TSagaData, 'TSagaState option>) : EventAction<'TSagaState> =
            handleEvent.Invoke(evt, state)

        let fsharpApplySideEffects (state: SagaState<'TSagaData, 'TSagaState>) (recovering: bool) : SagaTransition<'TSagaState> * ExecuteCommand list =
            let result = applySideEffects.Invoke(state, recovering)
            (result.Transition, result.CommandsList)

        let fsharpApply (state: SagaState<'TSagaData, SagaStateWrapper<'TSagaState, 'TEvent>>) =
            apply.Invoke(state)

        let fsharpFactory = fun (s: string) -> originatorFactory.Invoke(s)

        SagaBuilder.init
            actorApi
            sagaData
            fsharpHandleEvent
            fsharpApplySideEffects
            fsharpApply
            fsharpFactory
            sagaName
            snapshotPolicy

    /// Initialize a saga with simplified apply - just takes data and unwrapped state, returns new data
    static member Init<'TEvent, 'TSagaData, 'TSagaState when 'TSagaState : not null and 'TEvent : not null>(
        actorApi: IActor,
        sagaData: 'TSagaData,
        handleEvent: Func<obj, SagaState<'TSagaData, 'TSagaState option>, EventAction<'TSagaState>>,
        applySideEffects: Func<SagaState<'TSagaData, 'TSagaState>, bool, SagaSideEffectResult<'TSagaState>>,
        apply: Func<'TSagaData, 'TSagaState, 'TSagaData>,
        originatorFactory: AggregateFactory,
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
        originatorFactory: AggregateFactory,
        sagaName: string) : EntityFac<obj> =

        SagaApi.Init<'TEvent, 'TSagaData, 'TSagaState>(
            actorApi, sagaData, handleEvent, applySideEffects,
            Func<_, _, _>(fun data _ -> data), originatorFactory, sagaName)

    /// Initialize a saga with simplified signatures - no FSharpOption, typed events
    /// handleEvent: (event, data, currentState) -> EventAction
    /// applySideEffects: (data, state, recovering) -> SagaSideEffectResult
    /// LIMITATION: the typed handler only receives the originator's Event&lt;'TEvent&gt;.
    /// ToSelf/ToSelfAfter payloads and other aggregates' reply events cannot reach it
    /// (they are logged and ignored) — sagas needing timeouts or multi-aggregate
    /// orchestration must use the obj-based Init overloads.
    static member InitSimple<'TEvent, 'TSagaData, 'TSagaState when 'TSagaState : not null and 'TEvent : not null>(
        actorApi: IActor,
        sagaData: 'TSagaData,
        handleEvent: Func<Event<'TEvent>, 'TSagaData, 'TSagaState, EventAction<'TSagaState>>,
        applySideEffects: Func<'TSagaData, 'TSagaState, bool, SagaSideEffectResult<'TSagaState>>,
        apply: Func<'TSagaData, 'TSagaState, 'TSagaData>,
        originatorFactory: AggregateFactory,
        sagaName: string) : EntityFac<obj> =

        let logger = actorApi.LoggerFactory.CreateLogger sagaName

        // Wrap simplified handleEvent - unwrap FSharpOption and cast event
        let wrappedHandleEvent (evt: obj) (sagaState: SagaState<'TSagaData, 'TSagaState option>) =
            match evt with
            | :? Event<'TEvent> as typedEvent ->
                let currentState = sagaState.State |> Option.defaultValue Unchecked.defaultof<'TSagaState>
                handleEvent.Invoke(typedEvent, sagaState.Data, currentState)
            | other ->
                // Loud, not silent: a ToSelfAfter timeout payload or another
                // aggregate's reply landing here means the workflow depends on a
                // message this typed adapter structurally cannot deliver.
                logger.LogWarning(
                    "Saga {Saga} (InitSimple) ignored a {MessageType}: the typed handler only accepts Event<{EventType}>. Use the obj-based Init overload for ToSelf timeouts or multi-aggregate sagas.",
                    sagaName,
                    (match other with
                     | null -> "null"
                     | o -> o.GetType().Name),
                    typeof<'TEvent>.Name)

                EventAction.UnhandledEvent

        // Wrap simplified applySideEffects
        let wrappedApplySideEffects (sagaState: SagaState<'TSagaData, 'TSagaState>) (recovering: bool) =
            applySideEffects.Invoke(sagaState.Data, sagaState.State, recovering)

        SagaApi.Init<'TEvent, 'TSagaData, 'TSagaState>(
            actorApi, sagaData,
            Func<_, _, _>(wrappedHandleEvent),
            Func<_, _, _>(wrappedApplySideEffects),
            apply, originatorFactory, sagaName)

    /// Initialize a saga with simplified signatures and no apply.
    /// Same limitation as the other InitSimple overload: only the originator's
    /// Event&lt;'TEvent&gt; reaches the typed handler; ToSelf timeouts and other
    /// aggregates' replies require the obj-based Init overloads.
    static member InitSimple<'TEvent, 'TSagaData, 'TSagaState when 'TSagaState : not null and 'TEvent : not null>(
        actorApi: IActor,
        sagaData: 'TSagaData,
        handleEvent: Func<Event<'TEvent>, 'TSagaData, 'TSagaState, EventAction<'TSagaState>>,
        applySideEffects: Func<'TSagaData, 'TSagaState, bool, SagaSideEffectResult<'TSagaState>>,
        originatorFactory: AggregateFactory,
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

    /// Per-aggregate snapshot cadence. Override to use Every(n) or NoSnapshots;
    /// the default falls back to config:akka:persistence:snapshot-version-count (or 30).
    abstract member SnapshotPolicy: SnapshotPolicy
    default _.SnapshotPolicy = SnapshotPolicy.Default

    /// Register the aggregate with an explicit (already-resolved) snapshot policy.
    member this.Init(actorApi: IActor, snapshotPolicy: SnapshotPolicy) : AggregateRefs<'TCommand, 'TEvent> =
        ActorWiring.InitAggregate<'TState, 'TCommand, 'TEvent>(
            actorApi,
            this.InitialState,
            this.EntityName,
            Func<Command<'TCommand>, 'TState, EventAction<'TEvent>>(fun c s -> this.HandleCommand(c, s)),
            Func<Event<'TEvent>, 'TState, 'TState>(fun e s -> this.ApplyEvent(e, s)),
            snapshotPolicy)

    /// Register the aggregate and hand back its Factory + Handler.
    member this.Init(actorApi: IActor) : AggregateRefs<'TCommand, 'TEvent> =
        this.Init(actorApi, this.SnapshotPolicy)

/// C#-friendly abstract base for a saga. A concrete saga supplies InitialData,
/// SagaName, Originator (the aggregate it starts from), HandleEvent and
/// ApplySideEffects; the base provides the wiring (Init/Factory) and the small
/// transition DSL. Designed to be subclassed from C#.
[<AbstractClass>]
type Saga<'TEvent, 'TSagaData, 'TState when 'TEvent: not null and 'TState: not null>() =
    abstract member InitialData: 'TSagaData
    abstract member SagaName: string
    abstract member Originator: AggregateFactory
    abstract member HandleEvent: obj * SagaState<'TSagaData, 'TState option> -> EventAction<'TState>
    abstract member ApplySideEffects: SagaState<'TSagaData, 'TState> * bool -> SagaSideEffectResult<'TState>

    /// Transition and event DSL over TState.
    static member StateChanged(next: 'TState) : EventAction<'TState> = SagaEventActions.StateChanged<'TState>(next)
    static member Unhandled() : EventAction<'TState> = SagaEventActions.Unhandled<'TState>()
    static member Stay() : SagaTransition<'TState> = SagaTransitions.Stay<'TState>()
    static member NextState(next: 'TState) : SagaTransition<'TState> = SagaTransitions.NextState<'TState>(next)
    static member StopSaga() : SagaTransition<'TState> = SagaTransitions.StopSaga<'TState>()

    /// Per-saga snapshot cadence. Override to use Every(n) or NoSnapshots;
    /// the default falls back to config:akka:persistence:snapshot-version-count (or 30).
    abstract member SnapshotPolicy: SnapshotPolicy
    default _.SnapshotPolicy = SnapshotPolicy.Default

    /// Register the saga with an explicit (already-resolved) snapshot policy.
    member this.Init(actorApi: IActor, snapshotPolicy: SnapshotPolicy) : EntityFac<obj> =
        SagaApi.Init<'TEvent, 'TSagaData, 'TState>(
            actorApi,
            this.InitialData,
            Func<obj, SagaState<'TSagaData, 'TState option>, EventAction<'TState>>(fun e s -> this.HandleEvent(e, s)),
            Func<SagaState<'TSagaData, 'TState>, bool, SagaSideEffectResult<'TState>>(fun s r -> this.ApplySideEffects(s, r)),
            Func<SagaState<'TSagaData, SagaStateWrapper<'TState, 'TEvent>>, SagaState<'TSagaData, SagaStateWrapper<'TState, 'TEvent>>>(id),
            this.Originator,
            this.SagaName,
            snapshotPolicy)

    /// Register the saga; calling this IS the registration.
    member this.Init(actorApi: IActor) : EntityFac<obj> =
        this.Init(actorApi, this.SnapshotPolicy)

    /// The factory the saga-starter spawns instances from, with an explicit policy.
    member this.Factory(actorApi: IActor, snapshotPolicy: SnapshotPolicy) : AggregateFactory =
        let fac = this.Init(actorApi, snapshotPolicy)
        AggregateFactory(fun entityId -> fac.RefFor DEFAULT_SHARD entityId)

    /// The factory the saga-starter spawns instances from.
    member this.Factory(actorApi: IActor) : AggregateFactory =
        this.Factory(actorApi, this.SnapshotPolicy)
