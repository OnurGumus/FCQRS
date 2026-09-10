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
        run a b id silentReceived
    finally
        api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let tests =
    testList "command subscriptions" [
        testCase "a shared CID and event type cannot accept another aggregate type's reply" <| fun _ ->
            withAggregates 5 <| fun a b id silentReceived ->
                let cid = Fcqrs.newCid ()
                let isIncrement = function Incremented _ -> true | _ -> false
                let waiting = a.Send cid id Silent isIncrement |> Async.StartAsTask
                Expect.isTrue (silentReceived.Wait(TimeSpan.FromSeconds 5.0)) "the first request has been dispatched"

                b.Send cid id (Increment 7) isIncrement |> Async.RunSynchronously |> ignore
                a.Send cid id (Increment 9) isIncrement |> Async.RunSynchronously |> ignore

                let reply = waiting.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
                Expect.equal reply.EventDetails (Incremented 9) "TypeA must receive TypeA's event, not TypeB's earlier event"

        testCase "nonmatching events do not postpone the command deadline" <| fun _ ->
            withAggregates 1 <| fun a _ id silentReceived ->
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
