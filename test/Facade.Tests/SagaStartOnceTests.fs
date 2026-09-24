module SagaStartOnceTests

// A saga starts once per correlation ID and aggregate. A command whose event would start it again,
// with the same correlation ID, is refused with SagaAlreadyStartedException, and the aggregate
// stores none of its events. Before the refusal, the second event was stored and ran no workflow.

open System
open System.IO
open System.Threading
open Expecto
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Logging.Abstractions
open FCQRS.Common
open FCQRS.FSharp
open FCQRS.Model.Data
open Account

let private withSystem name (run: IActor -> unit) =
    let db = Path.Combine(Path.GetTempPath(), $"fcqrs_start_once_{Guid.NewGuid():N}.db")
    let api =
        Fcqrs.actor (VerifySerialization.configuration().Build()) NullLoggerFactory.Instance
            (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={db};")) name
    try
        run api
    finally
        api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore
        SqliteConnection.ClearAllPools()
        for path in [ db; db + "-wal"; db + "-shm" ] do
            if File.Exists path then File.Delete path

let private refusal (send: unit -> unit) =
    try
        send ()
        failtest "the command was refused"
    with :? SagaAlreadyStartedException as refused ->
        refused

let private until what (condition: unit -> bool) =
    let deadline = DateTime.UtcNow.AddSeconds 20.0
    while not (condition ()) && DateTime.UtcNow < deadline do
        Thread.Sleep 100
    Expect.isTrue (condition ()) what

let private reusedCorrelation =
    testCase "saga start once: a transfer that reuses a finished transfer's correlation ID is refused"
    <| fun _ ->
        withSystem "StartOnceReuse" <| fun api ->
            let accounts =
                Fcqrs.aggregate api
                    { Name = "Account"
                      Initial = initial
                      Decide = decide
                      Fold = fold
                      Snapshots = Default
                      Passivation = PassivationPolicy.Default }
            Fcqrs.wireSagaStarters api [ Fcqrs.saga api (Transfer.definition accounts.Factory) ]
            let send cid account command =
                Async.RunSynchronously(accounts.Send cid (Fcqrs.aggregateId account) command (fun _ -> true), 30000)
            // A withdrawal larger than any balance is rejected with the balance.
            let balance account =
                (send (Fcqrs.newCid ()) account (Withdraw 1_000_000m)).EventDetails
            let holds account (amount: decimal) = balance account = Rejected $"Insufficient funds: {amount} available"

            send (Fcqrs.newCid ()) "alice" (Open "Alice") |> ignore
            send (Fcqrs.newCid ()) "alice" (Deposit 100m) |> ignore
            send (Fcqrs.newCid ()) "bob" (Open "Bob") |> ignore

            let cid = Fcqrs.newCid ()
            send cid "alice" (SendTransfer("t1", "bob", 30m)) |> ignore
            until "t1 reached Bob" (fun () -> holds "bob" 30m)

            // A deposit sent while the second transfer waits for its saga is stashed, and runs
            // once the transfer is refused.
            let second =
                Async.StartAsTask(async { return refusal (fun () -> send cid "alice" (SendTransfer("t2", "bob", 20m)) |> ignore) })
            send (Fcqrs.newCid ()) "alice" (Deposit 5m) |> ignore
            let refused = second.Result
            Expect.equal refused.AggregateId "alice" "the refusal names the account"
            Expect.equal refused.Saga "Transfer" "the refusal names the saga"
            Expect.equal refused.CorrelationId (cid |> ValueLens.Value |> ValueLens.Value) "the refusal names the reused correlation ID"
            Expect.isTrue (holds "alice" 75m) "the refused transfer took no money, and the deposit was stored"

            // With its own correlation ID, the same transfer goes through.
            send (Fcqrs.newCid ()) "alice" (SendTransfer("t2", "bob", 20m)) |> ignore
            until "t2 reached Bob" (fun () -> holds "bob" 50m)
            Expect.isTrue (holds "alice" 55m) "Alice paid for t1 and t2 once each"

type BatchCommand =
    | StartTwo
    | StartOne

type BatchEvent = Began of string

let private sameSagaTwice =
    testCase "saga start once: a command whose two events would start the same saga is refused"
    <| fun _ ->
        withSystem "StartOnceBatch" <| fun api ->
            let batches =
                Fcqrs.aggregate api
                    { Name = "Batch"
                      Initial = 0
                      Decide =
                        fun (command: Command<BatchCommand>) _ ->
                            match command.CommandDetails with
                            | StartTwo -> PersistAllEvents [ Began "a"; Began "b" ]
                            | StartOne -> PersistEvent(Began "c")
                      Fold = fun _ count -> count + 1
                      Snapshots = NoSnapshots
                      Passivation = PassivationPolicy.Default }
            let saga =
                Fcqrs.saga api
                    { Name = "BatchSaga"
                      InitialData = ()
                      Originator = batches.Factory
                      HandleEvent =
                        fun message saga ->
                            match message, saga.State with
                            | :? Event<BatchEvent>, None -> StateChangedEvent "started"
                            | _ -> UnhandledEvent
                      ApplySideEffects = fun _ _ -> StopSaga, []
                      StartOn = fun (_: Event<BatchEvent>) -> true
                      Snapshots = NoSnapshots }
            Fcqrs.wireSagaStarters api [ saga ]
            let send command =
                Async.RunSynchronously(batches.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "x") command (fun _ -> true), 30000)

            let refused = refusal (fun () -> send StartTwo |> ignore)
            Expect.equal refused.Saga "BatchSaga" "the refusal names the saga"
            let next = send StartOne
            Expect.equal (ValueLens.Value next.Version) 1L "the refused command stored neither event"

let tests = testSequenced (testList "saga start once" [ reusedCorrelation; sameSagaTwice ])
