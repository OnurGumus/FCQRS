namespace FCQRS

// Host-builder / DI ergonomics for consuming FCQRS from a modern .NET app.
//
// Instead of hand-rolling a composition root (create the actor system, Init each
// aggregate, build the saga, wire the saga-starter, start the projection), a C#
// app registers the pieces fluently and FCQRS owns the *ordering* and *startup*:
//
//     builder.Services
//         .AddFcqrs(connectionString, "MyCluster")
//         .AddAggregate<DocumentShard, DocumentState, DocumentCommand, DocumentEvent>()
//         .AddAggregate<UserShard, UserState, UserCommand, UserEvent>()
//         .AddSaga<QuotaSaga, DocumentEvent, QuotaSagaData, QuotaState>(
//             create:  sp => new QuotaSaga(sp.Aggregate<DocumentShard>().Factory,
//                                          sp.Aggregate<UserShard>().Factory,
//                                          sp.GetRequiredService<ILogger<QuotaSaga>>()),
//             startOn: e => e is Event<DocumentEvent> { EventDetails: DocumentEvent.CreateOrUpdateRequested })
//         .AddProjection((offset, evt) => Projection.HandleEventWrapper(lf, conn, offset, evt));
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

/// A type-erased handle to a registered aggregate: just its entity-ref factory,
/// which is all a saga needs to target it. Returned by IServiceProvider.Aggregate&lt;T&gt;().
type AggregateHandle = { Factory: Func<string, IEntityRef<obj>> }

/// Runtime registry, populated once at host startup. Holds the live actor system,
/// each aggregate's factory + refs (keyed by the aggregate's CLR type) and the
/// projection subscription. Resolved from DI so endpoints can pull the Handlers /
/// the subscription, and saga registrations can look up the aggregates they wire.
type FcqrsRuntime(actor: IActor) =
    let factories = Dictionary<Type, Func<string, IEntityRef<obj>>>()
    let refs = Dictionary<Type, obj>()

    /// The live actor system.
    member _.Actor = actor

    /// The projection subscription, set when the projection step runs at startup.
    member val Subscription: FCQRS.Query.ISubscribe<IMessageWithCID> | null = null with get, set

    /// Record an aggregate's wiring under its CLR type (called at startup).
    member _.Register(shardType: Type, factory: Func<string, IEntityRef<obj>>, boxedRefs: obj) =
        factories[shardType] <- factory
        refs[shardType] <- boxedRefs

    /// The entity-ref factory of a registered aggregate.
    member _.Factory(shardType: Type) : Func<string, IEntityRef<obj>> =
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
/// DI. Returned by IServiceCollection.AddFcqrs.
type FcqrsBuilder internal (services: IServiceCollection, connectionString: string, clusterName: string) =
    let aggregateSteps = ResizeArray<IServiceProvider -> IActor -> FcqrsRuntime -> unit>()
    let sagaSteps = ResizeArray<IServiceProvider -> IActor -> FcqrsRuntime -> unit>()
    let sagaStarters = ResizeArray<obj -> Func<string, IEntityRef<obj>> option>()
    let mutable projectionStep: (IServiceProvider -> IActor -> FCQRS.Query.ISubscribe<IMessageWithCID>) option = None

    // Registers the ISubscribe<> resolver in DI exactly once, on the first
    // AddProjection call. The subscription itself is created at startup.
    member private _.RegisterSubscriptionResolver() =
        if projectionStep.IsNone then
            services.AddSingleton<FCQRS.Query.ISubscribe<IMessageWithCID>>(fun (sp: IServiceProvider) ->
                match sp.GetRequiredService<FcqrsRuntime>().Subscription with
                | null -> failwith "Projection subscription is not initialized yet (the host has not started)."
                | s -> s)
            |> ignore

    /// The underlying service collection (so you can keep chaining .Add… on it).
    member _.Services = services
    member internal _.ConnectionString = connectionString
    member internal _.ClusterName = clusterName
    member internal _.AggregateSteps = aggregateSteps
    member internal _.SagaSteps = sagaSteps
    member internal _.SagaStarters = sagaStarters
    member internal _.ProjectionStep = projectionStep

    /// Register an aggregate. The shard is constructed via DI (ctor args resolved
    /// from the container) and Init'd at startup; its Handler and AggregateRefs are
    /// registered so endpoints/sagas can resolve them.
    member this.AddAggregate<'TShard, 'TState, 'TCommand, 'TEvent
            when 'TShard :> Aggregate<'TState, 'TCommand, 'TEvent>
            and 'TShard: not struct
            and 'TEvent: not null>() : FcqrsBuilder =
        aggregateSteps.Add(fun sp actor runtime ->
            let shard = ActivatorUtilities.CreateInstance(sp, typeof<'TShard>) :?> Aggregate<'TState, 'TCommand, 'TEvent>
            let refs = shard.Init actor
            runtime.Register(typeof<'TShard>, refs.Factory, refs :> obj))
        services.AddSingleton<AggregateRefs<'TCommand, 'TEvent>>(fun (sp: IServiceProvider) ->
            sp.GetRequiredService<FcqrsRuntime>().Refs<'TCommand, 'TEvent>(typeof<'TShard>))
        |> ignore
        services.AddSingleton<Handler<'TCommand, 'TEvent>>(fun (sp: IServiceProvider) ->
            sp.GetRequiredService<AggregateRefs<'TCommand, 'TEvent>>().Handler)
        |> ignore
        this

    /// Register a saga and the event that starts it. `create` builds the saga
    /// (use sp.Aggregate&lt;T&gt;().Factory to reference the aggregates it coordinates);
    /// `startOn` decides which originator events spawn an instance.
    member this.AddSaga<'TSaga, 'TEvent, 'TSagaData, 'TState
            when 'TSaga :> Saga<'TEvent, 'TSagaData, 'TState>
            and 'TEvent: not null
            and 'TState: not null>(
            create: Func<IServiceProvider, 'TSaga>,
            startOn: Func<obj, bool>) : FcqrsBuilder =
        sagaSteps.Add(fun sp actor _runtime ->
            let saga = create.Invoke sp
            let sagaFactory = saga.Factory actor
            sagaStarters.Add(fun evt -> if startOn.Invoke evt then Some sagaFactory else None))
        this

    /// Register the read-model projection, resuming from the given offset (default 0).
    member this.AddProjection(handler: Func<int64, obj, IList<IMessageWithCID>>, [<Optional; DefaultParameterValue(0)>] lastOffset: int) : FcqrsBuilder =
        this.RegisterSubscriptionResolver()
        projectionStep <- Some(fun _sp actor -> QueryApi.InitWithList(actor, lastOffset, handler))
        this

    /// Register the read-model projection, building the handler (and resuming offset)
    /// from DI. Use this overload when the projection needs services — e.g. an
    /// ILoggerFactory — so it resolves them the same way the actor system does.
    member this.AddProjection(
            handler: Func<IServiceProvider, Func<int64, obj, IList<IMessageWithCID>>>,
            lastOffset: Func<IServiceProvider, int>) : FcqrsBuilder =
        this.RegisterSubscriptionResolver()
        projectionStep <- Some(fun sp actor -> QueryApi.InitWithList(actor, lastOffset.Invoke sp, handler.Invoke sp))
        this

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

            // Then sagas (each records its start-trigger).
            for step in builder.SagaSteps do
                step sp actor runtime

            // One saga-starter over all registered sagas (or empty if none).
            if builder.SagaStarters.Count > 0 then
                let combined =
                    Func<obj, IList<Func<string, IEntityRef<obj>>>>(fun evt ->
                        let result = List<Func<string, IEntityRef<obj>>>()
                        for starter in builder.SagaStarters do
                            match starter evt with
                            | Some f -> result.Add f
                            | None -> ()
                        result :> IList<_>)
                IActorExtensions.InitSagaStarterSimple(actor, combined)
            else
                IActorExtensions.InitSagaStarterEmpty actor

            // Finally the projection (resumes from the provided offset).
            match builder.ProjectionStep with
            | Some step -> runtime.Subscription <- step sp actor
            | None -> ()

            Task.CompletedTask

        member _.StopAsync(_ct: CancellationToken) : Task =
            runtime.Actor.Stop()

/// `services.AddFcqrs(...)` and `serviceProvider.Aggregate&lt;T&gt;()`.
[<Extension>]
type FcqrsServiceCollectionExtensions =

    /// Register a SQLite-backed FCQRS actor system and the startup wiring. Returns
    /// a builder for fluent .AddAggregate / .AddSaga / .AddProjection registration.
    /// IConfiguration and ILoggerFactory are taken from the container.
    [<Extension>]
    static member AddFcqrs(services: IServiceCollection, connectionString: string, clusterName: string) : FcqrsBuilder =
        let builder = FcqrsBuilder(services, connectionString, clusterName)

        services.AddSingleton<IActor>(fun (sp: IServiceProvider) ->
            let config = sp.GetRequiredService<IConfiguration>()
            let loggerFactory = sp.GetRequiredService<ILoggerFactory>()
            ActorApi.Create(config, loggerFactory, connectionString, clusterName))
        |> ignore

        services.AddSingleton<FcqrsRuntime>(fun (sp: IServiceProvider) -> FcqrsRuntime(sp.GetRequiredService<IActor>()))
        |> ignore

        services.AddSingleton<FcqrsBuilder>(builder) |> ignore
        services.AddHostedService<FcqrsHostedService>() |> ignore
        builder

    /// Resolve a registered aggregate's entity-ref factory by its CLR type. Use
    /// inside a saga's `create` delegate to reference the aggregates it coordinates.
    [<Extension>]
    static member Aggregate<'TShard>(serviceProvider: IServiceProvider) : AggregateHandle =
        { Factory = serviceProvider.GetRequiredService<FcqrsRuntime>().Factory(typeof<'TShard>) }
