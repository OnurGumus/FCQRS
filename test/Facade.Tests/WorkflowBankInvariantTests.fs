module WorkflowBankInvariantTests

// The bank's transfer written as a workflow. Processes are killed while transfers run, and a new
// process finishes them. The vaults together must hold what they held at the start: no kill may
// create money, lose it, or move a transfer twice.
//
// FCQRS_TEST_POSTGRES names a server, as for the saga version of this test.

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
open FCQRS.Workflows

/// The argument that makes this assembly run as the child that starts transfers.
[<Literal>]
let ChildFlag = "--workflow-bank-child"

[<Literal>]
let private Prefix = "workflow-bank-child: "

// A vault answers a repeated command with the verdict it gave the first time. A workflow sends a
// command again after a restart when the reply was not stored, so this is what keeps a transfer
// from moving money twice.
type VaultCommand =
    | Open of balance: decimal
    | Take of transferId: string * amount: decimal
    | Give of transferId: string * amount: decimal
    | GiveBack of transferId: string * amount: decimal
    | Report

type VaultEvent =
    | Opened of balance: decimal
    | Taken of transferId: string * amount: decimal
    | Given of transferId: string * amount: decimal
    | GivenBack of transferId: string * amount: decimal
    | Refused of reason: string
    | Reported of balance: decimal

type VaultState =
    { IsOpen: bool
      Balance: decimal
      Taken: Set<string>
      Given: Set<string>
      GivenBack: Set<string> }

let private initial =
    { IsOpen = false
      Balance = 0m
      Taken = Set.empty
      Given = Set.empty
      GivenBack = Set.empty }

let private decide (command: Command<VaultCommand>) (state: VaultState) =
    match command.CommandDetails, state.IsOpen with
    | Open _, true -> DeferEvent(Refused "already open")
    | Open balance, false -> PersistEvent(Opened balance)
    | _, false -> DeferEvent(Refused "not open")
    | Report, _ -> DeferEvent(Reported state.Balance)
    // The repeat check comes before the funds check: a repeated Take must not be refused because
    // the first one already took the money.
    | Take(id, amount), _ when state.Taken.Contains id -> DeferEvent(Taken(id, amount))
    | Take(_, amount), _ when amount > state.Balance -> DeferEvent(Refused "insufficient funds")
    | Take(id, amount), _ -> PersistEvent(Taken(id, amount))
    | Give(id, amount), _ when state.Given.Contains id -> DeferEvent(Given(id, amount))
    | Give(id, amount), _ -> PersistEvent(Given(id, amount))
    | GiveBack(id, amount), _ when state.GivenBack.Contains id -> DeferEvent(GivenBack(id, amount))
    | GiveBack(id, amount), _ -> PersistEvent(GivenBack(id, amount))

let private fold (event: Event<VaultEvent>) (state: VaultState) =
    match event.EventDetails with
    | Opened balance -> { state with IsOpen = true; Balance = balance }
    // A repeated verdict is deferred and must change nothing.
    | Taken(id, _) when state.Taken.Contains id -> state
    | Taken(id, amount) -> { state with Balance = state.Balance - amount; Taken = state.Taken.Add id }
    | Given(id, _) when state.Given.Contains id -> state
    | Given(id, amount) -> { state with Balance = state.Balance + amount; Given = state.Given.Add id }
    | GivenBack(id, _) when state.GivenBack.Contains id -> state
    | GivenBack(id, amount) -> { state with Balance = state.Balance + amount; GivenBack = state.GivenBack.Add id }
    | Refused _
    | Reported _ -> state

/// Take the money, deliver it, and give it back if delivery is refused.
let private transfer (vaults: AggregateHandle<VaultCommand, VaultEvent>) =
    Workflow.define "VaultTransfer" (fun (id: string, source: string, target: string, amount: decimal) -> workflow {
        let! taken = send vaults source (Take(id, amount))
        match taken with
        | Taken _ ->
            let! given = send vaults target (Give(id, amount))
            match given with
            | Given _ -> return "moved"
            | _ ->
                let! (_: VaultEvent) = send vaults source (GiveBack(id, amount))
                return "returned"
        | _ -> return "refused"
    })

let private vaultNames = [| for i in 0..5 -> $"v{i}" |]
// Never opened: delivery to one of them is refused, and the money goes back.
let private closedNames = [| "closed0"; "closed1" |]
let private opening = 1000m
let private total = opening * decimal vaultNames.Length

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

/// An actor system on the journal, with no aggregate or workflow registered, so nothing recovers.
let private actorOn (journal: Journal) =
    let configuration =
        VerifySerialization.configuration()
            .AddInMemoryCollection([ KeyValuePair<string, string | null>("config:akka:fcqrs:command-timeout", "10") ])
            .Build()
    let connection =
        match journal with
        | Sqlite path -> Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={path};"
        | Postgres connectionString -> Fcqrs.connect FCQRS.Actor.DBType.PostgreSQL15 connectionString
    Fcqrs.actor configuration NullLoggerFactory.Instance (Some connection) "WorkflowBank"

let private start (journal: Journal) =
    let api = actorOn journal
    let vaults =
        Fcqrs.aggregate api
            { Name = "Vault"
              Initial = initial
              Decide = decide
              Fold = fold
              Snapshots = Every 3
              Passivation = PassivationPolicy.Default }
    Fcqrs.wireSagaStarters api []
    // Registering the workflow resumes the transfers an earlier process left running.
    let transfers = Workflows.register api (transfer vaults)
    api, vaults, transfers

let private ask (vaults: AggregateHandle<VaultCommand, VaultEvent>) (vault: string) command =
    Async.RunSynchronously(vaults.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId vault) command (fun _ -> true), 30000)

/// Starts random transfers from concurrent callers until the parent kills the process, and
/// reports each transfer whose start was stored.
let runChild (seed: string) (journal: string) : int =
    let _, vaults, transfers = start (Journal.Parse journal)
    // Report ready once the bank and the transfers' shard region answer, so the kill lands while
    // transfers are running and not while the regions are still starting.
    ask vaults vaultNames[0] Report |> ignore
    Async.RunSynchronously(transfers.Status "warm-up", 60000) |> ignore
    Console.WriteLine(Prefix + "ready")
    let caller (random: Random) =
        async {
            while true do
                let source = vaultNames[random.Next vaultNames.Length]
                let target =
                    if random.Next 6 = 0 then
                        closedNames[random.Next closedNames.Length]
                    else
                        let others = vaultNames |> Array.filter ((<>) source)
                        others[random.Next others.Length]
                let id = Guid.NewGuid().ToString "N"
                try
                    let! started = transfers.Start id (id, source, target, decimal (random.Next(1, 300)))
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
        info.ArgumentList.Add typeof<VaultState>.Assembly.Location
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

/// The journal's persistence IDs, and every stored vault event grouped by vault.
let private stored (api: IActor) =
    let journal = PersistenceQuery.Get(api.System).ReadJournalFor<SqlReadJournal>(SqlReadJournal.Identifier)
    let ids = journal.CurrentPersistenceIds().RunWith(Sink.Seq<string>(), api.Materializer).Result |> List.ofSeq
    let vaults =
        dict [
            for id in ids do
                if id.StartsWith "Vault/" then
                    let events =
                        journal.CurrentEventsByPersistenceId(id, 0L, Int64.MaxValue).RunWith(Sink.Seq<EventEnvelope>(), api.Materializer).Result
                        |> Seq.map (fun envelope -> envelope.Event :?> Event<VaultEvent>)
                        |> List.ofSeq
                    yield id.Substring(id.LastIndexOf '/' + 1), events ]
    let transfers = [ for id in ids do if id.StartsWith "VaultTransfer/" then id.Substring(id.LastIndexOf '/' + 1) ]
    transfers, vaults

/// The transfers whose journal has no final record.
let private unfinishedIn (api: IActor) =
    let journal = PersistenceQuery.Get(api.System).ReadJournalFor<SqlReadJournal>(SqlReadJournal.Identifier)
    let ids = journal.CurrentPersistenceIds().RunWith(Sink.Seq<string>(), api.Materializer).Result
    [ for id in ids do
        if id.StartsWith "VaultTransfer/" then
            let last =
                journal.CurrentEventsByPersistenceId(id, 0L, Int64.MaxValue).RunWith(Sink.Seq<EventEnvelope>(), api.Materializer).Result
                |> Seq.map (fun envelope -> envelope.Event :?> WorkflowRecord)
                |> Seq.last
            match last with
            | WorkflowRecord.Finished _ | WorkflowRecord.Faulted _ | WorkflowRecord.Cancelled _ -> ()
            | _ -> id ]

let private withJournal (postgres: string option) (test: Journal -> unit) =
    match postgres with
    | None ->
        let path = Path.Combine(Path.GetTempPath(), $"fcqrs_workflow_bank_{Guid.NewGuid():N}.db")
        try
            test (Sqlite path)
        finally
            SqliteConnection.ClearAllPools()
            for file in [ path; path + "-wal"; path + "-shm" ] do
                if File.Exists file then File.Delete file
    | Some root ->
        let database = $"fcqrs_workflow_bank_{Guid.NewGuid():N}"
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
        let api, vaults, _ = start journal
        try
            for name in vaultNames do
                ask vaults name (Open opening) |> ignore
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
    let api, vaults, transfers = start journal
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
        let taken = pick (function Taken(id, _) -> Some id | _ -> None)
        let given = pick (function Given(id, _) -> Some id | _ -> None)
        let back = pick (function GivenBack(id, _) -> Some id | _ -> None)
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
        Expect.equal (Seq.sum balances.Values) total $"the vaults together still hold {total} ({context})"
        for KeyValue(name, balance) in balances do
            Expect.equal (ask vaults name Report).EventDetails (Reported balance) $"{name} recovered the balance its journal holds ({context})"
    finally
        api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let private conserved (postgres: string option) (rounds: int) =
    let database = if postgres.IsSome then "PostgreSQL" else "SQLite"
    testCase $"workflow bank invariant: transfers written as workflows keep the total across process kills ({database})"
    <| fun _ -> withJournal postgres (killAndCheck database rounds)

let tests =
    let postgres =
        match Environment.GetEnvironmentVariable "FCQRS_TEST_POSTGRES" with
        | null | "" -> None
        | value -> Some value
    testSequenced (
        testList "workflow bank invariant" [
            conserved None 6
            match postgres with
            | Some _ -> conserved postgres 10
            | None -> ptestCase "workflow bank invariant on PostgreSQL: set FCQRS_TEST_POSTGRES to run" ignore ])
