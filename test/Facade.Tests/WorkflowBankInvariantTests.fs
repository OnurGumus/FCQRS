module WorkflowBankInvariantTests

// The tutorial's bank from samples/accounts/5-transfer-money, whose transfer is a workflow.
// Processes are killed while transfers run, and a new process finishes them. The accounts together
// must hold what they held at the start: no kill may create money, lose it, or move a transfer twice.
//
// FCQRS_TEST_POSTGRES, FCQRS_TEST_SQLSERVER, or FCQRS_TEST_ORACLE names a server, as for the saga version of this test.

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
open FCQRS.Common
open FCQRS.FSharp
open FCQRS.Model.Data
open FCQRS.Workflows
open Account
open Transfer

/// The argument that makes this assembly run as the child that starts transfers.
[<Literal>]
let ChildFlag = "--workflow-bank-child"

[<Literal>]
let private Prefix = "workflow-bank-child: "

let private accountNames = [| for i in 0..5 -> $"a{i}" |]
// Never opened: delivery to one of them is refused, and the money goes back.
let private closedNames = [| "closed0"; "closed1" |]
let private opening = 1000m
let private total = opening * decimal accountNames.Length

type private Journal =
    | Sqlite of path: string
    | OnServer of kind: TestDatabases.Kind * connectionString: string

    member journal.Argument =
        match journal with
        | Sqlite path -> "sqlite:" + path
        | OnServer(kind, connectionString) -> kind.Prefix + connectionString

    static member Parse(argument: string) =
        match TestDatabases.Kind.Parse argument with
        | Some(kind, connectionString) -> OnServer(kind, connectionString)
        | None -> Sqlite(argument.Substring "sqlite:".Length)

/// An actor system on the journal, with no aggregate or workflow registered, so nothing recovers.
let private actorOn (journal: Journal) =
    let configuration =
        VerifySerialization.configuration()
            .AddInMemoryCollection([ KeyValuePair<string, string | null>("config:akka:fcqrs:command-timeout", "10") ])
            .Build()
    let connection =
        match journal with
        | Sqlite path -> Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={path};"
        | OnServer(kind, connectionString) -> Fcqrs.connect kind.DBType connectionString
    Fcqrs.actor configuration NullLoggerFactory.Instance (Some connection) "WorkflowBank"

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
    Fcqrs.wireSagaStarters api []
    // Registering the workflow resumes the transfers an earlier process left running.
    let transfers = Workflows.register api (Transfer.definition accounts)
    api, accounts, transfers

let private ask (accounts: AggregateHandle<AccountCommand, AccountEvent>) (account: string) command =
    Async.RunSynchronously(accounts.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId account) command (fun _ -> true), 30000)

/// Starts random transfers from concurrent callers until the parent kills the process, and
/// reports each transfer whose start was stored.
let runChild (seed: string) (journal: string) : int =
    let _, accounts, transfers = start (Journal.Parse journal)
    // Report ready once the bank and the transfers' shard region answer, so the kill lands while
    // transfers are running and not while the regions are still starting.
    ask accounts accountNames[0] (Withdraw(total + 1m)) |> ignore
    Async.RunSynchronously(transfers.Status "warm-up", 60000) |> ignore
    Console.WriteLine(Prefix + "ready")
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
                try
                    let request = { Id = id; Source = source; Target = target; Amount = decimal (random.Next(1, 300)) }
                    let! started = transfers.Start id request
                    if started = StartResult.Started then
                        Console.WriteLine(Prefix + "started " + id)
                    // Does not wait for the outcome, so many transfers are running when the kill lands.
                    ()
                with _ ->
                    ()
        }
    let callers = 1 + int seed % 16
    [ for i in 1..callers -> caller (Random(int seed + i)) ]
    |> Async.Parallel
    |> Async.Ignore
    |> Async.RunSynchronously
    0

/// Starts a child that runs transfers, kills it `delay` after it is ready, and returns the
/// transfers whose start it confirmed.
let private killDuringTransfers (journal: Journal) (seed: int) (delay: TimeSpan) =
    let host = Environment.ProcessPath |> Unchecked.nonNull
    let info = ProcessStartInfo(host)
    // Under `dotnet Facade.Tests.dll` the host is the muxer, which needs the assembly path.
    if String.Equals(Path.GetFileNameWithoutExtension host, "dotnet", StringComparison.OrdinalIgnoreCase) then
        info.ArgumentList.Add typeof<AccountState>.Assembly.Location
    for argument in [ ChildFlag; string seed; journal.Argument ] do
        info.ArgumentList.Add argument
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.UseShellExecute <- false
    use ready = new ManualResetEventSlim()
    let output = ConcurrentQueue<string>()
    use child = new Process(StartInfo = info)
    child.OutputDataReceived.Add(fun line ->
        match line.Data with
        | null -> ()
        | text ->
            output.Enqueue text
            if text = Prefix + "ready" then ready.Set())
    child.ErrorDataReceived.Add(fun line -> match line.Data with null -> () | text -> output.Enqueue text)
    child.Start() |> ignore
    child.BeginOutputReadLine()
    child.BeginErrorReadLine()
    let log () = String.Join("\n", output)
    try
        Expect.isTrue (ready.Wait(TimeSpan.FromSeconds 60.0)) $"the child started running transfers:\n{log ()}"
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
        if line.StartsWith(Prefix + "started ") then
            yield line.Substring((Prefix + "started ").Length) ]

/// The transfers' IDs, and every stored account event grouped by account.
let private stored (api: IActor) =
    let journal = PersistenceQuery.Get(api.System).ReadJournalFor<SqlReadJournal>(SqlReadJournal.Identifier)
    let ids = journal.CurrentPersistenceIds().RunWith(Sink.Seq<string>(), api.Materializer).Result |> List.ofSeq
    let accounts =
        dict [
            for id in ids do
                if id.StartsWith "Account/" then
                    let events =
                        journal.CurrentEventsByPersistenceId(id, 0L, Int64.MaxValue).RunWith(Sink.Seq<EventEnvelope>(), api.Materializer).Result
                        |> Seq.map (fun envelope -> envelope.Event :?> Event<AccountEvent>)
                        |> List.ofSeq
                    yield id.Substring(id.LastIndexOf '/' + 1), events ]
    let transfers = [ for id in ids do if id.StartsWith "Transfer/" then id.Substring(id.LastIndexOf '/' + 1) ]
    transfers, accounts

/// The transfers whose journal has no final record.
let private unfinishedIn (api: IActor) =
    let journal = PersistenceQuery.Get(api.System).ReadJournalFor<SqlReadJournal>(SqlReadJournal.Identifier)
    let ids = journal.CurrentPersistenceIds().RunWith(Sink.Seq<string>(), api.Materializer).Result
    [ for id in ids do
        if id.StartsWith "Transfer/" then
            let last =
                journal.CurrentEventsByPersistenceId(id, 0L, Int64.MaxValue).RunWith(Sink.Seq<EventEnvelope>(), api.Materializer).Result
                |> Seq.map (fun envelope -> envelope.Event :?> WorkflowRecord)
                |> Seq.last
            match last with
            | WorkflowRecord.Finished _ | WorkflowRecord.Faulted _ | WorkflowRecord.Cancelled _ -> ()
            | _ -> id ]

let private withJournal (server: TestDatabases.Server option) (test: Journal -> unit) =
    match server with
    | None ->
        let path = Path.Combine(Path.GetTempPath(), $"fcqrs_workflow_bank_{Guid.NewGuid():N}.db")
        try
            test (Sqlite path)
        finally
            SqliteConnection.ClearAllPools()
            for file in [ path; path + "-wal"; path + "-shm" ] do
                if File.Exists file then File.Delete file
    | Some server ->
        TestDatabases.withDatabase server "fcqrs_bank" (fun connectionString -> test (OnServer(server.Kind, connectionString)))

let private killAndCheck (database: string) (rounds: int) (journal: Journal) =
    let seed = Random.Shared.Next 1_000_000
    let random = Random seed
    let context = $"{database}, seed {seed}"
    do
        let api, accounts, _ = start journal
        try
            for name in accountNames do
                ask accounts name (Open name) |> ignore
                ask accounts name (Deposit opening) |> ignore
        finally
            api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

    // The transfers a kill left unfinished, read before any workflow recovers.
    let unfinishedNow () =
        let reader = actorOn journal
        try
            (unfinishedIn reader).Length
        finally
            reader.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

    let rounds =
        [ for round in 1..rounds do
            let started = killDuringTransfers journal (seed + 100 * round) (TimeSpan.FromMilliseconds(float (random.Next(300, 2000))))
            started, unfinishedNow () ]
    let confirmed = rounds |> List.collect fst
    let interrupted = rounds |> List.sumBy snd
    let unfinished = rounds |> List.last |> snd
    // Without this, the test would pass even if every transfer had finished before each kill.
    Expect.isGreaterThan interrupted 0 $"kills landed while transfers were running ({context})"

    // A new process recovers the transfers the kills interrupted. Nothing is sent to them.
    let recovery = Stopwatch.StartNew()
    let api, accounts, transfers = start journal
    try
        let ended id =
            match Async.RunSynchronously(transfers.Status id, 30000) with
            | WorkflowStatus.Completed -> true
            | WorkflowStatus.Running _ -> false
            | other -> failtest $"transfer {id} is {other} ({context})"
        let deadline = DateTime.UtcNow.AddSeconds 180.0
        let mutable ids, _ = stored api
        while not (List.forall ended ids) && DateTime.UtcNow < deadline do
            Thread.Sleep 200
            ids <- fst (stored api)
        let recovered = recovery.Elapsed
        Expect.isTrue (List.forall ended ids) $"every started transfer ended ({context})"

        let ids, events = stored api
        Expect.isNonEmpty confirmed $"the killed processes started transfers ({context})"
        Expect.isEmpty (confirmed |> List.filter (fun id -> not (List.contains id ids))) $"every confirmed start is in the journal ({context})"

        let pick choose = [ for vault in events.Values do yield! vault |> List.choose (fun event -> choose event.EventDetails) ]
        let taken = pick (function TransferSent(id, _, _) -> Some id | _ -> None)
        let given = pick (function TransferReceived(id, _, _) -> Some id | _ -> None)
        let back = pick (function TransferRefunded(id, _, _) -> Some id | _ -> None)
        printfn "workflow bank invariant (%s): %d transfers started, %d taken, %d given, %d given back; kills found %d transfers unfinished, %d after the last, which ended %.1f s after the new process started"
            context ids.Length taken.Length given.Length back.Length interrupted unfinished recovered.TotalSeconds
        let once (list: string list) what =
            Expect.isEmpty (list |> List.countBy id |> List.filter (fun (_, count) -> count > 1)) $"each transfer {what} once ({context})"
        once taken "was taken"
        once given "was given"
        once back "was given back"
        Expect.equal (set taken) (Set.union (set given) (set back)) $"every transfer taken was given or given back ({context})"
        Expect.isEmpty (Set.intersect (set given) (set back)) $"no transfer was both given and given back ({context})"

        let balances = dict [ for KeyValue(name, history) in events -> name, (history |> List.fold (fun state event -> fold event state) initial).Balance ]
        for KeyValue(name, balance) in balances do
            Expect.isGreaterThanOrEqual balance 0m $"{name} ends with no debt ({context})"
        Expect.equal (Seq.sum balances.Values) total $"the accounts together still hold {total} ({context})"
        for KeyValue(name, balance) in balances do
            let reply = ask accounts name (Withdraw(total + 1m))
            Expect.equal reply.EventDetails (Rejected $"Insufficient funds: {balance} available")
                $"{name} recovered the balance its journal holds ({context})"
    finally
        api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let private conserved (server: TestDatabases.Server option) (rounds: int) =
    let database = server |> Option.map _.Kind.Name |> Option.defaultValue "SQLite"
    testCase $"workflow bank invariant: transfers written as workflows keep the total across process kills ({database})"
    <| fun _ -> withJournal server (killAndCheck database rounds)

/// A repeated SendTransfer, as a workflow sends after a restart, gets the first verdict even when the
/// first send spent the balance: a refusal would make the workflow end the transfer as refused.
let private repeatedSend =
    testCase "workflow bank: a repeated SendTransfer answers TransferSent after the balance was spent" <| fun _ ->
        let command details : Command<AccountCommand> =
            { CommandDetails = details
              CreationDate = DateTime.UtcNow
              Id = Guid.CreateVersion7().ToString() |> ValueLens.CreateAsResult |> Result.value
              Sender = None
              CorrelationId = Fcqrs.newCid ()
              Metadata = Map.empty }
        let event details : Event<AccountEvent> =
            { EventDetails = details
              CreationDate = DateTime.UtcNow
              Id = Guid.CreateVersion7().ToString() |> ValueLens.CreateAsResult |> Result.value
              Sender = None
              CorrelationId = Fcqrs.newCid ()
              Version = Version.Zero
              Metadata = Map.empty }
        let state =
            [ Opened "Alice"; Deposited 50m; TransferSent("t1", "bob", 50m) ]
            |> List.fold (fun state details -> fold (event details) state) initial
        Expect.equal state.Balance 0m "the first send spent the balance"
        match decide (command (SendTransfer("t1", "bob", 50m))) state with
        | DeferEvent(TransferSent("t1", "bob", 50m)) -> ()
        | other -> failtest $"expected the first verdict, not stored; got %A{other}"
        Expect.equal (fold (event (TransferSent("t1", "bob", 50m))) state).Balance 0m "folding the repeated reply moves no money"

let tests =
    testSequenced (
        testList "workflow bank invariant" [
            repeatedSend
            conserved None 6
            for server, variable in [ TestDatabases.postgres, "FCQRS_TEST_POSTGRES"; TestDatabases.sqlServer, "FCQRS_TEST_SQLSERVER"; TestDatabases.oracle, "FCQRS_TEST_ORACLE" ] do
                match server with
                | Some _ -> conserved server 10
                | None ->
                    let kind = match variable with "FCQRS_TEST_POSTGRES" -> "PostgreSQL" | "FCQRS_TEST_SQLSERVER" -> "SQL Server" | _ -> "Oracle"
                    ptestCase $"workflow bank invariant on {kind}: set {variable} to run" ignore ])
