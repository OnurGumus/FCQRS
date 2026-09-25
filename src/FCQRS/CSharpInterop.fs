/// C# interoperability helpers for FCQRS
/// Provides simpler APIs for consuming FCQRS from C#
module FCQRS.CSharp

open System
open System.Threading.Tasks
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
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
/// (CID, AggregateId, MessageId, ShortString, LongString, Version) from C#, and
/// for reading a Version's number back.
type Values =
    /// Create a ShortString from a string (throws on failure)
    static member CreateShortString(s: string) : ShortString =
        match ValueLens.TryCreate<ShortString, _, _> s with
        | Ok v -> v
        | Error e -> failwithf "Failed to create ShortString: %A" e

    /// Create a CID from a string (throws on failure).
    /// A CID becomes part of pub-sub topics and saga entity names built with
    /// "~" separators, so a CID containing "~" breaks the correlation parsing
    /// (toRawGuid/toCid) and leaves sagas permanently deaf. CID's constructor
    /// rejects it with ArgumentException.
    static member CreateCID(s: string) : CID =
        let shortString = Values.CreateShortString s
        ValueLens.Create shortString

    /// Create a new CID from a GUID v7
    static member NewCID() : CID =
        Guid.CreateVersion7().ToString() |> Values.CreateCID

    /// Create an AggregateId from a string (throws on failure).
    /// Any non-blank id works: the shard names entity actors
    /// Uri.EscapeDataString(entityId), so characters Akka actor names would
    /// reject directly (spaces, %) are escaped before they reach an actor path.
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

    /// The number a Version holds, such as an event's persisted version. Store it
    /// beside read-model data, or pass it to SendIfVersionAsync as the expected
    /// version. F# code reads it with ValueLens.Value.
    static member VersionValue(version: Version) : int64 =
        ValueLens.Value version

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
    /// because recovery cannot replay a deferred event. A deferred event does not
    /// start a saga; running sagas still receive it.
    static member Defer<'TEvent when 'TEvent: not null>(event: 'TEvent) : EventAction<'TEvent> =
        EventAction.DeferEvent event

    /// Persist the event when `shouldPersist` is true; otherwise Defer it - the
    /// event is still published and folded but is not written to the journal or
    /// sent through a projection, and it does not start a saga. Its fold should
    /// preserve the current state.
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
    /// originator/saga name resolution and the saga-start handshake.
    member val PrefixConversion: PrefixConversion = PrefixConversion (Some id) with get, set
    /// The event to send to start the saga. Pass the originator's event unchanged: a saga
    /// recovered before it leaves Started asks the originator whether this exact event
    /// (its Id and Version) is the one it journaled, and ends when it is not.
    member val StartingEvent: obj | null = null with get, set

/// C#-friendly factory for PrefixConversion
type PrefixConversions =
    /// Identity conversion - uses originator prefix with saga suffix
    /// This creates saga IDs like: originatorId~Saga~correlationId
    static member Identity = PrefixConversion (Some id)
    /// Custom conversion of the correlation id part of the saga name: originatorId~Saga~f(correlationId).
    /// The saga reads its correlation id from the end of its name, so `f` must return the correlation id,
    /// optionally after a prefix that ends with `~`, for example `cid => "audit~" + cid`. FCQRS
    /// logs an error and does not start the saga when `f` changes or drops the correlation id, or throws.
    static member Custom(f: Func<string, string>) = PrefixConversion (Some f.Invoke)

/// C#-friendly Actor API
type ActorApi =
    /// Create an actor system using the selected SQL journal provider.
    /// Install that provider's ADO.NET driver in the application (for example Npgsql for PostgreSQL).
    static member Create(
        configuration: Microsoft.Extensions.Configuration.IConfiguration,
        loggerFactory: Microsoft.Extensions.Logging.ILoggerFactory,
        connectionString: string,
        clusterName: string,
        databaseType: Actor.DBType) : IActor =
        let connection : Actor.Connection =
            { ConnectionString = Values.CreateLongString connectionString; DBType = databaseType }
        Actor.api configuration loggerFactory (Some connection) (Values.CreateShortString clusterName)

    /// Create the actor system with SQLite connection
    static member Create(
        configuration: Microsoft.Extensions.Configuration.IConfiguration,
        loggerFactory: Microsoft.Extensions.Logging.ILoggerFactory,
        sqliteConnectionString: string,
        clusterName: string) : IActor =
        ActorApi.Create(configuration, loggerFactory, sqliteConnectionString, clusterName, Actor.DBType.Sqlite)

/// C#-friendly projection API. A projection follows each aggregate's and saga's own sequence
/// numbers, so it never skips a stored event. Without a name, it keeps its progress in memory and
/// reads the whole journal each time it starts. With a name, it stores its progress in the journal
/// database under that name and resumes where it stopped; a handler can then see an event again
/// after a crash. A handler that throws terminates the process.
type QueryApi =
    static member private Progress(name: string | null) =
        match name with
        | null -> Projections.InMemory
        | name -> Projections.Stored name

    /// Starts a projection whose handler returns the notifications to publish (F# list).
    static member Init(
        actorApi: IActor,
        eventHandler: Func<obj, IMessageWithCID list>,
        [<Optional; DefaultParameterValue(null: string | null)>] name: string | null) : Projections.IProjection =
        Projections.startTracked actorApi (QueryApi.Progress name) eventHandler.Invoke

    /// Starts a projection whose handler returns the notifications to publish. An overload of
    /// Init, distinguished by the handler's return type.
    static member Init(
        actorApi: IActor,
        eventHandler: Func<obj, System.Collections.Generic.IList<IMessageWithCID>>,
        [<Optional; DefaultParameterValue(null: string | null)>] name: string | null) : Projections.IProjection =
        Projections.startTracked actorApi (QueryApi.Progress name) (fun evt -> eventHandler.Invoke evt |> List.ofSeq)

    /// Starts a projection with a single-event handler: the handler just updates the read model
    /// (returns void); each aggregate event is then published to subscribers as-is. Use a
    /// list-returning overload when notifications must be filtered or transformed.
    static member Init(
        actorApi: IActor,
        eventHandler: Action<obj>,
        [<Optional; DefaultParameterValue(null: string | null)>] name: string | null) : Projections.IProjection =
        Projections.startTracked actorApi (QueryApi.Progress name) (Query.autoPublish eventHandler.Invoke)

    /// Starts a projection with a filtered single-event handler: the handler updates the read
    /// model and returns Publish/Suppress to say whether this event wakes subscribers.
    static member Init(
        actorApi: IActor,
        eventHandler: Func<obj, Notify>,
        [<Optional; DefaultParameterValue(null: string | null)>] name: string | null) : Projections.IProjection =
        Projections.startTracked actorApi (QueryApi.Progress name) (Query.filterPublish eventHandler.Invoke)

/// Low-level wiring over an IActor: register the saga-starter, aggregates and
/// actors, and send commands. Most members are plain static helpers (you call
/// ActorWiring.Foo(actor, …)); a couple are genuine extension methods. For app
/// composition, prefer the host-builder API (AddFcqrs/AddAggregate/AddSaga).
[<Extension>]
type ActorWiring =
    /// Register a deterministic, one-to-one historical event conversion for this actor system.
    /// Register every conversion before initializing any aggregate, saga, or projection. Chains
    /// follow the envelope's declared payload type; duplicate sources and cycles are rejected.
    /// Applies during FCQRS journal recovery and projection reads. Envelope metadata and stored
    /// bytes are preserved; live messages and application-owned snapshot state are not converted.
    /// Keep historical payload types readable. A converter must not return null.
    /// Converters may run concurrently across consumers and must be thread-safe.
    [<Extension>]
    static member WithEventUpcaster<'Old, 'New when 'Old: not null and 'New: not null>(
        actor: IActor, convert: Func<'Old, 'New>) : IActor =
        FCQRS.EventUpcasting.Internal.register actor.System convert
        actor

    /// Initialize saga starter with no sagas (for simple scenarios)
    [<Extension>]
    static member InitializeSagaStarterEmpty(actor: IActor) : unit =
        actor.InitializeSagaStarter(fun _ -> ([] : list<(string -> Akkling.Cluster.Sharding.IEntityRef<obj>) * PrefixConversion * obj>))

    /// Static helper for C# where extension may not resolve
    static member InitSagaStarterEmpty(actor: IActor) : unit =
        actor.InitializeSagaStarter(fun _ -> ([] : list<(string -> Akkling.Cluster.Sharding.IEntityRef<obj>) * PrefixConversion * obj>))

    /// C#-friendly InitializeSagaStarter that accepts Func returning IList of SagaDefinition.
    /// A null list means "no sagas". Null Factory/StartingEvent members fail with a
    /// named error: the aggregate runs this handler for each event it stores, and an
    /// exception there terminates the process, so the error must say what was wrong.
    static member InitSagaStarter(
        actor: IActor,
        eventHandler: Func<obj, System.Collections.Generic.IList<SagaDefinition> | null>) : unit =
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

                    (def.Factory.Invoke, def.PrefixConversion, def.StartingEvent |> Unchecked.nonNull))
                |> List.ofSeq

        actor.InitializeSagaStarter(handler)

    /// C#-friendly simplified InitializeSagaStarter where you just return factories.
    /// A null list means "no sagas"; a null factory fails with a named error.
    static member InitSagaStarterSimple(
        actor: IActor,
        eventHandler: Func<obj, System.Collections.Generic.IList<AggregateFactory> | null>) : unit =
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
        actor.InitializeActor initialState entityName cmdHandler evtApplier SnapshotPolicy.Default PassivationPolicy.Default

    /// InitActor with an explicit per-aggregate snapshot policy.
    static member InitActor<'TState, 'TCommand, 'TEvent when 'TEvent: not null>(
        actor: IActor,
        initialState: 'TState,
        entityName: string,
        handleCommand: Func<Command<'TCommand>, 'TState, EventAction<'TEvent>>,
        applyEvent: Func<Event<'TEvent>, 'TState, 'TState>,
        snapshotPolicy: SnapshotPolicy) : Akkling.Cluster.Sharding.EntityFac<obj> =
        ActorWiring.InitActor<'TState, 'TCommand, 'TEvent>(
            actor, initialState, entityName, handleCommand, applyEvent, snapshotPolicy, PassivationPolicy.Default)

    /// InitActor with explicit per-aggregate snapshot and idle-passivation policies.
    static member InitActor<'TState, 'TCommand, 'TEvent when 'TEvent: not null>(
        actor: IActor,
        initialState: 'TState,
        entityName: string,
        handleCommand: Func<Command<'TCommand>, 'TState, EventAction<'TEvent>>,
        applyEvent: Func<Event<'TEvent>, 'TState, 'TState>,
        snapshotPolicy: SnapshotPolicy,
        passivationPolicy: PassivationPolicy) : Akkling.Cluster.Sharding.EntityFac<obj> =
        let cmdHandler cmd state = handleCommand.Invoke(cmd, state)
        let evtApplier evt state = applyEvent.Invoke(evt, state)
        actor.InitializeActor initialState entityName cmdHandler evtApplier snapshotPolicy passivationPolicy

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
        ActorWiring.InitAggregate<'TState, 'TCommand, 'TEvent>(
            actor, initialState, entityName, handleCommand, applyEvent, snapshotPolicy, PassivationPolicy.Default)

    /// InitAggregate with explicit per-aggregate snapshot and idle-passivation policies.
    static member InitAggregate<'TState, 'TCommand, 'TEvent when 'TEvent: not null>(
        actor: IActor,
        initialState: 'TState,
        entityName: string,
        handleCommand: Func<Command<'TCommand>, 'TState, EventAction<'TEvent>>,
        applyEvent: Func<Event<'TEvent>, 'TState, 'TState>,
        snapshotPolicy: SnapshotPolicy,
        passivationPolicy: PassivationPolicy) : AggregateRefs<'TCommand, 'TEvent> =
        let fac =
            ActorWiring.InitActor<'TState, 'TCommand, 'TEvent>(
                actor, initialState, entityName, handleCommand, applyEvent, snapshotPolicy, passivationPolicy)
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
        ActorWiring.InitActorWithRunner<'TState, 'TCommand, 'TEvent>(
            actor, initialState, entityName, handleCommand, applyEvent, runner, snapshotPolicy, PassivationPolicy.Default)

    /// InitActorWithRunner with an explicit idle-passivation policy. RunAsync work is
    /// ephemeral, so an aggregate that dispatches long effects is a candidate for a
    /// longer idle timeout: passivation drops work still in flight.
    static member InitActorWithRunner<'TState, 'TCommand, 'TEvent when 'TEvent: not null>(
        actor: IActor,
        initialState: 'TState,
        entityName: string,
        handleCommand: Func<Command<'TCommand>, 'TState, EventAction<'TEvent>>,
        applyEvent: Func<Event<'TEvent>, 'TState, 'TState>,
        runner: Func<obj, Task<obj>>,
        snapshotPolicy: SnapshotPolicy,
        passivationPolicy: PassivationPolicy) : Akkling.Cluster.Sharding.EntityFac<obj> =
        let cmdHandler cmd state = handleCommand.Invoke(cmd, state)
        let evtApplier evt state = applyEvent.Invoke(evt, state)
        let boxedRunner: obj -> Async<obj> = fun description -> runner.Invoke description |> Async.AwaitTask
        actor.InitializeActorWithRunner initialState entityName cmdHandler evtApplier snapshotPolicy passivationPolicy (Some boxedRunner)

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
        ActorWiring.InitAggregateWithEffects<'TState, 'TCommand, 'TEvent>(
            actor, initialState, entityName, handleCommand, applyEvent, runner, snapshotPolicy, PassivationPolicy.Default)

    /// InitAggregateWithEffects with an explicit idle-passivation policy.
    static member InitAggregateWithEffects<'TState, 'TCommand, 'TEvent when 'TEvent: not null>(
        actor: IActor,
        initialState: 'TState,
        entityName: string,
        handleCommand: Func<Command<'TCommand>, 'TState, EventAction<'TEvent>>,
        applyEvent: Func<Event<'TEvent>, 'TState, 'TState>,
        runner: Func<obj, Task<obj>>,
        snapshotPolicy: SnapshotPolicy,
        passivationPolicy: PassivationPolicy) : AggregateRefs<'TCommand, 'TEvent> =
        let fac =
            ActorWiring.InitActorWithRunner<'TState, 'TCommand, 'TEvent>(
                actor, initialState, entityName, handleCommand, applyEvent, runner, snapshotPolicy, passivationPolicy)
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

    /// Send only when the aggregate's persisted version equals expectedVersion (initially zero).
    /// A mismatch faults with AggregateVersionConflictException before the handler or filter runs.
    /// The check and handler run in one actor turn. Deferred replies do not advance the version;
    /// persisted batches advance it per event. Stash and RunAsync continuations recheck the version.
    /// This does not deduplicate commands or wait for a projection. A timeout does not undo a write.
    /// A caller-built PublishEvent reply must retain the incoming command's Id and CorrelationId.
    [<Extension>]
    static member SendIfVersionAsync<'TEvent, 'TCommand when 'TEvent: not null>(
        actor: IActor,
        entityFactory: AggregateFactory,
        expectedVersion: int64,
        cid: CID,
        aggregateId: AggregateId,
        command: 'TCommand,
        filter: Func<'TEvent, bool>) : Task<Event<'TEvent>> =
        ActorWiring.SendIfVersionAsync(actor, entityFactory, expectedVersion, cid, aggregateId, command, filter,
            System.Threading.CancellationToken.None)

    /// Send a conditional command with cancellation of the caller's wait. An already-canceled
    /// token prevents starting the request. Once started, cancellation does not undo processing.
    /// A mismatch faults with AggregateVersionConflictException independently of the event filter.
    [<Extension>]
    static member SendIfVersionAsync<'TEvent, 'TCommand when 'TEvent: not null>(
        actor: IActor,
        entityFactory: AggregateFactory,
        expectedVersion: int64,
        cid: CID,
        aggregateId: AggregateId,
        command: 'TCommand,
        filter: Func<'TEvent, bool>,
        cancellationToken: System.Threading.CancellationToken) : Task<Event<'TEvent>> =
        FCQRS.Actor.Internal.createConditionalCommandSubscription actor entityFactory.Invoke expectedVersion cid aggregateId command filter.Invoke
        |> fun work -> Async.StartAsTask(work, cancellationToken = cancellationToken)

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

    /// Optional expectation for a waiting state (null = none). Requires
    /// Transition = Stay; the framework sends the expectation's Resend commands
    /// on state entry, re-sends them on the schedule, and delivers
    /// ExpectationExhausted to HandleEvent past the deadline.
    member val Expect: Expectation = Unchecked.defaultof<Expectation> with get, set

    /// Convert commands to F# list for internal use
    member this.CommandsList = this.Commands |> List.ofSeq

    /// The transition with Expect folded in (internal use).
    member this.EffectiveTransition: SagaTransition<'TState> =
        match box this.Expect with
        | null -> this.Transition
        | _ ->
            match this.Transition with
            | Stay -> StayExpecting this.Expect
            | _ ->
                invalidOp
                    "SagaSideEffectResult.Expect requires Transition = Stay: an expectation declares what the current state is waiting for."

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

    /// Stay in the current state with a declared expectation (alternative to
    /// setting SagaSideEffectResult.Expect alongside Stay)
    static member StayExpecting<'TState>(expectation: Expectation) : SagaTransition<'TState> =
        SagaTransition.StayExpecting expectation

/// C#-friendly factories for saga expectation retry schedules
type RetrySchedules =
    /// Re-send at a fixed interval
    static member Fixed(interval: TimeSpan) : RetrySchedule = FixedInterval interval

    /// Re-send with exponential backoff (with jitter): first after `initial`,
    /// multiplied by `factor` each time, capped at `max`
    static member Backoff(initial: TimeSpan, factor: float, max: TimeSpan) : RetrySchedule =
        RetrySchedule.Backoff(initial, factor, max)

/// C#-friendly factory for saga expectations
type Expectations =
    /// Declare what a waiting state expects: `resend` is sent on state entry and
    /// re-sent on `retryEvery` until a transition is persisted; past `deadline`
    /// (measured from the persisted state-entry time) HandleEvent receives an
    /// ExpectationExhausted message it must answer with a transition.
    static member Create
        (resend: System.Collections.Generic.IList<ExecuteCommand>, deadline: TimeSpan, retryEvery: RetrySchedule)
        : Expectation =
        { Resend = List.ofSeq resend
          Deadline = deadline
          RetryEvery = retryEvery }

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

    /// Per-aggregate idle passivation. Override with PassivationPolicy.NewAfter(...)
    /// or PassivationPolicy.Never; the default defers to
    /// akka.cluster.sharding[.EntityName].passivate-idle-entity-after (120s).
    abstract member PassivationPolicy: PassivationPolicy
    default _.PassivationPolicy = PassivationPolicy.Default

    /// Register the aggregate with an explicit (already-resolved) snapshot policy.
    member this.Init(actorApi: IActor, snapshotPolicy: SnapshotPolicy) : AggregateRefs<'TCommand, 'TEvent> =
        this.Init(actorApi, snapshotPolicy, this.PassivationPolicy)

    /// Register the aggregate with explicit (already-resolved) snapshot and
    /// idle-passivation policies.
    member this.Init(actorApi: IActor, snapshotPolicy: SnapshotPolicy, passivationPolicy: PassivationPolicy) : AggregateRefs<'TCommand, 'TEvent> =
        ActorWiring.InitAggregate<'TState, 'TCommand, 'TEvent>(
            actorApi,
            this.InitialState,
            this.EntityName,
            Func<Command<'TCommand>, 'TState, EventAction<'TEvent>>(fun c s -> this.HandleCommand(c, s)),
            Func<Event<'TEvent>, 'TState, 'TState>(fun e s -> this.ApplyEvent(e, s)),
            snapshotPolicy,
            passivationPolicy)

    /// Register the aggregate and hand back its Factory + Handler.
    member this.Init(actorApi: IActor) : AggregateRefs<'TCommand, 'TEvent> =
        this.Init(actorApi, this.SnapshotPolicy, this.PassivationPolicy)

/// C#-friendly abstract base for a saga, the counterpart of the F# Saga definition. A concrete
/// saga supplies SagaName, InitialData, Originator, StartsOn, Start, HandleEvent and
/// ApplySideEffects; the base provides the wiring (Init/Factory) and the small transition DSL.
/// TData is data the saga carries in every state, TState its states, and TEvent the event type
/// of the originator, the aggregate whose stored event starts the saga.
/// Designed to be subclassed from C#.
[<AbstractClass>]
type Saga<'TData, 'TState, 'TEvent when 'TState: not null and 'TEvent: not null>() =
    abstract member InitialData: 'TData
    /// The saga's stored name, like an aggregate's EntityName. Changing it orphans stored sagas.
    abstract member SagaName: string
    /// The aggregate whose stored event starts the saga, and the target of ToOriginator.
    abstract member Originator: AggregateFactory
    /// Whether a stored originator event starts an instance of this saga.
    abstract member StartsOn: Event<'TEvent> -> bool
    /// Handles a message that arrives before the saga has a state: normally the event that
    /// started it. Return StateChanged with the first state, or Unhandled.
    abstract member Start: obj * 'TData -> EventAction<'TState>
    /// Handles a message once the saga has a state: events from the aggregates it sends commands
    /// to, ExpectationExhausted, and the saga's own ToSelf messages.
    abstract member HandleEvent: obj * SagaState<'TData, 'TState> -> EventAction<'TState>
    /// Returns the transition and commands for a stored state. It runs again after recovery;
    /// the bool says whether the saga is recovering.
    abstract member ApplySideEffects: SagaState<'TData, 'TState> * bool -> SagaSideEffectResult<'TState>

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
        // The F# definition passes the state as an option; C# receives the two cases as two members.
        let handleEvent (message: obj) (saga: SagaState<'TData, 'TState option>) =
            match saga.State with
            | None -> this.Start(message, saga.Data)
            | Some state -> this.HandleEvent(message, { Data = saga.Data; State = state })

        let applySideEffects (saga: SagaState<'TData, 'TState>) (recovering: bool) =
            let result = this.ApplySideEffects(saga, recovering)
            result.EffectiveTransition, result.CommandsList

        let originator = this.Originator
        SagaBuilder.initSimple<'TData, 'TState, 'TEvent>
            actorApi
            this.InitialData
            handleEvent
            applySideEffects
            id
            originator.Invoke
            this.SagaName
            snapshotPolicy

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

    /// The start rule the saga-starter evaluates for each stored event.
    member internal this.StartRule(message: obj) : bool =
        match message with
        | :? Event<'TEvent> as event -> this.StartsOn event
        | _ -> false
