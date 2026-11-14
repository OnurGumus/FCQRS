module HostingAndRegistryTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Expecto
open FCQRS
open FCQRS.Common
open FCQRS.CSharp
open FCQRS.Model.Data

type HostedAggregate() =
    inherit Aggregate<int, int, int>()
    override _.InitialState = 0
    override _.EntityName = "HostedInjectionRegression"
    override _.HandleCommand(command, state) = PersistEvent(state + command.CommandDetails)
    override _.ApplyEvent(event, _) = event.EventDetails

type HostedResult() =
    member val Completed = false with get, set

// The host constructs this service before FcqrsHostedService.StartAsync runs.
// Every injected handle must resolve here, then work once this service starts.
type InjectedWorker(
    handler: Handler<int, int>,
    refs: AggregateRefs<int, int>,
    subscription: FCQRS.Query.ISubscribe,
    genericSubscription: FCQRS.Query.ISubscribe<IMessageWithCID>,
    serviceProvider: IServiceProvider,
    result: HostedResult) =

    let keyedRefs = serviceProvider.GetRequiredKeyedService<AggregateRefs<int, int>>(typeof<HostedAggregate>)
    let keyedHandler = serviceProvider.GetRequiredKeyedService<Handler<int, int>>(typeof<HostedAggregate>)

    do
        Expect.isTrue (obj.ReferenceEquals(refs, keyedRefs)) "keyed and unkeyed refs share one handle"
        Expect.isTrue (obj.ReferenceEquals(handler, keyedHandler)) "keyed and unkeyed handlers share one delegate"
        Expect.isTrue (obj.ReferenceEquals(handler, refs.Handler)) "refs expose the same handler"
        Expect.isTrue (obj.ReferenceEquals(subscription, genericSubscription)) "subscription interfaces share one handle"

    interface IHostedService with
        member _.StartAsync(cancellationToken) =
            task {
                let id = Values.CreateAggregateId "host-worker"
                let cid = Values.NewCID()
                let timeout = TimeSpan.FromSeconds 15.0
                // Exercise the deferred factory too, before using the handler.
                keyedRefs.Factory.Invoke(id.ToString()) |> ignore
                let allSeen = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
                use allEvents = subscription.Subscribe((fun _ -> allSeen.TrySetResult() |> ignore), cancellationToken)
                use byCid = subscription.Subscribe(cid, 1, cancellationToken = cancellationToken)
                use byFilter = genericSubscription.Subscribe((fun event -> event.CID = cid), 1, cancellationToken = cancellationToken)
                use byBoth = subscription.Subscribe(cid, (fun _ -> true), 1, cancellationToken = cancellationToken)

                let! first = handler.Invoke(Func<int, bool>(fun _ -> true), cid, id, 1)
                Expect.equal first.EventDetails 1 "constructor-injected handler dispatches after startup"
                Expect.equal (Values.VersionValue first.Version) 1L "C# reads the reply's persisted version"
                do! Task.WhenAll(byCid.Task, byFilter.Task, byBoth.Task, allSeen.Task).WaitAsync(timeout, cancellationToken)
                let! second = keyedHandler.Invoke(Func<int, bool>(fun _ -> true), Values.NewCID(), id, 2)
                Expect.equal second.EventDetails 3 "keyed handler reaches the same aggregate"
                Expect.equal (Values.VersionValue second.Version) 2L "the second event has version 2"
                Expect.equal (Values.VersionValue(Values.CreateVersion 7L)) 7L "VersionValue reverses CreateVersion"
                result.Completed <- true
            } :> Task

        member _.StopAsync(_) = Task.CompletedTask

let private hostedConstructorInjection =
    testCase "hosting: worker constructors can inject handlers, refs and subscriptions"
    <| fun _ ->
        let database = Path.Combine(Path.GetTempPath(), $"fcqrs-host-injection-{Guid.NewGuid():N}.db")
        let result = HostedResult()
        let builder = HostBuilder()
        builder.ConfigureServices(Action<HostBuilderContext, IServiceCollection>(fun _ services ->
            services
                .AddFcqrs($"Data Source={database};", "HostedInjectionRegression")
                .AddAggregate<HostedAggregate, int, int, int>()
                .AddProjection(Action<obj>(fun _ -> ()))
            |> ignore
            services.AddSingleton<HostedResult>(result) |> ignore
            services.AddHostedService<InjectedWorker>() |> ignore))
        |> ignore

        use host = builder.Build()
        try
            host.StartAsync().WaitAsync(TimeSpan.FromSeconds 30.0).GetAwaiter().GetResult()
            Expect.isTrue result.Completed "the worker started and observed its projected write"
        finally
            host.StopAsync().WaitAsync(TimeSpan.FromSeconds 30.0).GetAwaiter().GetResult()
            for path in [ database; database + "-wal"; database + "-shm" ] do
                if File.Exists path then File.Delete path

type RenewalAggregate() =
    inherit Aggregate<int, int, int>()
    override _.InitialState = 0
    override _.EntityName = "HostedRenewal"
    override _.HandleCommand(command, state) = PersistEvent(state + command.CommandDetails)
    override _.ApplyEvent(event, _) = event.EventDetails

type RenewalSaga(originator: AggregateFactory, ruleChecks: int ref) =
    inherit Saga<int, string, int>()
    override _.InitialData = 0
    override _.SagaName = "HostedRenewalSaga"
    override _.Originator = originator
    override _.StartsOn _ =
        Interlocked.Increment(&ruleChecks.contents) |> ignore
        true
    override _.Start(event, _) =
        match event with
        | :? Event<int> -> Saga<int, string, int>.StateChanged "renewed"
        | _ -> Saga<int, string, int>.Unhandled()
    override _.HandleEvent(_, _) = Saga<int, string, int>.Unhandled()
    override _.ApplySideEffects(_, _) =
        SagaSideEffectResult<string>(Transition = Saga<int, string, int>.StopSaga())

let private repeatedStart =
    testCase "hosting: each start of a shared builder uses only its own saga rules"
    <| fun _ ->
        let database = Path.Combine(Path.GetTempPath(), $"fcqrs-repeated-start-{Guid.NewGuid():N}.db")
        let ruleChecks = ref 0
        let services = ServiceCollection()
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            VerifySerialization.configuration().Build())
        |> ignore
        services.AddLogging() |> ignore
        services
            .AddFcqrs($"Data Source={database};", "RepeatedStart")
            .AddAggregate<RenewalAggregate, int, int, int>()
            .AddSaga(fun sp -> RenewalSaga(sp.AggregateFactory<RenewalAggregate>(), ruleChecks) :> Saga<_, _, _>)
        |> ignore

        // Two providers built from one service collection share the FcqrsBuilder.
        let run () =
            use provider = services.BuildServiceProvider()
            let hosted = provider.GetServices<IHostedService>() |> Seq.toList
            for service in hosted do
                service.StartAsync(CancellationToken.None).GetAwaiter().GetResult()
            try
                ruleChecks.Value <- 0
                let handler = provider.GetRequiredService<Handler<int, int>>()
                let reply =
                    handler
                        .Invoke(Func<int, bool>(fun _ -> true), Values.NewCID(), Values.CreateAggregateId "renewal", 1)
                        .WaitAsync(TimeSpan.FromSeconds 20.0)
                        .GetAwaiter()
                        .GetResult()
                Expect.isGreaterThan reply.EventDetails 0 "the renewal was journaled"
                ruleChecks.Value
            finally
                for service in hosted do
                    service.StopAsync(CancellationToken.None).GetAwaiter().GetResult()

        try
            Expect.equal (run ()) 1 "the first start checks the saga rule once per event"
            Expect.equal (run ()) 1 "a second start does not keep the stopped system's saga rule"
        finally
            for path in [ database; database + "-wal"; database + "-shm" ] do
                if File.Exists path then File.Delete path

type LedgerCommand = Record
type LedgerEvent = Recorded

// FCQRS.FSharp's Aggregate record would shadow the C# Aggregate class used above.
module private LongConnection =
    open FCQRS.FSharp

    let test =
        testCase "hosting: a connection string longer than 255 characters starts the actor system"
        <| fun _ ->
            let database = Path.Combine(Path.GetTempPath(), $"fcqrs-long-connection-{Guid.NewGuid():N}.db")
            // Provider settings such as TLS, pooling and timeouts make production strings this long.
            let connectionString = $"Data Source={database};" + String.replicate 15 "Default Timeout=30;"
            Expect.isGreaterThan connectionString.Length 255 "the connection string exceeds a ShortString"
            let configuration = VerifySerialization.configuration().Build()
            let loggers = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance
            let api =
                Fcqrs.actor configuration loggers
                    (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite connectionString)) "LongConnection"
            try
                let ledger =
                    Fcqrs.aggregate api
                        { Name = "Ledger"
                          Initial = 0
                          Decide = fun (_: Command<LedgerCommand>) _ -> PersistEvent Recorded
                          Fold = fun (_: Event<LedgerEvent>) state -> state + 1
                          Snapshots = NoSnapshots
                          Passivation = PassivationPolicy.Default }
                Fcqrs.wireSagaStarters api []
                let reply =
                    Async.RunSynchronously(
                        ledger.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "ledger") Record (fun _ -> true), 20000)
                Expect.equal reply.EventDetails Recorded "the journal accepts writes over the long connection string"
                let csharpApi = ActorApi.Create(configuration, loggers, connectionString, "LongConnectionCSharp")
                csharpApi.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore
            finally
                api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore
                for path in [ database; database + "-wal"; database + "-shm" ] do
                    if File.Exists path then File.Delete path

// Akka rebuilds an actor system's settings without a lock whenever an extension adds its
// configuration. Two threads doing so at once can leave one reading settings without its own
// configuration; the Persistence extension then fails, and stays failed for every persistent
// actor. Seen as "Couldn't find serializer id for PersistenceMessageSerializer" when a projection
// was created while a shard coordinator started its store. An extension that adds nothing new
// leaves the settings object as it is, so the check is that none of them replaces it after start.
module private ExtensionsAtStart =
    open FCQRS.FSharp
    open FCQRS.Workflows

    let test =
        testCase "hosting: every Akka extension FCQRS uses has added its configuration before the actor system is returned"
        <| fun _ ->
            let database = Path.Combine(Path.GetTempPath(), $"fcqrs-extensions-{Guid.NewGuid():N}.db")
            let configuration = VerifySerialization.configuration().Build()
            let loggers = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance
            let api =
                Fcqrs.actor configuration loggers (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={database};")) "ExtensionsAtStart"
            try
                let system = api.System
                let started = system.Settings.Config
                let unchanged what =
                    Expect.isTrue (obj.ReferenceEquals(started, system.Settings.Config)) $"{what} did not rebuild the settings"
                Akka.Persistence.Persistence.Instance.Apply(system) |> ignore
                unchanged "the Persistence extension"
                Akka.Persistence.Sql.SqlPersistence.Get(system) |> ignore
                unchanged "the SQL persistence extension"
                Akka.Cluster.Sharding.ClusterSharding.Get(system) |> ignore
                unchanged "cluster sharding"
                Akka.Cluster.Tools.Singleton.ClusterSingletonManagerSettings.Create(system) |> ignore
                unchanged "a cluster singleton"
                Akka.Cluster.Tools.PublishSubscribe.DistributedPubSub.Get(system) |> ignore
                unchanged "distributed pub-sub"
                Akka.Streams.ActorMaterializerSettings.Create(system) |> ignore
                unchanged "a stream materializer"
                // A remembered region starts its coordinator's journal store on another thread once
                // the cluster is up, and a projection reads the SQL journal.
                let greet =
                    Workflows.register api (Workflow.define "extensions-greet" (fun (name: string) -> workflow { return $"hello {name}" }))
                Fcqrs.wireSagaStarters api []
                let projection = Fcqrs.projection api (Projection.multi FromStart (fun _ -> []))
                Async.RunSynchronously(greet.Start "e1" "Ada", 30000) |> ignore
                let outcome = Async.RunSynchronously(greet.Result "e1" (TimeSpan.FromSeconds 20.0), 30000)
                Expect.equal outcome (WorkflowOutcome.Finished "hello Ada") "the run finished"
                projection.CatchUpAsync().Wait(TimeSpan.FromSeconds 30.0) |> ignore
                unchanged "a remembered region, a projection and a run"
            finally
                api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
                for path in [ database; database + "-wal"; database + "-shm" ] do
                    if File.Exists path then File.Delete path

[<AbstractClass>]
type UnconfiguredEvent() = class end
type UnconfiguredCase() = inherit UnconfiguredEvent()

[<AbstractClass; System.Text.Json.Serialization.JsonDerivedType(typeof<ConfiguredCase>, "case")>]
type ConfiguredEvent() = class end
and ConfiguredCase() = inherit ConfiguredEvent()

module private StorableEvents =
    open FCQRS.FSharp

    let test =
        testCase "registration: an abstract event type without polymorphism is rejected"
        <| fun _ ->
            let database = Path.Combine(Path.GetTempPath(), $"fcqrs-storable-events-{Guid.NewGuid():N}.db")
            let configuration = VerifySerialization.configuration().Build()
            let loggers = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance
            let api =
                Fcqrs.actor configuration loggers
                    (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={database};")) "StorableEvents"
            try
                // System.Text.Json would store each event as {}, losing its fields without an error.
                let rejected =
                    try
                        Fcqrs.aggregate api
                            { Name = "Unconfigured"
                              Initial = 0
                              Decide = fun (_: Command<int>) _ -> PersistEvent(UnconfiguredCase() :> UnconfiguredEvent)
                              Fold = fun (_: Event<UnconfiguredEvent>) state -> state
                              Snapshots = NoSnapshots
                              Passivation = PassivationPolicy.Default }
                        |> ignore
                        None
                    with :? InvalidOperationException as error -> Some error.Message
                Expect.isSome rejected "registration rejects an event type that would be stored as {}"
                Expect.stringContains rejected.Value "stored as {}" "the error names the data loss"
                // [JsonDerivedType] configures polymorphism, so the same shape registers.
                Fcqrs.aggregate api
                    { Name = "Configured"
                      Initial = 0
                      Decide = fun (_: Command<int>) _ -> PersistEvent(ConfiguredCase() :> ConfiguredEvent)
                      Fold = fun (_: Event<ConfiguredEvent>) state -> state
                      Snapshots = NoSnapshots
                      Passivation = PassivationPolicy.Default }
                |> ignore
            finally
                api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore
                for path in [ database; database + "-wal"; database + "-shm" ] do
                    if File.Exists path then File.Delete path

type RegistryLeft = { Left: int }
type RegistryRight = { Right: string }
type RegistryRejected = { Rejected: bool }

let private concurrentRegistry =
    testCase "journal registry: conflicting concurrent registrations have one winner"
    <| fun _ ->
        let name = $"registry-race-{Guid.NewGuid():N}"
        // Many disjoint aliases keep both callers checking the same unclaimed
        // primary name concurrently when conflict checks are not serialized.
        let aliases side = Array.init 32768 (fun index -> $"{name}.{side}.{index}")
        let leftAliases = aliases "left"
        let rightAliases = aliases "right"
        use gate = new Barrier(2)
        let register payloadType names =
            Task.Factory.StartNew((fun () ->
                gate.SignalAndWait() |> ignore
                try
                    JournalTypes.Map(payloadType, name, names)
                    true
                with :? InvalidOperationException -> false), TaskCreationOptions.LongRunning)

        let left = register typeof<RegistryLeft> leftAliases
        let right = register typeof<RegistryRight> rightAliases
        Task.WaitAll [| left :> Task; right :> Task |]
        let successfulRegistrations = [ left.Result; right.Result ] |> List.filter id |> List.length
        Expect.equal successfulRegistrations 1 "only one conflicting registration succeeds"

        use system = Akka.Actor.ActorSystem.Create("registry-race-test")
        let serializer = FCQRS.ActorSerialization.STJSerializer(system :?> Akka.Actor.ExtendedActorSystem)
        let winner, loser, winningAliases, losingAliases =
            if left.Result then
                (box { Left = 7 } |> nonNull), (box { Right = "seven" } |> nonNull), leftAliases, rightAliases
            else
                (box { Right = "seven" } |> nonNull), (box { Left = 7 } |> nonNull), rightAliases, leftAliases

        let manifest = serializer.Manifest winner
        Expect.equal manifest ("fcqrs:" + name) "the winner writes the registered manifest"
        let bytes = serializer.ToBinary winner
        let recovered = serializer.FromBinary(bytes, manifest)
        Expect.equal (recovered.GetType()) (winner.GetType()) "the same manifest reads its writer's type"
        Expect.equal (serializer.FromBinary(bytes, "fcqrs:" + winningAliases[0])) winner "winning aliases recover the payload"
        // Unmapped types fall back to their CLR name without an assembly version.
        let loserType = loser.GetType()
        Expect.equal (serializer.Manifest loser) $"{loserType.FullName}, {loserType.Assembly.GetName().Name}" "the losing type acquired no writer mapping"

        // A conflict at the end of a later registration must not install its
        // earlier primary name or alias, including when another registration lost.
        let rejectedName = name + ".rejected"
        let cleanAlias = name + ".unused"
        Expect.throwsT<InvalidOperationException>
            (fun () -> JournalTypes.Map(typeof<RegistryRejected>, rejectedName, [| cleanAlias; name |]))
            "an alias conflict rejects the complete registration"
        JournalTypes.Map(typeof<RegistryRejected>, cleanAlias, Array.append losingAliases [| rejectedName |])
        let accepted = box { Rejected = false }
        Expect.equal (serializer.Manifest accepted) ("fcqrs:" + cleanAlias) "rejected registrations reserved no primary names or aliases"

let tests =
    testSequenced (
        testList
            "hosting and registry"
            [ hostedConstructorInjection; repeatedStart; LongConnection.test; ExtensionsAtStart.test; StorableEvents.test; concurrentRegistry ]
    )
