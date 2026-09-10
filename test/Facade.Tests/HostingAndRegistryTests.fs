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
                do! Task.WhenAll(byCid.Task, byFilter.Task, byBoth.Task, allSeen.Task).WaitAsync(timeout, cancellationToken)
                let! second = keyedHandler.Invoke(Func<int, bool>(fun _ -> true), Values.NewCID(), id, 2)
                Expect.equal second.EventDetails 3 "keyed handler reaches the same aggregate"
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
                .AddProjection(Action<int64, obj>(fun _ _ -> ()))
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
        Expect.equal (serializer.Manifest loser) (loser.GetType().AssemblyQualifiedName |> string) "the losing type acquired no writer mapping"

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

let tests = testSequenced (testList "hosting and registry" [ hostedConstructorInjection; concurrentRegistry ])
