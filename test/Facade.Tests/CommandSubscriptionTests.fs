module CommandSubscriptionTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging.Abstractions
open FCQRS.Common
open FCQRS.FSharp

type private TestCommand =
    | Increment of int
    | Poke
    | Silent

type private TestEvent =
    | Incremented of int
    | Poked

type private SlowEffect = Compute

type private SlowCommand =
    | Begin
    | Complete
    | Check

type private SlowEvent =
    | Completed
    | Checked

let private runnerOffTheAggregateThread =
    testCase "a runner's synchronous work does not block its aggregate" <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), $"fcqrs_runner_thread_{Guid.NewGuid():N}.db")
        let api =
            Fcqrs.actor (ConfigurationBuilder().Build()) NullLoggerFactory.Instance
                (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={db};")) "RunnerThread"
        try
            use runnerStarted = new ManualResetEventSlim(false)
            let runner (_: SlowEffect) : Async<SlowCommand> =
                // Synchronous work before the runner's first asynchronous step.
                runnerStarted.Set()
                Thread.Sleep 2000
                async { return Complete }
            let slow =
                Fcqrs.aggregateWithEffects api
                    { Name = "SlowRunner"
                      Initial = 0
                      Decide =
                        fun (command: Command<SlowCommand>) _ ->
                            match command.CommandDetails with
                            | Begin -> dispatch Compute
                            | Complete -> PersistEvent Completed
                            | Check -> DeferEvent Checked
                      Fold = fun (_: Event<SlowEvent>) state -> state
                      Snapshots = NoSnapshots
                      Passivation = PassivationPolicy.Default }
                    runner
            Fcqrs.wireSagaStarters api []
            let id = Fcqrs.aggregateId "slow"
            let completion =
                slow.Send (Fcqrs.newCid ()) id Begin (function Completed -> true | _ -> false) |> Async.StartAsTask
            Expect.isTrue (runnerStarted.Wait(TimeSpan.FromSeconds 5.0)) "the runner started"
            let timer = Diagnostics.Stopwatch.StartNew()
            slow.Send (Fcqrs.newCid ()) id Check (fun _ -> true) |> Async.RunSynchronously |> ignore
            Expect.isLessThan timer.ElapsedMilliseconds 1000L "the aggregate answered while its runner was busy"
            Expect.equal (completion.WaitAsync(TimeSpan.FromSeconds 10.0).GetAwaiter().GetResult().EventDetails) Completed
                "the runner's command still completes the first request"
        finally
            api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let private withAggregates timeoutSeconds run =
    let db = Path.Combine(Path.GetTempPath(), $"fcqrs_command_regression_{Guid.NewGuid():N}.db")
    let config =
        ConfigurationBuilder()
            .AddInMemoryCollection(
                [ Collections.Generic.KeyValuePair<string, string | null>(
                      "config:akka:fcqrs:command-timeout", string timeoutSeconds) ])
            .Build()
    let api =
        Fcqrs.actor config NullLoggerFactory.Instance
            (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={db};")) "CommandRegression"
    use silentReceived = new ManualResetEventSlim(false)
    let register name =
        Fcqrs.aggregate api
            { Name = name
              Initial = 0
              Decide = fun command _ ->
                  match command.CommandDetails with
                  | Increment n -> PersistEvent(Incremented n)
                  | Poke -> DeferEvent Poked
                  | Silent ->
                      silentReceived.Set()
                      IgnoreEvent
              Fold = fun event state ->
                  match event.EventDetails with
                  | Incremented n -> state + n
                  | Poked -> state
              Snapshots = NoSnapshots
              Passivation = PassivationPolicy.Default }
    try
        let a = register "TypeA"
        let b = register "TypeB"
        Fcqrs.wireSagaStarters api []
        let id = Fcqrs.aggregateId "shared entity"
        for aggregate in [ a; b ] do
            aggregate.Send (Fcqrs.newCid ()) id Poke (fun _ -> true)
            |> Async.RunSynchronously
            |> ignore
        run api a b id silentReceived
    finally
        api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let tests =
    testList "command subscriptions" [
        runnerOffTheAggregateThread

        testCase "application handler registers eagerly and preserves command delivery" <| fun _ ->
            withAggregates 2 <| fun api _ _ _ _ ->
                let seenCids = Collections.Concurrent.ConcurrentQueue<FCQRS.Model.Data.CID>()
                let handler: Handler<TestCommand, TestEvent> =
                    Fcqrs.handler api
                        { Name = "ApplicationHandler"
                          Initial = 0
                          Decide = fun command state ->
                              seenCids.Enqueue command.CorrelationId
                              match command.CommandDetails with
                              | Increment n -> PersistEvent(Incremented(state + n))
                              | Poke -> DeferEvent Poked
                              | Silent -> IgnoreEvent
                          Fold = fun event state ->
                              match event.EventDetails with
                              | Incremented total -> total
                              | Poked -> state
                          Snapshots = NoSnapshots
                          Passivation = PassivationPolicy.Default }

                // This lookup throws if registration was deferred until the first send.
                Akka.Cluster.Sharding.ClusterSharding.Get(api.System).ShardRegion("ApplicationHandler") |> ignore
                let cid = Fcqrs.newCid ()
                let first, second = Fcqrs.aggregateId "first", Fcqrs.aggregateId "second"
                let send id command = handler (fun _ -> true) cid id command |> Async.RunSynchronously
                let pending = handler (fun _ -> true) cid first (Increment 3)
                Expect.isTrue seenCids.IsEmpty "constructing an Async must not dispatch the command"
                Expect.equal (Async.RunSynchronously pending) (Incremented 3) "return the event payload"
                Expect.equal (send first (Increment 4)) (Incremented 7) "reuse the same entity state"
                Expect.equal (send second (Increment 2)) (Incremented 2) "route a different id to independent state"
                Expect.equal (send first Poke) Poked "return deferred replies without a projection"
                Expect.equal (seenCids.ToArray()) [| cid; cid; cid; cid |] "forward the caller's correlation id on every send"

                Expect.throwsT<TimeoutException>
                    (fun () ->
                        handler (function Incremented _ -> true | _ -> false) cid first Poke
                        |> Async.RunSynchronously |> ignore)
                    "respect the event filter and propagate the command timeout"

        testCase "a shared CID and event type cannot accept another aggregate type's reply" <| fun _ ->
            withAggregates 5 <| fun _ a b id silentReceived ->
                let cid = Fcqrs.newCid ()
                let isIncrement = function Incremented _ -> true | _ -> false
                let waiting = a.Send cid id Silent isIncrement |> Async.StartAsTask
                Expect.isTrue (silentReceived.Wait(TimeSpan.FromSeconds 5.0)) "the first request has been dispatched"

                b.Send cid id (Increment 7) isIncrement |> Async.RunSynchronously |> ignore
                a.Send cid id (Increment 9) isIncrement |> Async.RunSynchronously |> ignore

                let reply = waiting.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
                Expect.equal reply.EventDetails (Incremented 9) "TypeA must receive TypeA's event, not TypeB's earlier event"

        testCase "stopping the actor system releases a waiting send" <| fun _ ->
            withAggregates 120 <| fun api a _ id silentReceived ->
                let pending = a.Send (Fcqrs.newCid ()) id Silent (fun _ -> true) |> Async.StartAsTask
                Expect.isTrue (silentReceived.Wait(TimeSpan.FromSeconds 5.0)) "the command reached its aggregate"
                api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore
                let released = (pending :> Task).ContinueWith(fun (_: Task) -> ()).Wait(TimeSpan.FromSeconds 10.0)
                Expect.isTrue released "the caller is released well before its two-minute command timeout"
                Expect.notEqual pending.Status TaskStatus.RanToCompletion "no reply arrived"
                if pending.IsFaulted then
                    let error = pending.Exception.GetBaseException()
                    Expect.isTrue (error :? OperationCanceledException) $"shutdown is reported as cancellation, not {error.GetType().Name}"

        testCase "a command timeout beyond the ask timer's limit still delivers the reply" <| fun _ ->
            // Sixty days, beyond the ~49.7 days a cancellation timer accepts.
            withAggregates (60 * 24 * 3600) <| fun _ a _ id _ ->
                let reply = Async.RunSynchronously(a.Send (Fcqrs.newCid ()) id (Increment 1) (fun _ -> true), 20000)
                Expect.equal reply.EventDetails (Incremented 1) "the send completes"

        testCase "nonmatching events do not postpone the command deadline" <| fun _ ->
            withAggregates 1 <| fun _ a _ id silentReceived ->
                let cid = Fcqrs.newCid ()
                let waiting =
                    a.Send cid id Silent (function Incremented _ -> true | _ -> false)
                    |> Async.StartAsTask
                Expect.isTrue (silentReceived.Wait(TimeSpan.FromSeconds 5.0)) "the timed request has been dispatched"
                use stopTraffic = new CancellationTokenSource()
                let traffic =
                    async {
                        while not stopTraffic.IsCancellationRequested do
                            do! a.Send cid id Poke (fun _ -> true) |> Async.Ignore
                            do! Async.Sleep 50
                    }
                    |> Async.StartAsTask
                try
                    // Traffic continues throughout this window. ReceiveTimeout
                    // would never fire because every Poked event resets it.
                    let completed = Task.WhenAny(waiting :> Task, Task.Delay 3000).GetAwaiter().GetResult()
                    Expect.isTrue (obj.ReferenceEquals(completed, waiting)) "the one-second deadline fired while events were arriving"
                    Expect.throwsT<TimeoutException>
                        (fun () -> waiting.GetAwaiter().GetResult() |> ignore)
                        "the caller receives the configured command timeout"
                finally
                    stopTraffic.Cancel()
                    traffic.Wait(TimeSpan.FromSeconds 5.0) |> ignore
    ]
