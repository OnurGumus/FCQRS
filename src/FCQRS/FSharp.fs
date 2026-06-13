/// Idiomatic-F# functional facade for FCQRS.
///
/// Gives F# consumers the same one-call ergonomics the C# host-builder
/// (HostExtensions.fs) gives C#, but with F# idioms: records-of-functions for the
/// definitions, typed handles for the results, an explicit wiring pipeline, and
/// plain helpers for saga side effects. It is a *pure addition* — it wraps only the
/// existing primitives (IActor.InitializeActor / SagaBuilder.initSimple / Query.init
/// / InitializeSagaStarter / CreateCommandSubscription / Actor.api) and changes
/// nothing in the C# interop layer or the core.
///
///     open FCQRS.FSharp
///     let api       = Fcqrs.actor config loggerFactory (Some (Fcqrs.connect DBType.Sqlite conn)) "Cluster"
///     let documents = Fcqrs.aggregate api { Name="Document"; Initial=...; Decide=...; Fold=... }
///     let users     = Fcqrs.aggregate api { Name="User"; ... }
///     let quota     = Fcqrs.saga api (quotaDef documents.Factory users.Factory)
///     Fcqrs.wireSagaStarters api [ quota ]
///     let subs      = Fcqrs.projection api (Projection.single 0 updateReadModel)
///     // (Projection.multi when you must control which notifications publish)
///     // send a command and await the resulting event (read-your-writes):
///     let! ev = documents.Send (Fcqrs.newCid()) (Fcqrs.aggregateId id) cmd (fun e -> ...)
module FCQRS.FSharp

open Akkling.Cluster.Sharding
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Common
open FCQRS.Model.Data

/// An aggregate's entity-ref factory: an id -> its sharded actor ref.
type AggregateFactory = string -> IEntityRef<obj>

// ---------------------------------------------------------------------------
// Definitions (records of functions) + the handles returned after registration.
// ---------------------------------------------------------------------------

/// A pure aggregate definition: the four things that define an event-sourced
/// aggregate. No wiring, no IActor.
type Aggregate<'State, 'Command, 'Event when 'Event: not null> =
    { Name: string
      Initial: 'State
      /// handleCommand (decide): command + current state -> what to do.
      Decide: Command<'Command> -> 'State -> EventAction<'Event>
      /// applyEvent (fold): event + current state -> next state (pure).
      Fold: Event<'Event> -> 'State -> 'State
      /// Snapshot cadence: Default (config / 30), NoSnapshots, or Every n.
      Snapshots: SnapshotPolicy }

/// What you get back after registering an aggregate.
type AggregateHandle<'Command, 'Event when 'Event: not null> =
    { /// Entity-ref factory (DEFAULT_SHARD applied) — hand this to a saga to target it.
      Factory: AggregateFactory
      /// Send a command and await the first matching event (read-your-writes).
      Send: CID -> AggregateId -> 'Command -> ('Event -> bool) -> Async<Event<'Event>> }

/// A pure saga definition. HandleEvent is obj-based so a single saga can match
/// events from several aggregates; ApplySideEffects returns the transition plus the
/// commands to dispatch.
type Saga<'Data, 'State, 'OriginatorEvent when 'State: not null and 'OriginatorEvent: not null> =
    { Name: string
      InitialData: 'Data
      /// The aggregate the saga starts from (its commands' Originator target).
      Originator: AggregateFactory
      HandleEvent: obj -> SagaState<'Data, 'State option> -> EventAction<'State>
      ApplySideEffects: SagaState<'Data, 'State> -> bool -> SagaTransition<'State> * ExecuteCommand list
      /// Which originator events spawn an instance of this saga. Typed to the
      /// originator's event so 'OriginatorEvent is inferred from the definition —
      /// there is no type argument to remember (or to get silently wrong).
      StartOn: Event<'OriginatorEvent> -> bool
      /// Snapshot cadence: Default (config / 30), NoSnapshots, or Every n.
      Snapshots: SnapshotPolicy }

/// What you get back after registering a saga.
type SagaHandle =
    { Factory: AggregateFactory
      StartOn: obj -> bool }

/// A read-model projection definition.
type Projection =
    { /// Resume from this journal offset (e.g. the last committed offset).
      LastOffset: int
      /// Called per persisted event; returns the read-model events to publish
      /// to subscribers (empty list = nothing to notify).
      Handle: int64 -> obj -> IMessageWithCID list }

/// Constructors for the two projection-handler shapes.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Projection =

    /// Multi-event handler: full control — return exactly the notifications to
    /// publish (empty list = nothing). Use when notifications must be filtered
    /// or transformed, e.g. suppressing intermediate events so read-your-writes
    /// only wakes on the final one.
    let multi (lastOffset: int) (handle: int64 -> obj -> IMessageWithCID list) : Projection =
        { LastOffset = lastOffset; Handle = handle }

    /// Single-event handler: just update the read model (returns unit); each
    /// aggregate event is then published to subscribers as-is. The common case
    /// when every event is worth notifying.
    let single (lastOffset: int) (handle: int64 -> obj -> unit) : Projection =
        { LastOffset = lastOffset
          Handle = FCQRS.Query.autoPublish handle }

    /// Filtered single-event handler: update the read model, then return
    /// Publish/Suppress to decide whether *this* event wakes subscribers. The
    /// middle rung between `single` (always publish) and `multi` (return an
    /// arbitrary notification list) — reach for it in the common "publish each
    /// event except the intermediate ones" case, without building a list.
    let filtered (lastOffset: int) (handle: int64 -> obj -> Notify) : Projection =
        { LastOffset = lastOffset
          Handle = FCQRS.Query.filterPublish handle }

// ---------------------------------------------------------------------------
// Saga side-effect command builders (wrap ExecuteCommand / TargetActor).
// ---------------------------------------------------------------------------

/// Send a command back to the saga's originator aggregate.
let toOriginator (factory: AggregateFactory) (command: obj) : ExecuteCommand =
    { TargetActor = FactoryAndName { Factory = factory; Name = Originator }
      Command = command
      DelayInMs = None }

/// Send a command to a specific aggregate instance by id (cross-aggregate).
let toAggregate (factory: AggregateFactory) (id: string) (command: obj) : ExecuteCommand =
    { TargetActor = FactoryAndName { Factory = factory; Name = Name id }
      Command = command
      DelayInMs = None }

/// Send a command to a concrete actor ref.
let toActor (actorRef: Akkling.ActorRefs.IActorRef<obj>) (command: obj) : ExecuteCommand =
    { TargetActor = ActorRef actorRef; Command = command; DelayInMs = None }

/// Send a command to the saga itself (raw, lands in HandleEvent) — e.g. timeouts.
let toSelf (command: obj) : ExecuteCommand =
    { TargetActor = Self; Command = command; DelayInMs = None }

/// Delayed variant of toOriginator (delayMs, taskName key).
let toOriginatorAfter (factory: AggregateFactory) (delayMs: int64) (taskName: string) (command: obj) : ExecuteCommand =
    { TargetActor = FactoryAndName { Factory = factory; Name = Originator }
      Command = command
      DelayInMs = Some(delayMs, taskName) }

/// Delayed variant of toAggregate.
let toAggregateAfter (factory: AggregateFactory) (id: string) (delayMs: int64) (taskName: string) (command: obj) : ExecuteCommand =
    { TargetActor = FactoryAndName { Factory = factory; Name = Name id }
      Command = command
      DelayInMs = Some(delayMs, taskName) }

/// Delayed variant of toActor.
let toActorAfter (actorRef: Akkling.ActorRefs.IActorRef<obj>) (delayMs: int64) (taskName: string) (command: obj) : ExecuteCommand =
    { TargetActor = ActorRef actorRef
      Command = command
      DelayInMs = Some(delayMs, taskName) }

/// Schedule a message to the saga itself after a delay — the idiomatic saga
/// timeout: enter a state, toSelfAfter a reminder, and HandleEvent decides
/// whether it still matters when it arrives.
let toSelfAfter (delayMs: int64) (taskName: string) (command: obj) : ExecuteCommand =
    { TargetActor = Self
      Command = command
      DelayInMs = Some(delayMs, taskName) }

// ---------------------------------------------------------------------------
// Optional pipe-friendly aliases. The DU cases (PersistEvent, StateChangedEvent,
// Stay, StopSaga ...) work directly too; these just read nicely in pipelines.
// ---------------------------------------------------------------------------

/// A (type, stable-journal-name) pair for Fcqrs.journalTypes.
let journalType<'T> (name: string) : System.Type * string = typeof<'T>, name

let persist (event: 'e) : EventAction<'e> = PersistEvent event
let persistAll (events: 'e list) : EventAction<'e> = PersistAllEvents events
let persistAndSnapshot (event: 'e) : EventAction<'e> = PersistAndSnapshot event
let defer (event: 'e) : EventAction<'e> = DeferEvent event
let transitionTo (state: 'state) : EventAction<'state> = StateChangedEvent state
let stay<'state> : SagaTransition<'state> = Stay
let stop<'state> : SagaTransition<'state> = StopSaga
let nextState (state: 'state) : SagaTransition<'state> = NextState state

// ---------------------------------------------------------------------------
// The verbs: build the actor system, register aggregates/sagas/projections.
// ---------------------------------------------------------------------------

module Fcqrs =

    let private short (s: string) : ShortString =
        s |> ValueLens.TryCreate |> Result.value

    /// The single place DEFAULT_SHARD / RefFor is applied (mirrors CSharpInterop).
    let private refFor (fac: EntityFac<obj>) : AggregateFactory =
        fun entityId -> fac.RefFor DEFAULT_SHARD entityId

    /// Build a SQLite/etc. Connection from a raw connection string (ShortString hidden).
    let connect (dbType: FCQRS.Actor.DBType) (connectionString: string) : FCQRS.Actor.Connection =
        { ConnectionString = short connectionString; DBType = dbType }

    /// Create the actor system from plain values (cluster name as a string).
    let actor (config: IConfiguration) (loggerFactory: ILoggerFactory) (connection: FCQRS.Actor.Connection option) (clusterName: string) : IActor =
        FCQRS.Actor.api config loggerFactory connection (short clusterName)

    /// A fresh correlation id (UUID v7).
    let newCid () : CID =
        System.Guid.CreateVersion7().ToString() |> ValueLens.CreateAsResult |> Result.value

    /// An aggregate id from a string (e.g. a document/user key).
    let aggregateId (s: string) : AggregateId =
        s |> ValueLens.CreateAsResult |> Result.value

    /// Register an aggregate and return its typed handle. Calling this IS the
    /// registration (it initializes the sharding region).
    let aggregate (api: IActor) (def: Aggregate<'State, 'Command, 'Event>) : AggregateHandle<'Command, 'Event> =
        let fac = api.InitializeActor def.Initial def.Name def.Decide def.Fold def.Snapshots
        let factory = refFor fac
        { Factory = factory
          Send = fun cid id command filter -> api.CreateCommandSubscription factory cid id command filter None }

    /// Register a saga and return its handle. The originator-event type is inferred
    /// from `def.StartOn`, so there are no type arguments to supply:
    /// `Fcqrs.saga api def`.
    let saga (api: IActor) (def: Saga<'Data, 'State, 'OriginatorEvent>) : SagaHandle =
        let fac =
            SagaBuilder.initSimple<'Data, 'State, 'OriginatorEvent>
                api
                def.InitialData
                def.HandleEvent
                def.ApplySideEffects
                id
                def.Originator
                def.Name
                def.Snapshots
        // Adapt the typed StartOn to the obj predicate the saga-starter feeds.
        let startOn (o: obj) =
            match o with
            | :? (Event<'OriginatorEvent>) as e -> def.StartOn e
            | _ -> false
        { Factory = refFor fac; StartOn = startOn }

    /// Wire every registered saga into one saga-starter (or the empty starter if
    /// none). Call after the aggregates + sagas are registered.
    let wireSagaStarters (api: IActor) (sagas: SagaHandle list) : unit =
        match sagas with
        | [] -> api.InitializeSagaStarter(fun (_: obj) -> ([]: (AggregateFactory) list))
        | sagas ->
            api.InitializeSagaStarter(fun (evt: obj) ->
                [ for s in sagas do
                      if s.StartOn evt then
                          yield s.Factory ])

    /// Register stable journal names for payload types (see JournalTypes):
    ///     Fcqrs.journalTypes [ journalType<Document.Event> "doc.event"; ... ]
    /// Call before the actor system writes anything.
    let journalTypes (mappings: (System.Type * string) list) : unit =
        for t, name in mappings do
            JournalTypes.Map(t, name)

    /// Register the read-model projection and return the subscription stream.
    let projection (api: IActor) (p: Projection) : FCQRS.Query.ISubscribe =
        FCQRS.Query.init api p.LastOffset p.Handle |> FCQRS.Query.asDefaultSubscribe
