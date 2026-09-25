module BankInvariantTests

// The tutorial's bank from samples/accounts/5-transfer-money, killed at random points. A transfer
// moves money between two accounts through a saga, so the total across the accounts must stay the
// same however often the process dies. It runs on SQLite, and on PostgreSQL when
// FCQRS_TEST_POSTGRES names a server: PostgreSQL runs writes concurrently, so a write can commit
// after later ones, which SQLite's single writer never does.

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Threading
open Akka.Persistence.Query
open Akka.Persistence.Sql.Query
open Akka.Streams.Dsl
open Expecto
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging.Abstractions
open Npgsql
open FCQRS.Common
open FCQRS.FSharp
open Account

[<Literal>]
let ChildFlag = "--bank-invariant-child"

let private accountNames = [| for i in 0..5 -> $"a{i}" |]
// Never opened: a transfer to one of them is turned down and refunded.
let private closedNames = [| "closed0"; "closed1" |]
let private opening = 1000m
let private total = opening * decimal accountNames.Length

/// The journal database, written as "sqlite:<path>" or "postgres:<connection string>" for the child.
type private Journal =
    | Sqlite of path: string
    | Postgres of connectionString: string

    member journal.Argument =
        match journal with
        | Sqlite path -> "sqlite:" + path
        | Postgres connectionString -> "postgres:" + connectionString

    static member Parse(argument: string) =
        if argument.StartsWith "postgres:" then Postgres(argument.Substring "postgres:".Length)
        else Sqlite(argument.Substring "sqlite:".Length)

/// An actor system on the journal, with no aggregates or sagas yet.
let private actorOn (journal: Journal) =
    let configuration =
        VerifySerialization.configuration()
            .AddInMemoryCollection([ KeyValuePair<string, string | null>("config:akka:fcqrs:command-timeout", "10") ])
            .Build()
    let connection =
        match journal with
        | Sqlite path -> Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={path};"
        | Postgres connectionString -> Fcqrs.connect FCQRS.Actor.DBType.PostgreSQL15 connectionString
    Fcqrs.actor configuration NullLoggerFactory.Instance (Some connection) "BankInvariant"

let private start (journal: Journal) =
    let api = actorOn journal
    // Short snapshot intervals, so recovery also starts from snapshots.
    let accounts =
        Fcqrs.aggregate api
            { Name = "Account"
              Initial = initial
              Decide = decide
              Fold = fold
              Snapshots = Every 3
              Passivation = PassivationPolicy.Default }
    let transfers = Fcqrs.saga api { Transfer.definition accounts.Factory with Snapshots = Every 2 }
    Fcqrs.wireSagaStarters api [ transfers ]
    api, accounts

let private send (accounts: AggregateHandle<AccountCommand, AccountEvent>) (account: string) command =
    Async.RunSynchronously(accounts.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId account) command (fun _ -> true), 30000)

/// Sends random transfers from concurrent callers until the parent kills the process, and reports
/// each transfer the bank confirmed as stored.
let runChild (seed: string) (journal: string) : int =
    let _, accounts = start (Journal.Parse journal)
    // Report ready once the bank answers, so the kill lands while transfers are running.
    send accounts accountNames[0] (Withdraw(total + 1m)) |> ignore
    Console.WriteLine "bank-invariant-child: ready"
    let caller (random: Random) =
        async {
            while true do
                let source = accountNames[random.Next accountNames.Length]
                let target =
                    if random.Next 6 = 0 then
                        closedNames[random.Next closedNames.Length]
                    else
                        let others = accountNames |> Array.filter ((<>) source)
                        others[random.Next others.Length]
                let id = Guid.NewGuid().ToString "N"
                let transfer = SendTransfer(id, target, decimal (random.Next(1, 300)))
                try
                    let! reply = accounts.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId source) transfer (fun _ -> true)
                    if reply.Journaled = Some true then
                        Console.WriteLine $"bank-invariant-child: stored {id}"
                with _ ->
                    ()
        }
    // Rounds differ in load. With many callers, several transfers wait on each account when the kill
    // lands; with few, accounts sit idle at a version, which recovery has to handle too.
    let callers = 1 + int seed % 16
    [ for i in 1..callers -> caller (Random(int seed + i)) ]
    |> Async.Parallel
    |> Async.Ignore
    |> Async.RunSynchronously
    0

/// Starts a child that sends transfers, kills it `delay` after it is ready, and returns the
/// transfers it confirmed as stored.
let private killDuringTransfers (journal: Journal) (seed: int) (delay: TimeSpan) =
    let host = Environment.ProcessPath |> Unchecked.nonNull
    let start = ProcessStartInfo(host)
    // Under `dotnet Facade.Tests.dll` the host is the muxer, which needs the assembly path.
    if String.Equals(Path.GetFileNameWithoutExtension host, "dotnet", StringComparison.OrdinalIgnoreCase) then
        start.ArgumentList.Add typeof<AccountState>.Assembly.Location
    for argument in [ ChildFlag; string seed; journal.Argument ] do
        start.ArgumentList.Add argument
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.UseShellExecute <- false
    use ready = new ManualResetEventSlim()
    let output = ConcurrentQueue<string>()
    use child = new Process(StartInfo = start)
    child.OutputDataReceived.Add(fun line ->
        match line.Data with
        | null -> ()
        | text ->
            output.Enqueue text
            if text = "bank-invariant-child: ready" then ready.Set())
    child.ErrorDataReceived.Add(fun line -> match line.Data with null -> () | text -> output.Enqueue text)
    child.Start() |> ignore
    child.BeginOutputReadLine()
    child.BeginErrorReadLine()
    let log () = String.Join("\n", output)
    try
        Expect.isTrue (ready.Wait(TimeSpan.FromSeconds 60.0)) $"the child started sending transfers:\n{log ()}"
        Thread.Sleep delay
        // A child that exited on its own hit a fail-fast termination.
        if child.HasExited then
            failtest $"the child exited with code {child.ExitCode} before it was killed:\n{log ()}"
    finally
        if not child.HasExited then child.Kill true
        child.WaitForExit()
    let unserializable = output |> Seq.filter _.StartsWith(VerifySerialization.FailurePrefix) |> List.ofSeq
    Expect.isEmpty unserializable "every message the child sent survives the trip to another node"
    [ for line in output do
        if line.StartsWith "bank-invariant-child: stored " then
            yield line.Substring "bank-invariant-child: stored ".Length ]

/// Every stored account event, grouped by the account that stored it.
let private history (api: IActor) =
    let journal = PersistenceQuery.Get(api.System).ReadJournalFor<SqlReadJournal>(SqlReadJournal.Identifier)
    let ids = journal.CurrentPersistenceIds().RunWith(Sink.Seq<string>(), api.Materializer).Result
    dict [
        for id in ids do
            if id.StartsWith "Account/" then
                let events =
                    journal.CurrentEventsByPersistenceId(id, 0L, Int64.MaxValue).RunWith(Sink.Seq<EventEnvelope>(), api.Materializer).Result
                    |> Seq.map (fun envelope -> envelope.Event :?> Event<AccountEvent>)
                    |> List.ofSeq
                yield (string events.Head.Sender.Value), events ]

let private transfersOf (events: Event<AccountEvent> list) =
    let pick choose = events |> List.choose (fun event -> choose event.EventDetails)
    {| Sent = pick (function TransferSent(id, _, _) -> Some id | _ -> None)
       Received = pick (function TransferReceived(id, _, _) -> Some id | _ -> None)
       Refunded = pick (function TransferRefunded(id, _, _) -> Some id | _ -> None) |}

/// Sent transfers that neither reached their target nor came back to their source.
let private unsettled (history: IDictionary<string, Event<AccountEvent> list>) =
    let all = history.Values |> Seq.map transfersOf |> List.ofSeq
    let finished = set [ for t in all do yield! t.Received; yield! t.Refunded ]
    [ for t in all do yield! t.Sent ] |> List.filter (finished.Contains >> not)

/// Creates a fresh journal database, runs `test` on it, and removes it.
let private withJournal (postgres: string option) (test: Journal -> unit) =
    match postgres with
    | None ->
        let path = Path.Combine(Path.GetTempPath(), $"fcqrs_bank_invariant_{Guid.NewGuid():N}.db")
        try
            test (Sqlite path)
        finally
            SqliteConnection.ClearAllPools()
            for file in [ path; path + "-wal"; path + "-shm" ] do
                if File.Exists file then File.Delete file
    | Some root ->
        let database = $"fcqrs_bank_{Guid.NewGuid():N}"
        let admin (sql: string) =
            use connection = new NpgsqlConnection(root)
            connection.Open()
            use command = connection.CreateCommand()
            command.CommandText <- sql
            command.ExecuteNonQuery() |> ignore
        admin $"CREATE DATABASE {database}"
        try
            let builder = NpgsqlConnectionStringBuilder(root)
            builder.Database <- database
            test (Postgres builder.ConnectionString)
        finally
            NpgsqlConnection.ClearAllPools()
            admin $"DROP DATABASE IF EXISTS {database} WITH (FORCE)"

let private killAndCheck (database: string) (rounds: int) (journal: Journal) =
    let seed = Random.Shared.Next 1_000_000
    let random = Random seed
    let context = $"{database}, seed {seed}"
    do
        let api, accounts = start journal
        try
            for name in accountNames do
                send accounts name (Open name) |> ignore
                send accounts name (Deposit opening) |> ignore
        finally
            api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

    let confirmed =
        [ for round in 1..rounds do
            yield! killDuringTransfers journal (seed + 100 * round) (TimeSpan.FromMilliseconds(float (random.Next(300, 2000)))) ]

    // The transfers the kills left unfinished, read before any saga recovers.
    let unfinished =
        let reader = actorOn journal
        try
            (unsettled (history reader)).Length
        finally
            reader.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

    // A new process recovers the sagas the kills interrupted; they finish their transfers.
    do
        let api, accounts = start journal
        try
            let deadline = DateTime.UtcNow.AddSeconds 120.0
            let mutable stored = history api
            while not (List.isEmpty (unsettled stored)) && DateTime.UtcNow < deadline do
                Thread.Sleep 1000
                stored <- history api

            let transfers = stored.Values |> Seq.map transfersOf |> List.ofSeq
            let sent = transfers |> List.collect _.Sent
            printfn "bank invariant (%s): %d transfers sent, %d received, %d refunded; %d were unfinished after the last kill"
                context sent.Length (transfers |> List.sumBy _.Received.Length) (transfers |> List.sumBy _.Refunded.Length) unfinished
            Expect.isNonEmpty confirmed $"the killed processes stored transfers ({context})"
            let journaled = set sent
            Expect.isEmpty (confirmed |> List.filter (journaled.Contains >> not))
                $"every transfer a killed process confirmed as stored is in the journal ({context})"
            Expect.isEmpty (unsettled stored) $"every stored transfer reached its target or came back ({context})"

            let once (ids: string list) what =
                let repeated = ids |> List.countBy id |> List.filter (fun (_, n) -> n > 1)
                Expect.isEmpty repeated $"each transfer {what} once ({context})"
            once sent "was sent"
            once (transfers |> List.collect _.Received) "was received"
            once (transfers |> List.collect _.Refunded) "was refunded"
            let received = set (transfers |> List.collect _.Received)
            let refunded = transfers |> List.collect _.Refunded
            Expect.isEmpty (refunded |> List.filter received.Contains) $"no transfer was both received and refunded ({context})"

            for name in closedNames do
                Expect.isFalse (stored.ContainsKey name) $"{name} stored nothing ({context})"

            let balances =
                dict [ for KeyValue(name, events) in stored -> name, (events |> List.fold (fun state event -> fold event state) initial).Balance ]
            for KeyValue(name, balance) in balances do
                Expect.isGreaterThanOrEqual balance 0m $"{name} ends with no debt ({context})"
            Expect.equal (Seq.sum balances.Values) total $"the accounts together still hold {total} ({context})"

            // Each live account, recovered from its snapshot and later events, reports the same balance.
            for KeyValue(name, balance) in balances do
                let reply = send accounts name (Withdraw(total + 1m))
                Expect.equal reply.EventDetails (Rejected $"Insufficient funds: {balance} available")
                    $"{name} recovered the balance its journal holds ({context})"
        finally
            api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let private conserved (postgres: string option) (rounds: int) =
    let database = if postgres.IsSome then "PostgreSQL" else "SQLite"
    testCase $"bank invariant: transfers keep the total balance across process kills ({database})"
    <| fun _ -> withJournal postgres (killAndCheck database rounds)

let tests =
    let postgres =
        match Environment.GetEnvironmentVariable "FCQRS_TEST_POSTGRES" with
        | null | "" -> None
        | value -> Some value
    testSequenced (
        testList "bank invariant" [
            conserved None 8
            match postgres with
            // More kills: each one lands while more transfers run at once than on SQLite.
            | Some _ -> conserved postgres 15
            | None -> ptestCase "bank invariant on PostgreSQL: set FCQRS_TEST_POSTGRES to run" ignore ])
