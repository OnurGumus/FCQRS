namespace FCQRS

// Host-builder / DI ergonomics for consuming FCQRS from a modern .NET app.
//
// Instead of hand-rolling a composition root (create the actor system, Init each
// aggregate, build the saga, wire the saga-starter, start the projection), a C#
// app registers the pieces fluently and FCQRS owns the *ordering* and *startup*:
//
//     builder.Services
//         .AddFcqrs(connectionString, "MyCluster")
//         .AddAggregate<DocumentShard>()
//         .AddAggregate<SlugShard>()
//         .AddSaga(sp => new PublicationSaga(sp.AggregateFactory<DocumentShard>(),
//                                            sp.AggregateFactory<SlugShard>()))
//
// (An aggregate's TState/TCommand/TEvent come off its Aggregate<,,> base; see
// FcqrsBuilderExtensions at the bottom. A saga's come off the class `create` returns.)
//         .AddProjection(evt => Projection.Handle(evt));
//
// The actual wiring runs once at host startup (an IHostedService), in the order
// aggregates -> sagas -> saga-starter -> projection, so a saga can resolve the
// factories of the aggregates it coordinates. Handler<,>, AggregateRefs<,> and
// the projection's ISubscribe<> are registered in DI, so endpoints inject them.

open System
open System.Threading
open System.Threading.Tasks
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Akkling.Cluster.Sharding
open FCQRS.Common
open FCQRS.Model.Data
open FCQRS.CSharp

/// Runtime registry, populated once at host startup. Holds the live actor system,
/// each aggregate's factory + refs (keyed by the aggregate's CLR type) and the
/// projection subscription. Resolved from DI so endpoints can pull the Handlers /
/// the subscription, and saga registrations can look up the aggregates they wire.
type FcqrsRuntime(actor: IActor) =
    let factories = Dictionary<Type, AggregateFactory>()
    let refs = Dictionary<Type, obj>()

    /// The live actor system.
    member _.Actor = actor

    /// The projection subscription, set when the projection step runs at startup.
    member val Subscription: FCQRS.Query.ISubscribe | null = null with get, set

    /// Record an aggregate's wiring under its CLR type (called at startup).
    member _.Register(shardType: Type, factory: AggregateFactory, boxedRefs: obj) =
        factories[shardType] <- factory
        refs[shardType] <- boxedRefs

    /// The entity-ref factory of a registered aggregate.
    member _.Factory(shardType: Type) : AggregateFactory =
        match factories.TryGetValue shardType with
        | true, f -> f
        | _ -> failwithf "Aggregate '%s' is not registered. Call AddAggregate for it before the saga that targets it." shardType.Name

    /// The typed refs of a registered aggregate.
    member _.Refs<'TCommand, 'TEvent when 'TEvent: not null>(shardType: Type) : AggregateRefs<'TCommand, 'TEvent> =
        match refs.TryGetValue shardType with
        | true, r -> r :?> AggregateRefs<'TCommand, 'TEvent>
        | _ -> failwithf "Aggregate '%s' is not registered." shardType.Name

/// Fluent registration builder. Each AddXxx records a step to run at startup and,
/// where relevant, registers the resolved piece (Handler, refs, subscription) in
/// DI. These services support constructor injection before startup; use their
/// operations after FCQRS's hosted service starts. Returned by IServiceCollection.AddFcqrs.
type FcqrsBuilder internal (services: IServiceCollection, connectionString: string, clusterName: string) =
    let upcasters = FCQRS.EventUpcasting.Internal.Registry()
    let aggregateSteps = ResizeArray<IServiceProvider -> IActor -> FcqrsRuntime -> unit>()
    // Each saga step registers its saga with the actor system it is given and returns
    // that system's start rule. StartAsync collects the rules for its own run: a
    // builder-wide list would keep rules bound to an earlier, stopped actor system.
    let sagaSteps = ResizeArray<IServiceProvider -> IActor -> FcqrsRuntime -> (obj -> AggregateFactory option)>()
    let mutable projectionStep: (IServiceProvider -> IActor -> FCQRS.Query.ISubscribe) option = None
    // Builder-level snapshot default: what an entity's SnapshotPolicy.Default
    // resolves to. Itself Default => fall through to the config key / 30.
    let mutable defaultSnapshotPolicy = SnapshotPolicy.Default
    // Akka-internal logging override (loglevel * also set stdout-loglevel).
    let mutable akkaLogging: (AkkaLogLevel * bool) option = None
    // Maps each Handler/AggregateRefs type pair <TCommand,TEvent> to the shard
    // types registered for it. Two aggregates sharing the same pair make the
    // UNKEYED DI registration ambiguous: MS DI resolves the last registration,
    // which would silently route commands to the wrong aggregate. The unkeyed
    // factories check this map at resolution time and fail loudly instead;
    // the keyed-by-shard registrations are always unambiguous.
    let pairToShards = Dictionary<Type * Type, ResizeArray<Type>>()

    // Registers the ISubscribe resolver in DI exactly once, on the first
    // AddProjection call. The subscription itself is created at startup.
    member private _.RegisterSubscriptionResolver() =
        if projectionStep.IsNone then
            // The host constructs all hosted services before starting any of them.
            // A worker can inject this forwarding subscription in its constructor;
            // resolve the live projection only when it actually subscribes.
            services.AddSingleton<FCQRS.Query.ISubscribe>(fun (sp: IServiceProvider) ->
                let runtime = sp.GetRequiredService<FcqrsRuntime>()
                let current () =
                    match runtime.Subscription with
                    | null -> failwith "Projection subscription is not initialized yet (the host has not started)."
                    | s -> s

                { new FCQRS.Query.ISubscribe with
                    member _.Subscribe(callback: IMessageWithCID -> unit, ?cancellationToken: CancellationToken) : IDisposable =
                        (current ()).Subscribe(callback, ?cancellationToken = cancellationToken)
                    member _.Subscribe(filter: IMessageWithCID -> bool, take: int, ?callback: IMessageWithCID -> unit, ?cancellationToken: CancellationToken) : FCQRS.Query.IAwaitableDisposable =
                        (current ()).Subscribe(filter, take, ?callback = callback, ?cancellationToken = cancellationToken)
                    member _.Subscribe(cid: CID, take: int, ?callback: IMessageWithCID -> unit, ?cancellationToken: CancellationToken) : FCQRS.Query.IAwaitableDisposable =
                        (current ()).Subscribe(cid, take, ?callback = callback, ?cancellationToken = cancellationToken)
                    member _.Subscribe(cid: CID, filter: IMessageWithCID -> bool, take: int, ?callback: IMessageWithCID -> unit, ?cancellationToken: CancellationToken) : FCQRS.Query.IAwaitableDisposable =
                        (current ()).Subscribe(cid, filter, take, ?callback = callback, ?cancellationToken = cancellationToken)

                  interface FCQRS.Query.IHasNotificationTimeout with
                      member _.Timeout =
                          match box (current ()) with
                          | :? FCQRS.Query.IHasNotificationTimeout as t -> t.Timeout
                          | _ -> TimeSpan.FromSeconds 30.0 })
            |> ignore
            // The closed generic, for consumers that inject ISubscribe<IMessageWithCID>
            // (resolves to the same instance, since ISubscribe : ISubscribe<IMessageWithCID>).
            services.AddSingleton<FCQRS.Query.ISubscribe<IMessageWithCID>>(fun (sp: IServiceProvider) ->
                sp.GetRequiredService<FCQRS.Query.ISubscribe>() :> FCQRS.Query.ISubscribe<IMessageWithCID>)
            |> ignore

    /// Set the builder-wide default snapshot cadence: every aggregate/saga whose
    /// own SnapshotPolicy is Default uses this instead. Per-entity overrides
    /// (Every n / NoSnapshots) always win; leaving this unset keeps the config
    /// key (config:akka:persistence:snapshot-version-count) / 30 fallback.
    member this.WithDefaultSnapshotPolicy(policy: SnapshotPolicy) : FcqrsBuilder =
        defaultSnapshotPolicy <- policy
        this

    /// Register stable journal names for payload types: manifests become
    /// "fcqrs:ev(doc.event)" instead of CLR type names, so types can
    /// be renamed/moved freely (update the mapping; old rows keep reading).
    member this.WithJournalTypes(configure: Action<JournalTypeMapBuilder>) : FcqrsBuilder =
        configure.Invoke(JournalTypeMapBuilder())
        this

    /// Register a deterministic, one-to-one historical event conversion. Chained conversions
    /// follow declared envelope payload types. Duplicate source registrations and cycles fail.
    /// The host installs these conversions before initializing aggregates, sagas, or projections.
    /// Each actor system has its own registry, fixed for its lifetime. Only FCQRS journal reads
    /// are converted; stored envelopes, live messages, and application-owned snapshot state are
    /// unchanged. Keep old payload types readable and register readers before deploying writers.
    /// Finish builder registrations before building or resolving the host; resolving IActor fixes
    /// this configuration. Converters may run concurrently and must be thread-safe.
    member this.WithEventUpcaster<'Old, 'New when 'Old: not null and 'New: not null>(
        convert: Func<'Old, 'New>) : FcqrsBuilder =
        upcasters.Register convert
        this

    member internal _.InstallUpcasters(actor: IActor) =
        FCQRS.EventUpcasting.Internal.install actor.System upcasters

    /// Enable Akka's internal logging (FCQRS ships it OFF). `level` maps to
    /// akka.loglevel; by default akka.stdout-loglevel is set to the same value.
    /// FCQRS's own logs are unaffected — they follow the host's ILoggerFactory.
    member this.WithAkkaLogging(level: AkkaLogLevel, [<Optional; DefaultParameterValue(true)>] includeStdout: bool) : FcqrsBuilder =
        akkaLogging <- Some(level, includeStdout)
        this

    /// The message-flow narrative — which command reached which aggregate and
    /// what it yielded, saga state transitions, the commands sagas issue — is
    /// written at Information level to the "FCQRS.MessageFlow" category and is
    /// ON by default: these lines describe your application's messages, not
    /// FCQRS internals. Turn it off here (a process-wide switch), or filter
    /// the category in your logging configuration.
    member this.WithMessageFlowLogging(enabled: bool) : FcqrsBuilder =
        Telemetry.MessageFlowLogging <- enabled
        this

    /// Whether message *payloads* appear in diagnostics detail — the span tags
    /// (command.type / event.type) and the message-flow log lines. ON by
    /// default. Span *names* are always low-cardinality case names regardless,
    /// so this never affects tracing rules or grouping. Turn it off for
    /// sensitive domains: tags and log lines then carry the case name only.
    member this.WithPayloadDiagnostics(enabled: bool) : FcqrsBuilder =
        Telemetry.IncludePayloads <- enabled
        this

    member internal _.AkkaLogging = akkaLogging

    member internal _.EffectiveSnapshotPolicy(entityPolicy: SnapshotPolicy) : SnapshotPolicy =
        match entityPolicy with
        | SnapshotPolicy.Default -> defaultSnapshotPolicy
        | p -> p

    /// The underlying service collection (so you can keep chaining .Add… on it).
    member _.Services = services
    member internal _.ConnectionString = connectionString
    member internal _.ClusterName = clusterName
    member internal _.AggregateSteps = aggregateSteps
    member internal _.SagaSteps = sagaSteps
    member internal _.ProjectionStep = projectionStep

    /// Register an aggregate. The shard is constructed via DI (ctor args resolved
    /// from the container) and Init'd at startup; its Handler and AggregateRefs are
    /// registered so endpoints/sagas can resolve them. Hosted services may inject
    /// these handles in their constructors and invoke them after FCQRS starts.
    member this.AddAggregate<'TShard, 'TState, 'TCommand, 'TEvent
            when 'TShard :> Aggregate<'TState, 'TCommand, 'TEvent>
            and 'TShard: not struct
            and 'TEvent: not null>() : FcqrsBuilder =
        let shardType = typeof<'TShard>
        let pair = (typeof<'TCommand>, typeof<'TEvent>)

        let shards =
            match pairToShards.TryGetValue pair with
            | true, list -> list
            | _ ->
                let list = ResizeArray<Type>()
                pairToShards.[pair] <- list
                list

        if not (shards.Contains shardType) then
            shards.Add shardType

        // Fails loudly when Handler<TCommand,TEvent> is ambiguous (see pairToShards).
        let ambiguityError () =
            let names =
                pairToShards.[pair] |> Seq.map _.Name |> String.concat ", "

            invalidOp (
                $"AggregateRefs/Handler<{typeof<'TCommand>.Name}, {typeof<'TEvent>.Name}> is ambiguous: "
                + $"aggregates [{names}] share the same command/event types. "
                + "Resolve the keyed registration for the shard you mean, e.g. "
                + "sp.GetKeyedService<Handler<...>>(typeof(MyShard)) or [FromKeyedServices(typeof(MyShard))]."
            )

        aggregateSteps.Add(fun sp actor runtime ->
            let shard = ActivatorUtilities.CreateInstance(sp, typeof<'TShard>) :?> Aggregate<'TState, 'TCommand, 'TEvent>
            let refs = shard.Init(actor, this.EffectiveSnapshotPolicy shard.SnapshotPolicy)
            runtime.Register(shardType, refs.Factory, refs :> obj))

        services.AddSingleton<AggregateRefs<'TCommand, 'TEvent>>(fun (sp: IServiceProvider) ->
            if pairToShards.[pair].Count > 1 then
                ambiguityError ()

            sp.GetRequiredKeyedService<AggregateRefs<'TCommand, 'TEvent>>(shardType))
        |> ignore

        services.AddSingleton<Handler<'TCommand, 'TEvent>>(fun (sp: IServiceProvider) ->
            if pairToShards.[pair].Count > 1 then
                ambiguityError ()

            sp.GetRequiredKeyedService<AggregateRefs<'TCommand, 'TEvent>>(shardType).Handler)
        |> ignore

        // Keyed-by-shard registrations are always unambiguous. Keep these handles
        // resolvable while the host constructs its services; actual operations
        // delegate to the wiring installed by FcqrsHostedService.StartAsync.
        services.AddKeyedSingleton<AggregateRefs<'TCommand, 'TEvent>>(shardType, fun (sp: IServiceProvider) (_: obj) ->
            let runtime = sp.GetRequiredService<FcqrsRuntime>()
            { Factory = AggregateFactory(fun entityId -> runtime.Factory(shardType).Invoke(entityId))
              Handler =
                  Handler<'TCommand, 'TEvent>(fun filter cid aggregateId command ->
                      runtime.Refs<'TCommand, 'TEvent>(shardType).Handler.Invoke(filter, cid, aggregateId, command)) })
        |> ignore

        services.AddKeyedSingleton<Handler<'TCommand, 'TEvent>>(shardType, fun (sp: IServiceProvider) (_: obj) ->
            sp.GetRequiredKeyedService<AggregateRefs<'TCommand, 'TEvent>>(shardType).Handler)
        |> ignore

        this

    /// Register a saga. `create` builds it; use sp.AggregateFactory&lt;T&gt;() to reference the
    /// aggregates it sends commands to. The saga's StartsOn decides which originator events
    /// start an instance. C# infers TData, TState and TEvent from the saga class `create`
    /// returns, so a call names no type arguments:
    ///
    ///     .AddSaga(sp => new Transfer(sp.AggregateFactory&lt;Account&gt;()))
    member this.AddSaga<'TData, 'TState, 'TEvent when 'TState: not null and 'TEvent: not null>(
            create: Func<IServiceProvider, Saga<'TData, 'TState, 'TEvent>>) : FcqrsBuilder =
        sagaSteps.Add(fun sp actor _runtime ->
            let saga = create.Invoke sp
            let sagaFactory = saga.Factory(actor, this.EffectiveSnapshotPolicy saga.SnapshotPolicy)
            fun evt -> if saga.StartRule evt then Some sagaFactory else None)
        this

    // Sets the projection step exactly once: a second AddProjection call would
    // otherwise silently discard the first projection, leaving its read model
    // stale with no error. Fail loudly instead.
    member private this.SetProjectionStep(step: IServiceProvider -> IActor -> FCQRS.Query.ISubscribe) =
        if projectionStep.IsSome then
            invalidOp
                "A projection is already registered. FCQRS supports one projection per host; \
                 combine handlers into a single AddProjection call instead of registering twice."

        projectionStep <- Some step

    // Starts the projection at host startup and registers it as IProjection, which also
    // serves ISubscribe resolution and read-your-writes waits.
    member private this.RegisterProjection(start: IServiceProvider -> IActor -> FCQRS.Projections.IProjection) =
        this.RegisterSubscriptionResolver()
        this.SetProjectionStep(fun sp actor -> start sp actor :> FCQRS.Query.ISubscribe)
        services.AddSingleton<FCQRS.Projections.IProjection>(fun (sp: IServiceProvider) ->
            let runtime = sp.GetRequiredService<FcqrsRuntime>()
            let current () =
                match runtime.Subscription with
                | :? FCQRS.Projections.IProjection as projection -> projection
                | _ -> invalidOp "The projection is not initialized yet (the host has not started)."
            let subs = sp.GetRequiredService<FCQRS.Query.ISubscribe>()
            { new FCQRS.Projections.IProjection with
                member _.CatchUpAsync() = (current ()).CatchUpAsync()
                member _.CatchUpAsync(ct) = (current ()).CatchUpAsync(ct)
                member _.Completion = (current ()).Completion
                member _.Dispose() =
                    match runtime.Subscription with
                    | :? FCQRS.Projections.IProjection as projection -> projection.Dispose()
                    | _ -> ()
                member _.Subscribe(callback, ?cancellationToken) = subs.Subscribe(callback, ?cancellationToken = cancellationToken)
                member _.Subscribe(filter: IMessageWithCID -> bool, take, ?callback, ?cancellationToken) = subs.Subscribe(filter, take, ?callback = callback, ?cancellationToken = cancellationToken)
                member _.Subscribe(cid: CID, take, ?callback, ?cancellationToken) = subs.Subscribe(cid, take, ?callback = callback, ?cancellationToken = cancellationToken)
                member _.Subscribe(cid: CID, filter: IMessageWithCID -> bool, take, ?callback, ?cancellationToken) = subs.Subscribe(cid, filter, take, ?callback = callback, ?cancellationToken = cancellationToken)

              // sendAwaiting reads the command timeout from here; without it the wait falls back to 30 s.
              interface FCQRS.Query.IHasNotificationTimeout with
                  member _.Timeout =
                      match box subs with
                      | :? FCQRS.Query.IHasNotificationTimeout as t -> t.Timeout
                      | _ -> TimeSpan.FromSeconds 30.0 })
        |> ignore
        this

    /// Register the read-model projection. It follows each aggregate's and saga's own sequence
    /// numbers, so it never skips a stored event. Without a name it keeps its progress in memory
    /// and reads the whole journal at each start, for a read model kept in memory. With a name it
    /// stores its progress in the journal database and resumes, and a handler can see an event
    /// again after a crash. A handler that throws terminates the process. The handler returns the
    /// notifications to publish. Resolve FCQRS.Projections.IProjection to wait for it.
    member this.AddProjection(
            handler: Func<obj, IList<IMessageWithCID>>,
            [<Optional; DefaultParameterValue(null: string | null)>] name: string | null) : FcqrsBuilder =
        this.RegisterProjection(fun _ actor -> QueryApi.Init(actor, handler, name))

    /// Register the read-model projection with a single-event handler: the handler just
    /// updates the read model (returns void); each aggregate event is then published to
    /// subscribers as-is. Use the list-returning overload when notifications must be
    /// filtered, e.g. suppressing intermediate events so read-your-writes only wakes on the final one.
    member this.AddProjection(
            handler: Action<obj>,
            [<Optional; DefaultParameterValue(null: string | null)>] name: string | null) : FcqrsBuilder =
        this.RegisterProjection(fun _ actor -> QueryApi.Init(actor, handler, name))

    /// Register the read-model projection with a filtered single-event handler: the handler
    /// updates the read model and returns Publish/Suppress per event to control whether it
    /// wakes subscribers.
    member this.AddProjection(
            handler: Func<obj, Notify>,
            [<Optional; DefaultParameterValue(null: string | null)>] name: string | null) : FcqrsBuilder =
        this.RegisterProjection(fun _ actor -> QueryApi.Init(actor, handler, name))

    /// Register the read-model projection, building the handler from DI. Use this overload
    /// when the projection needs services, e.g. an ILoggerFactory.
    member this.AddProjection(
            handler: Func<IServiceProvider, Func<obj, IList<IMessageWithCID>>>,
            [<Optional; DefaultParameterValue(null: string | null)>] name: string | null) : FcqrsBuilder =
        this.RegisterProjection(fun sp actor -> QueryApi.Init(actor, handler.Invoke sp, name))

    /// DI variant of the single-event handler overload.
    member this.AddProjection(
            handler: Func<IServiceProvider, Action<obj>>,
            [<Optional; DefaultParameterValue(null: string | null)>] name: string | null) : FcqrsBuilder =
        this.RegisterProjection(fun sp actor -> QueryApi.Init(actor, handler.Invoke sp, name))

    /// DI variant of the filtered single-event handler overload.
    member this.AddProjection(
            handler: Func<IServiceProvider, Func<obj, Notify>>,
            [<Optional; DefaultParameterValue(null: string | null)>] name: string | null) : FcqrsBuilder =
        this.RegisterProjection(fun sp actor -> QueryApi.Init(actor, handler.Invoke sp, name))

    /// Register a transactional projection with journal-wide CatchUpAsync support.
    /// The handler must write through the supplied connection and transaction and
    /// await all database work. FCQRS commits updates with durable contiguous progress.
    /// Resolve FCQRS.Projections.IProjection from DI to wait after an aggregate reply.
    /// Ordering is per persistence ID, and unprocessed journal history must be retained.
    member this.AddTransactionalProjection(
            options: FCQRS.Projections.TransactionalProjectionOptions,
            handler: Func<System.Data.Common.DbConnection, System.Data.Common.DbTransaction, Akka.Persistence.Query.EventEnvelope, Task>) : FcqrsBuilder =
        if isNull (box handler) then nullArg (nameof handler)
        this.RegisterProjection(fun _ actor ->
            FCQRS.Projections.start actor options (fun connection transaction envelope -> handler.Invoke(connection, transaction, envelope)))

/// The single startup step: creates the actor system (via the IActor singleton),
/// runs the recorded registration steps in order, wires the saga-starter from all
/// registered sagas, and starts the projection. Stops the actor system on shutdown.
type internal FcqrsHostedService(sp: IServiceProvider, builder: FcqrsBuilder, runtime: FcqrsRuntime) =
    interface IHostedService with
        member _.StartAsync(_ct: CancellationToken) : Task =
            let actor = runtime.Actor

            // Aggregates first — a saga's `create` resolves their factories.
            for step in builder.AggregateSteps do
                step sp actor runtime

            // Then sagas (each returns its start-trigger for this actor system).
            let sagaStarters = [ for step in builder.SagaSteps -> step sp actor runtime ]

            // One saga-starter over all registered sagas (or empty if none).
            if not sagaStarters.IsEmpty then
                let combined =
                    Func<obj, IList<AggregateFactory>>(fun evt ->
                        let result = List<AggregateFactory>()
                        for starter in sagaStarters do
                            match starter evt with
                            | Some f -> result.Add f
                            | None -> ()
                        result :> IList<_>)
                ActorWiring.InitSagaStarterSimple(actor, combined)
            else
                ActorWiring.InitSagaStarterEmpty actor

            // Finally the projection (resumes from the provided offset).
            match builder.ProjectionStep with
            | Some step -> runtime.Subscription <- step sp actor
            | None -> ()

            Task.CompletedTask

        member _.StopAsync(_ct: CancellationToken) : Task =
            match runtime.Subscription with
            | :? FCQRS.Projections.IProjection as projection -> projection.Dispose()
            | _ -> ()
            runtime.Actor.Stop()

/// `services.AddFcqrs(...)` and `serviceProvider.Aggregate&lt;T&gt;()`.
[<Extension>]
type FcqrsServiceCollectionExtensions =

    /// Register a SQLite-backed FCQRS actor system and the startup wiring. Returns
    /// a builder for fluent .AddAggregate / .AddSaga / .AddProjection registration.
    /// IConfiguration and ILoggerFactory are taken from the container.
    [<Extension>]
    static member AddFcqrs(services: IServiceCollection, connectionString: string, clusterName: string) : FcqrsBuilder =
        FcqrsServiceCollectionExtensions.AddFcqrs(services, connectionString, clusterName, Actor.DBType.Sqlite)

    /// Register FCQRS with the selected SQL journal provider and startup wiring.
    /// Install the application's ADO.NET provider, such as Npgsql for PostgreSQL.
    [<Extension>]
    static member AddFcqrs(services: IServiceCollection, connectionString: string, clusterName: string, databaseType: Actor.DBType) : FcqrsBuilder =
        let builder = FcqrsBuilder(services, connectionString, clusterName)

        services.AddSingleton<IActor>(fun (sp: IServiceProvider) ->
            let baseConfig = sp.GetRequiredService<IConfiguration>()
            let loggerFactory = sp.GetRequiredService<ILoggerFactory>()

            // Overlay the builder's Akka logging choice (in-memory keys win
            // because they are added after the host configuration).
            let config =
                match builder.AkkaLogging with
                | Some(level, includeStdout) ->
                    let kv (k: string) (v: string) = KeyValuePair<string, string | null>(k, v)

                    let overrides =
                        [ kv "config:akka:loglevel" (level.ToHocon())
                          if includeStdout then
                              kv "config:akka:stdout-loglevel" (level.ToHocon()) ]

                    ConfigurationBuilder().AddConfiguration(baseConfig).AddInMemoryCollection(overrides).Build()
                    :> IConfiguration
                | None -> baseConfig

            let actor = ActorApi.Create(config, loggerFactory, connectionString, clusterName, databaseType)
            try
                builder.InstallUpcasters actor
                actor
            with _ ->
                actor.Stop().GetAwaiter().GetResult()
                reraise ())
        |> ignore

        services.AddSingleton<FcqrsRuntime>(fun (sp: IServiceProvider) -> FcqrsRuntime(sp.GetRequiredService<IActor>()))
        |> ignore

        services.AddSingleton<FcqrsBuilder>(builder) |> ignore
        services.AddHostedService<FcqrsHostedService>() |> ignore
        builder

    /// Resolve a registered aggregate's entity-ref factory by its CLR type. Use
    /// inside a saga's `create` delegate to reference the aggregates it coordinates.
    [<Extension>]
    static member AggregateFactory<'TShard>(serviceProvider: IServiceProvider) : AggregateFactory =
        serviceProvider.GetRequiredService<FcqrsRuntime>().Factory(typeof<'TShard>)

[<AutoOpen>]
module private BaseTypeArgs =
    /// Walk the inheritance chain to the closed generic base built from
    /// `definition` (Aggregate<,,>) and return its type arguments.
    let rec baseArgs (definition: Type) (t: Type | null) : Type[] option =
        match t with
        | null -> None
        | t when t.IsGenericType && t.GetGenericTypeDefinition() = definition -> Some(t.GetGenericArguments())
        | t -> baseArgs definition t.BaseType

/// The single-type-argument form of AddAggregate. The concrete class already names its
/// state, command and event types on its Aggregate<,,> base, so registration repeats none of them:
///
///     .AddAggregate<Account>()
///
/// Reflection runs once per registration, while the host is being composed, and never on the
/// message path. The four-type-argument instance overload remains for classes that acquire the
/// base generically.
[<Extension>]
type FcqrsBuilderExtensions =

    /// Register an aggregate naming only its class; TState/TCommand/TEvent are
    /// read off its Aggregate&lt;TState, TCommand, TEvent&gt; base.
    [<Extension>]
    static member AddAggregate<'TShard when 'TShard: not struct>(builder: FcqrsBuilder) : FcqrsBuilder =
        match baseArgs typedefof<Aggregate<obj, obj, obj>> typeof<'TShard> with
        | Some args ->
            let m = typeof<FcqrsBuilder>.GetMethod "AddAggregate" |> Unchecked.nonNull

            m.MakeGenericMethod([| typeof<'TShard>; args[0]; args[1]; args[2] |]).Invoke(builder, [||])
            |> Unchecked.nonNull
            :?> FcqrsBuilder
        | None ->
            invalidOp
                $"{typeof<'TShard>.Name} does not derive from Aggregate<TState, TCommand, TEvent> — inherit the base class, or use the four-type-argument AddAggregate."
