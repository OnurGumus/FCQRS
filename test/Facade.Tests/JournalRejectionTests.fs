module JournalRejectionTests

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Akka.Persistence
open Expecto
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging.Abstractions
open FCQRS.Common
open FCQRS.FSharp

// A rejected write terminates the process, so each scenario runs in a child process.

type TollCommand =
    | Pass
    | Refuse

type TollEvent =
    | Passed
    | RefusedByTestJournal

type TollSagaState = SagaRefusedByTestJournal

/// An in-memory journal that rejects every write whose payload mentions RefusedByTestJournal.
type RefusingJournal() =
    inherit Akka.Persistence.Journal.MemoryJournal()

    member private _.Refused(write: AtomicWrite) =
        match write.Payload with
        | :? IEnumerable<IPersistentRepresentation> as events ->
            events |> Seq.exists (fun event -> (sprintf "%A" event.Payload).Contains "RefusedByTestJournal")
        | _ -> false

    member private _.WriteAccepted(writes: AtomicWrite list, token: CancellationToken) =
        base.WriteMessagesAsync(writes, token)

    override this.WriteMessagesAsync(messages: IEnumerable<AtomicWrite>, token: CancellationToken) =
        let writes = List.ofSeq messages
        task {
            let! _ = this.WriteAccepted(writes |> List.filter (this.Refused >> not), token)
            let refusal = InvalidOperationException "The test journal refuses this event."
            return
                writes
                |> List.map (fun write -> if this.Refused write then refusal :> exn else Unchecked.defaultof<exn>)
                |> ImmutableList.CreateRange
                :> IImmutableList<exn>
        }

[<Literal>]
let ChildFlag = "--journal-rejection-child"

// A projection that finds a journal row missing terminates the process: reading again cannot
// bring the row back, and skipping it would leave the read model wrong without a sign.
let private missingHistory (db: string) : int =
    let api =
        Fcqrs.actor (VerifySerialization.configuration().Build()) NullLoggerFactory.Instance
            (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={db};")) "MissingHistory"
    let tolls =
        Fcqrs.aggregate api
            { Name = "Toll"
              Initial = 0
              Decide = fun (_: Command<TollCommand>) _ -> PersistEvent Passed
              Fold = fun (_: Event<TollEvent>) state -> state + 1
              Snapshots = NoSnapshots
              Passivation = PassivationPolicy.Default }
    Fcqrs.wireSagaStarters api []
    for _ in 1..2 do
        Async.RunSynchronously(tolls.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "gate") Pass (fun _ -> true), 30000) |> ignore
    do
        use connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};")
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <- "DELETE FROM journal WHERE persistence_id LIKE 'Toll/%' AND sequence_number = 1"
        command.ExecuteNonQuery() |> ignore
    printfn "journal-rejection-child: the actor system started"
    Fcqrs.projection api (Projection.single FromStart ignore) |> ignore
    Thread.Sleep 10000
    printfn "journal-rejection-child: the process survived the missing history"
    api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore
    0

let private rejectedWrites (scenario: string) (db: string) : int =
    let kv (key: string) (value: string) = KeyValuePair<string, string | null>(key, value)
    let configuration =
        VerifySerialization.configuration()
            .AddInMemoryCollection(
                [ kv "config:akka:persistence:journal:plugin" "akka.persistence.journal.refusing"
                  kv "config:akka:persistence:journal:refusing:class" "JournalRejectionTests+RefusingJournal, Facade.Tests"
                  kv "config:akka:fcqrs:command-timeout" "5" ])
            .Build()
    let api =
        Fcqrs.actor configuration NullLoggerFactory.Instance
            (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={db};")) "JournalRejection"
    let tolls =
        Fcqrs.aggregate api
            { Name = "Toll"
              Initial = 0
              Decide =
                fun (command: Command<TollCommand>) _ ->
                    match command.CommandDetails with
                    | Pass -> PersistEvent Passed
                    | Refuse -> PersistEvent RefusedByTestJournal
              Fold = fun (_: Event<TollEvent>) state -> state + 1
              Snapshots = NoSnapshots
              Passivation = PassivationPolicy.Default }
    match scenario with
    | "saga" ->
        // The saga's first own state is refused by the journal.
        let saga =
            Fcqrs.saga api
                { Name = "TollSaga"
                  InitialData = ()
                  Originator = tolls.Factory
                  HandleEvent =
                    fun event state ->
                        match event, state.State with
                        | :? Event<TollEvent>, None -> StateChangedEvent SagaRefusedByTestJournal
                        | _ -> UnhandledEvent
                  ApplySideEffects = fun state _ -> match state.State with SagaRefusedByTestJournal -> StopSaga, []
                  StartOn = fun (event: Event<TollEvent>) -> event.EventDetails = Passed
                  Snapshots = NoSnapshots }
        Fcqrs.wireSagaStarters api [ saga ]
    | _ -> Fcqrs.wireSagaStarters api []
    let send command =
        Async.RunSynchronously(tolls.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "gate") command (fun _ -> true), 30000)
    printfn "journal-rejection-child: the actor system started"
    // The saga refuses its own state change as soon as this event starts it.
    send Pass |> ignore
    if scenario = "aggregate" then
        try send Refuse |> ignore with _ -> ()
    Thread.Sleep 10000
    printfn "journal-rejection-child: the process survived the rejected write"
    api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore
    0

let runChild (scenario: string) (db: string) : int =
    match scenario with
    | "missing-history" -> missingHistory db
    | _ -> rejectedWrites scenario db

let private runScenario (scenario: string) =
    let db = Path.Combine(Path.GetTempPath(), $"fcqrs_journal_rejection_{Guid.NewGuid():N}.db")
    let host = Environment.ProcessPath |> Unchecked.nonNull
    let start = ProcessStartInfo(host)
    // Under `dotnet Facade.Tests.dll` the host is the muxer, which needs the assembly path.
    if Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase) then
        start.ArgumentList.Add typeof<RefusingJournal>.Assembly.Location
    for argument in [ ChildFlag; scenario; db ] do
        start.ArgumentList.Add argument
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.UseShellExecute <- false
    use child = Process.Start start |> Unchecked.nonNull
    let output = child.StandardOutput.ReadToEndAsync()
    let errors = child.StandardError.ReadToEndAsync()
    let exited = child.WaitForExit 60000
    if not exited then child.Kill true
    child.WaitForExit()
    for path in [ db; db + "-wal"; db + "-shm" ] do
        if File.Exists path then File.Delete path
    exited, child.ExitCode, output.Result, errors.Result

let private rejectedWrite (scenario: string) (message: string) =
    testCase $"journal rejection: a rejected {scenario} write terminates the process"
    <| fun _ ->
        let exited, exitCode, output, errors = runScenario scenario
        Expect.isTrue exited "the child process finished"
        Expect.stringContains output "the actor system started" "the scenario ran"
        Expect.isFalse (output.Contains "survived the rejected write") "the process did not continue past the rejection"
        Expect.notEqual exitCode 0 "the process terminated"
        Expect.stringContains errors message "the termination names the rejected write"

let private missingHistoryTerminates =
    testCase "journal history: a projection that finds a journal row missing terminates the process"
    <| fun _ ->
        let exited, exitCode, output, errors = runScenario "missing-history"
        Expect.isTrue exited "the child process finished"
        Expect.stringContains output "the actor system started" "the scenario ran"
        Expect.isFalse (output.Contains "survived the missing history") "the process did not continue past the missing row"
        Expect.notEqual exitCode 0 "the process terminated"
        Expect.stringContains errors "journal history is missing" "the termination names the missing history"

let tests =
    testSequenced (
        testList
            "journal rejection"
            [ rejectedWrite "aggregate" "the journal rejected an event"
              rejectedWrite "saga" "the journal rejected a saga event"
              missingHistoryTerminates ]
    )
