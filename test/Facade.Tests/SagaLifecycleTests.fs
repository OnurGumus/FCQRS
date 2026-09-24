module SagaLifecycleTests

open System
open System.IO
open System.Threading
open Expecto
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging.Abstractions
open FCQRS.Common
open FCQRS.FSharp
open FCQRS.Model.Data

type ApprovalCommand = Approve
type ApprovalEvent = Approved
type ApprovalSagaState = Recorded

let private withSystem name (run: IActor -> string -> unit) =
    let db = Path.Combine(Path.GetTempPath(), $"fcqrs_saga_lifecycle_{Guid.NewGuid():N}.db")
    let api =
        Fcqrs.actor (VerifySerialization.configuration().Build()) NullLoggerFactory.Instance
            (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={db};")) name
    try
        run api db
    finally
        api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

// The journal table can be briefly invisible to a separate connection after a reply,
// as the restart tests in Tests.fs also allow for; polling callers treat that as no rows.
let private journalCount (db: string) (persistenceIdPattern: string) =
    try
        use connection = new SqliteConnection($"Data Source={db};")
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <- "SELECT COUNT(*) FROM journal WHERE persistence_id LIKE $pattern"
        command.Parameters.AddWithValue("$pattern", persistenceIdPattern) |> ignore
        Convert.ToInt64(command.ExecuteScalar())
    with :? SqliteException -> 0L

let private deferredVerdict =
    testCase "saga lifecycle: a deferred repeat verdict does not start another saga"
    <| fun _ ->
        withSystem "DeferredVerdict" <| fun api db ->
            let starts = ref 0
            let approvals =
                Fcqrs.aggregate api
                    { Name = "Approval"
                      Initial = false
                      Decide = fun (_: Command<ApprovalCommand>) approved -> persistIf (not approved) Approved
                      Fold = fun (_: Event<ApprovalEvent>) _ -> true
                      Snapshots = NoSnapshots
                      Passivation = PassivationPolicy.Default }
            let saga =
                Fcqrs.saga api
                    { Name = "ApprovalSaga"
                      InitialData = ()
                      Originator = approvals.Factory
                      HandleEvent =
                        fun event state ->
                            match event, state.State with
                            | :? Event<ApprovalEvent>, None -> StateChangedEvent Recorded
                            | _ -> UnhandledEvent
                      ApplySideEffects =
                        fun state _ ->
                            match state.State with
                            | Recorded ->
                                Interlocked.Increment(&starts.contents) |> ignore
                                StopSaga, []
                      StartOn = fun (_: Event<ApprovalEvent>) -> true
                      Snapshots = NoSnapshots }
            Fcqrs.wireSagaStarters api [ saga ]
            let approve () =
                Async.RunSynchronously(
                    approvals.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "request-1") Approve (fun _ -> true), 20000)
            let first = approve ()
            // The repeat carries a new correlation ID, so a start would create a second saga.
            let repeat = approve ()
            Thread.Sleep 2000
            let deadline = DateTime.UtcNow.AddSeconds 10.0
            while journalCount db "Approval/%" < 1L && DateTime.UtcNow < deadline do
                Thread.Sleep 50
            Expect.equal (first.Journaled, repeat.Journaled) (Some true, Some false)
                "the first approval is journaled and the repeat is deferred"
            Expect.equal (journalCount db "Approval/%") 1L "only the first approval is in the journal"
            Expect.equal starts.Value 1 "only the journaled approval starts a saga"

type NoteCommand = Note
type NoteEvent = Noted
type NoteSagaState = Logged

let private manifestsWithoutVersions =
    testCase "saga lifecycle: saga journal rows carry no assembly versions"
    <| fun _ ->
        Fcqrs.journalTypes [ journalType<NoteEvent> "lifecycle.note.event"; journalType<NoteSagaState> "lifecycle.note.state" ]
        withSystem "SagaManifests" <| fun api db ->
            let notes =
                Fcqrs.aggregate api
                    { Name = "Notes"
                      Initial = 0
                      Decide = fun (_: Command<NoteCommand>) _ -> PersistEvent Noted
                      Fold = fun (_: Event<NoteEvent>) state -> state + 1
                      Snapshots = NoSnapshots
                      Passivation = PassivationPolicy.Default }
            let saga =
                Fcqrs.saga api
                    { Name = "NotesSaga"
                      InitialData = ()
                      Originator = notes.Factory
                      HandleEvent =
                        fun event state ->
                            match event, state.State with
                            | :? Event<NoteEvent>, None -> StateChangedEvent Logged
                            | _ -> UnhandledEvent
                      ApplySideEffects = fun state _ -> match state.State with Logged -> StopSaga, []
                      StartOn = fun (_: Event<NoteEvent>) -> true
                      Snapshots = NoSnapshots }
            Fcqrs.wireSagaStarters api [ saga ]
            Async.RunSynchronously(notes.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "note") Note (fun _ -> true), 20000)
            |> ignore
            let deadline = DateTime.UtcNow.AddSeconds 10.0
            while journalCount db "NotesSaga/%" < 2L && DateTime.UtcNow < deadline do
                Thread.Sleep 50
            Expect.isGreaterThanOrEqual (journalCount db "NotesSaga/%") 2L "the saga journaled its start and its state"
            let readManifests () =
                use connection = new SqliteConnection($"Data Source={db};")
                connection.Open()
                use command = connection.CreateCommand()
                command.CommandText <- "SELECT manifest FROM journal WHERE persistence_id NOT LIKE '/%'"
                use reader = command.ExecuteReader()
                [ while reader.Read() do yield reader.GetString 0 ]
            let rec retry attempts =
                try readManifests ()
                with :? SqliteException when attempts > 0 ->
                    Thread.Sleep 100
                    retry (attempts - 1)
            let manifests = retry 50
            let versioned = manifests |> List.filter (fun manifest -> manifest.Contains "Version=")
            Expect.isEmpty versioned "an older node cannot bind a newer assembly version"
            Expect.exists manifests (fun manifest -> manifest.Contains "saga-wrap(lifecycle.note.state,lifecycle.note.event)")
                "the saga's state rows use the registered names"

type LedgerCommand = Record
type LedgerEvent = Recorded
type LedgerSagaState = Waiting

let private wrapperTagRecovers =
    testCase "saga lifecycle: a saga stored under registered names recovers after a restart"
    <| fun _ ->
        Fcqrs.journalTypes [ journalType<LedgerEvent> "lifecycle.ledger.event"; journalType<LedgerSagaState> "lifecycle.ledger.state" ]
        let db = Path.Combine(Path.GetTempPath(), $"fcqrs_saga_wrap_{Guid.NewGuid():N}.db")
        let lmdb = Path.Combine(Path.GetTempPath(), $"fcqrs_saga_wrap_lmdb_{Guid.NewGuid():N}")
        // A durable remember-entities store lets the second system restart the saga by itself.
        let start (recovered: Threading.Tasks.TaskCompletionSource<LedgerSagaState>) =
            let configuration =
                VerifySerialization.configuration()
                    .AddInMemoryCollection(
                        [ Collections.Generic.KeyValuePair<string, string | null>(
                              "config:akka:cluster:distributed-data:durable:lmdb", lmdb) ])
                    .Build()
            let api =
                Fcqrs.actor configuration NullLoggerFactory.Instance
                    (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={db};")) "SagaWrapRecovery"
            let ledgers =
                Fcqrs.aggregate api
                    { Name = "Ledgers"
                      Initial = 0
                      Decide = fun (_: Command<LedgerCommand>) _ -> PersistEvent Recorded
                      Fold = fun (_: Event<LedgerEvent>) state -> state + 1
                      Snapshots = NoSnapshots
                      Passivation = PassivationPolicy.Default }
            let saga =
                Fcqrs.saga api
                    { Name = "LedgerSaga"
                      InitialData = ()
                      Originator = ledgers.Factory
                      HandleEvent =
                        fun event state ->
                            match event, state.State with
                            | :? Event<LedgerEvent>, None -> StateChangedEvent Waiting
                            | _ -> UnhandledEvent
                      ApplySideEffects =
                        fun state recovering ->
                            if recovering then recovered.TrySetResult state.State |> ignore
                            Stay, []
                      StartOn = fun (_: Event<LedgerEvent>) -> true
                      Snapshots = NoSnapshots }
            Fcqrs.wireSagaStarters api [ saga ]
            api, ledgers

        let first, ledgers = start (Threading.Tasks.TaskCompletionSource<LedgerSagaState>())
        try
            Async.RunSynchronously(ledgers.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "ledger") Record (fun _ -> true), 20000)
            |> ignore
            let deadline = DateTime.UtcNow.AddSeconds 10.0
            let stored () =
                try
                    use connection = new SqliteConnection($"Data Source={db};")
                    connection.Open()
                    use command = connection.CreateCommand()
                    command.CommandText <-
                        "SELECT COUNT(*) FROM journal WHERE persistence_id LIKE 'LedgerSaga/%' AND manifest LIKE '%saga-wrap(lifecycle.ledger.state,lifecycle.ledger.event)%'"
                    Convert.ToInt64(command.ExecuteScalar()) > 0L
                with :? SqliteException -> false
            while not (stored ()) && DateTime.UtcNow < deadline do
                Thread.Sleep 50
            Expect.isTrue (stored ()) "the saga stored its state under the saga-wrap tag"
        finally
            first.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

        let recovered = Threading.Tasks.TaskCompletionSource<LedgerSagaState>()
        let second, _ = start recovered
        try
            Expect.isTrue (recovered.Task.Wait(TimeSpan.FromSeconds 30.0)) "the saga recovered after the restart"
            Expect.equal recovered.Task.Result Waiting "recovery read the stored state"
        finally
            second.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore
            for path in [ db; db + "-wal"; db + "-shm" ] do
                if File.Exists path then File.Delete path

type DoorCommand =
    | Open
    | Knock

type DoorEvent =
    | Opened
    | Knocked

type DoorWatchState = Watching

let private bootDoors (db: string) (lmdb: string) (recovered: ManualResetEventSlim) =
    let configuration =
        VerifySerialization.configuration()
            .AddInMemoryCollection(
                [ Collections.Generic.KeyValuePair<string, string | null>("config:akka:cluster:distributed-data:durable:lmdb", lmdb)
                  // An unanswered start fails the process quickly instead of after 30 seconds.
                  Collections.Generic.KeyValuePair<string, string | null>("config:akka:fcqrs:saga-start-timeout", "10") ])
            .Build()
    let api =
        Fcqrs.actor configuration NullLoggerFactory.Instance
            (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={db};")) "DoorWatch"
    let doors =
        Fcqrs.aggregate api
            { Name = "Door"
              Initial = 0
              Decide =
                fun (command: Command<DoorCommand>) _ ->
                    match command.CommandDetails with
                    | Open -> PersistEvent Opened
                    | Knock -> DeferEvent Knocked
              Fold =
                fun (event: Event<DoorEvent>) state ->
                    match event.EventDetails with
                    | Opened -> state + 1
                    | Knocked -> state
              Snapshots = NoSnapshots
              Passivation = PassivationPolicy.Default }
    let saga =
        Fcqrs.saga api
            { Name = "DoorWatch"
              InitialData = ()
              Originator = doors.Factory
              HandleEvent =
                fun event state ->
                    match event, state.State with
                    | :? Event<DoorEvent> as opened, None when opened.EventDetails = Opened -> StateChangedEvent Watching
                    | _ -> UnhandledEvent
              ApplySideEffects =
                fun state recovering ->
                    // A recovered saga runs its side effects once its subscription is acknowledged.
                    if recovering then recovered.Set()
                    match state.State with
                    // Each retry tick re-sends a harmless deferred command.
                    | Watching ->
                        expecting (TimeSpan.FromMinutes 10.0) (FixedInterval(TimeSpan.FromMilliseconds 300.0))
                            [ toOriginator doors.Factory (box Knock) ], []
              StartOn = fun (event: Event<DoorEvent>) -> event.EventDetails = Opened
              Snapshots = NoSnapshots }
    Fcqrs.wireSagaStarters api [ saga ]
    api, doors, saga

let private recoveredReadiness =
    testCase "saga lifecycle: a recovered saga still answers starting messages after expectation retries"
    <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), $"fcqrs_saga_readiness_{Guid.NewGuid():N}.db")
        let lmdb = Path.Combine(Path.GetTempPath(), $"fcqrs_saga_readiness_lmdb_{Guid.NewGuid():N}")
        let cid = Fcqrs.newCid ()
        let door = Fcqrs.aggregateId "front"
        let send (doors: AggregateHandle<DoorCommand, DoorEvent>) command =
            Async.RunSynchronously(doors.Send cid door command (fun _ -> true), 30000)

        use firstRecovery = new ManualResetEventSlim(false)
        let api1, doors1, _ = bootDoors db lmdb firstRecovery
        let opened =
            try
                send doors1 Open
            with error ->
                api1.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore
                raise error
        try
            // Rows: the starting event, Started, then Watching. The retries need Watching.
            let deadline = DateTime.UtcNow.AddSeconds 10.0
            while journalCount db "DoorWatch/%" < 3L && DateTime.UtcNow < deadline do
                Thread.Sleep 50
            Expect.isGreaterThanOrEqual (journalCount db "DoorWatch/%") 3L "the saga journaled its start and Watching"
        finally
            api1.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

        // Remember-entities recovers the saga in Watching; its subscription is acknowledged afterwards.
        use recovered = new ManualResetEventSlim(false)
        let api2, doors2, saga = bootDoors db lmdb recovered
        try
            Expect.isTrue (recovered.Wait(TimeSpan.FromSeconds 20.0)) "the saga recovered and resubscribed"
            // Let several expectation retry ticks run after the resubscription.
            Thread.Sleep 1500
            // A new event with the same correlation ID would start the same saga again. The saga
            // answers at once, with a refusal, instead of leaving the handshake to time out.
            Expect.throwsT<SagaAlreadyStartedException> (fun () -> send doors2 Open |> ignore)
                "the repeated start was answered with a refusal"
            // Its own start, repeated as a waiting aggregate repeats it, is answered with readiness.
            let sagaId =
                use connection = new SqliteConnection($"Data Source={db};")
                connection.Open()
                use command = connection.CreateCommand()
                command.CommandText <- "SELECT persistence_id FROM journal WHERE persistence_id LIKE 'DoorWatch/%' LIMIT 1"
                (command.ExecuteScalar() :?> string).Split('/') |> Array.last |> Uri.UnescapeDataString
            let starting: SagaStarter.SagaStartingEvent<Event<DoorEvent>> = { Event = opened }
            let answer: obj = (saga.Factory sagaId).Ask(box starting, Some(TimeSpan.FromSeconds 10.0)) |> Async.RunSynchronously
            Expect.stringContains (sprintf "%A" answer) "Continue" "the recovered saga confirmed readiness for its own start"
        finally
            api2.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

type SlowStartCommand = Go
type SlowStartEvent = Went
type SlowStartState = Never

let private stopDuringSave =
    testCase "saga lifecycle: an entity passivated while it saves still answers the command"
    <| fun _ ->
        withSystem "StopDuringSave" <| fun api _ ->
            let orders =
                Fcqrs.aggregate api
                    { Name = "SlowStart"
                      Initial = 0
                      Decide = fun (_: Command<SlowStartCommand>) _ -> PersistEvent Went
                      Fold = fun (_: Event<SlowStartEvent>) state -> state + 1
                      Snapshots = NoSnapshots
                      // Idle passivation fires while the command waits for the saga check below.
                      Passivation = PassivationPolicy.After(TimeSpan.FromMilliseconds 300.0) }
            let saga =
                Fcqrs.saga api
                    { Name = "SlowStartSaga"
                      InitialData = ()
                      Originator = orders.Factory
                      HandleEvent = fun _ _ -> UnhandledEvent
                      ApplySideEffects = fun state _ -> match state.State with Never -> StopSaga, []
                      // Holds the originator in its saga-start check, before it persists.
                      StartOn =
                        fun (_: Event<SlowStartEvent>) ->
                            Thread.Sleep 1500
                            false
                      Snapshots = NoSnapshots }
            Fcqrs.wireSagaStarters api [ saga ]
            let reply =
                Async.RunSynchronously(
                    orders.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "slow") Go (fun _ -> true), 20000)
            Expect.equal reply.EventDetails Went "the persisted event reached the caller"

type CheckCommand = Check
type CheckEvent = Checked

/// The recovery check a saga sends its originator, built as the saga runtime builds it.
let private recoveryCheck<'TEvent when 'TEvent: not null> (cid: CID) (startingEvent: Event<'TEvent>) : obj =
    let continueOrAbort =
        match typeof<IActor>.Assembly.GetType("FCQRS.Common+ContinueOrAbort`1") with
        | null -> failwith "ContinueOrAbort was not found."
        | definition -> definition.MakeGenericType [| typeof<'TEvent> |]
    let flags = Reflection.BindingFlags.Public ||| Reflection.BindingFlags.NonPublic
    let case = Reflection.FSharpType.GetUnionCases(continueOrAbort, flags) |> Array.exactlyOne
    let details = Reflection.FSharpValue.MakeUnion(case, [| box startingEvent |], flags)
    let commandType = typedefof<Command<_>>.MakeGenericType [| continueOrAbort |]
    Reflection.FSharpValue.MakeRecord(
        commandType,
        [| details
           box DateTime.UtcNow
           box (Guid.CreateVersion7().ToString() |> ValueLens.CreateAsResult |> Result.value : MessageId)
           box (None: AggregateId option)
           box cid
           box (Map.empty<string, string>) |],
        flags)
    |> Unchecked.nonNull

/// A stand-in for a saga of `originator`. The originator answers a recovery check only when
/// the asker's name marks it as one of its sagas. Collects the answers it receives.
let private fakeSaga (system: Akka.Actor.ActorSystem) (originator: string) (cid: CID) =
    let replies = Collections.Concurrent.BlockingCollection<obj>()
    let saga =
        Akkling.Spawn.spawn system $"{originator}~Saga~{cid}" (Akkling.Props.props (fun (mailbox: Akkling.Actors.Actor<obj>) ->
            let rec receive () =
                Akkling.ComputationExpressions.actor {
                    let! message = mailbox.Receive()
                    match message with
                    | :? Akkling.Actors.LifecycleEvent -> ()
                    | reply -> replies.Add reply
                    return! receive ()
                }
            receive ()))
        |> Akkling.ActorRefs.untyped
    let reply () =
        let mutable reply: obj = null
        if replies.TryTake(&reply, TimeSpan.FromSeconds 5.0) then Some reply else None
    saga, reply

let private isAbort (reply: obj option) =
    match reply with
    | Some reply ->
        let replyType = reply.GetType()
        replyType.IsGenericType && replyType.GetGenericArguments().[0].Name = "AbortedEvent"
    | None -> false

let private continueOrAbortIdentity =
    testCase "saga lifecycle: recovery continues only on the event stored at that version"
    <| fun _ ->
        withSystem "ContinueOrAbortIdentity" <| fun api _ ->
            let aborts = Collections.Concurrent.ConcurrentQueue<string>()
            use listener = new Diagnostics.ActivityListener()
            listener.ShouldListenTo <- fun source -> source.Name = Telemetry.ActivitySourceName
            listener.Sample <-
                Diagnostics.SampleActivity<Diagnostics.ActivityContext>(fun _ -> Diagnostics.ActivitySamplingResult.AllDataAndRecorded)
            listener.ActivityStopped <-
                fun activity -> if activity.OperationName.StartsWith "Abort:" then aborts.Enqueue activity.OperationName
            Diagnostics.ActivitySource.AddActivityListener listener
            let checks =
                Fcqrs.aggregate api
                    { Name = "Check"
                      Initial = 0
                      Decide = fun (_: Command<CheckCommand>) _ -> PersistEvent Checked
                      Fold = fun (_: Event<CheckEvent>) state -> state + 1
                      Snapshots = NoSnapshots
                      Passivation = PassivationPolicy.Default }
            Fcqrs.wireSagaStarters api []
            let cid = Fcqrs.newCid ()
            let journaled =
                Async.RunSynchronously(checks.Send cid (Fcqrs.aggregateId "check") Check (fun _ -> true), 20000)

            let saga, reply = fakeSaga api.System "check" cid

            // Every other subscriber of the correlation ID, such as a pending send that reuses it.
            let published = Collections.Concurrent.ConcurrentQueue<obj>()
            use subscribed = new ManualResetEventSlim(false)
            let probe =
                Akkling.Spawn.spawn api.System "check-topic-probe" (Akkling.Props.props (fun (mailbox: Akkling.Actors.Actor<obj>) ->
                    let rec receive () =
                        Akkling.ComputationExpressions.actor {
                            let! message = mailbox.Receive()
                            match message with
                            | :? Akka.Cluster.Tools.PublishSubscribe.SubscribeAck -> subscribed.Set()
                            | :? Akkling.Actors.LifecycleEvent -> ()
                            | other -> published.Enqueue other
                            return! receive ()
                        }
                    receive ()))
                |> Akkling.ActorRefs.untyped
            let topic = "check~" + (cid |> ValueLens.Value |> ValueLens.Value)
            Akka.Cluster.Tools.PublishSubscribe.DistributedPubSub.Get(api.System).Mediator.Tell(
                Akka.Cluster.Tools.PublishSubscribe.Subscribe(topic, probe), probe)
            Expect.isTrue (subscribed.Wait(TimeSpan.FromSeconds 5.0)) "the probe listens on the correlation topic"

            let entity = checks.Factory "check"
            let continued () =
                match reply () with
                | Some(:? Event<CheckEvent> as event) -> event.Id = journaled.Id
                | _ -> false
            let aborted () = isAbort (reply ())
            let newId () : MessageId = Guid.CreateVersion7().ToString() |> ValueLens.CreateAsResult |> Result.value
            let otherEvent = { journaled with Id = newId () }

            // The starting event is the latest stored event.
            entity.Tell(recoveryCheck cid journaled, saga)
            Expect.isTrue (continued ()) "the latest stored event lets the saga continue"
            entity.Tell(recoveryCheck cid otherEvent, saga)
            Expect.isTrue (aborted ()) "a different event at the latest version aborts the saga"

            // A later event is stored, so the answer comes from the journal.
            Async.RunSynchronously(checks.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "check") Check (fun _ -> true), 20000)
            |> ignore
            entity.Tell(recoveryCheck cid journaled, saga)
            Expect.isTrue (continued ()) "a stored event lets the saga continue after later events"
            entity.Tell(recoveryCheck cid otherEvent, saga)
            Expect.isTrue (aborted ()) "a different event stored at that version aborts the saga"
            let beyondVersion: Version = 5L |> ValueLens.TryCreate |> Result.value
            let beyond = { journaled with Id = newId (); Version = beyondVersion }
            entity.Tell(recoveryCheck cid beyond, saga)
            Expect.isTrue (aborted ()) "an event beyond the stored versions aborts the saga"

            Expect.equal aborts.Count 3 "every abort is flagged in the trace"
            Thread.Sleep 500
            Expect.isEmpty published "answers go only to the saga that asked"

// An aggregate recovered from a snapshot with no later events does not know which event holds its
// version. Answering from the version alone let a saga continue for an event that was never stored:
// the bank invariant test found a transfer credited without its debit.
let private snapshotRecoveryIdentity =
    testCase "saga lifecycle: after a snapshot recovery, recovery continues only on the event stored at that version"
    <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), $"fcqrs_saga_snapshot_identity_{Guid.NewGuid():N}.db")
        let start () =
            let api =
                Fcqrs.actor (VerifySerialization.configuration().Build()) NullLoggerFactory.Instance
                    (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={db};")) "SnapshotIdentity"
            let checks =
                Fcqrs.aggregate api
                    { Name = "Check"
                      Initial = 0
                      Decide = fun (_: Command<CheckCommand>) _ -> PersistEvent Checked
                      Fold = fun (_: Event<CheckEvent>) state -> state + 1
                      // A snapshot after every event: recovery replays no event after it.
                      Snapshots = Every 1
                      Passivation = PassivationPolicy.Default }
            Fcqrs.wireSagaStarters api []
            api, checks
        let cid = Fcqrs.newCid ()
        let first, checks = start ()
        let journaled =
            try
                let journaled =
                    Async.RunSynchronously(checks.Send cid (Fcqrs.aggregateId "check") Check (fun _ -> true), 20000)
                // Snapshots are saved after the reply.
                let deadline = DateTime.UtcNow.AddSeconds 10.0
                let snapshots () =
                    try
                        use connection = new SqliteConnection($"Data Source={db};")
                        connection.Open()
                        use command = connection.CreateCommand()
                        command.CommandText <- "SELECT COUNT(*) FROM snapshot WHERE persistence_id LIKE 'Check/%'"
                        Convert.ToInt64(command.ExecuteScalar())
                    with :? SqliteException -> 0L
                while snapshots () = 0L && DateTime.UtcNow < deadline do
                    Thread.Sleep 50
                Expect.equal (snapshots ()) 1L "the aggregate saved a snapshot at version 1"
                journaled
            finally
                first.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

        let second, checks = start ()
        try
            let saga, reply = fakeSaga second.System "check" cid
            let entity = checks.Factory "check"
            let newId () : MessageId = Guid.CreateVersion7().ToString() |> ValueLens.CreateAsResult |> Result.value
            // The saga's starting event was never stored; another event holds its version.
            entity.Tell(recoveryCheck cid { journaled with Id = newId () }, saga)
            Expect.isTrue (isAbort (reply ())) "a different event at the snapshot's version aborts the saga"
            entity.Tell(recoveryCheck cid journaled, saga)
            match reply () with
            | Some(:? Event<CheckEvent> as event) -> Expect.equal event.Id journaled.Id "the stored event lets the saga continue"
            | other -> failtest $"the stored event lets the saga continue, but the answer was {other}"
        finally
            second.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore
            SqliteConnection.ClearAllPools()
            for path in [ db; db + "-wal"; db + "-shm" ] do
                if File.Exists path then File.Delete path

/// An in-memory journal whose next single-event reads fail, as a journal's reads do while its
/// database is unavailable. Recovery reads are not affected.
type FlakyReadJournal() =
    inherit Akka.Persistence.Journal.MemoryJournal()
    static let failuresLeft = ref 0
    static member FailNextReads(count: int) = failuresLeft.Value <- count

    override _.ReplayMessagesAsync(context, persistenceId, fromSequenceNr, toSequenceNr, max, recoveryCallback) =
        if max = 1L && fromSequenceNr = toSequenceNr && Interlocked.Decrement(&failuresLeft.contents) >= 0 then
            Threading.Tasks.Task.FromException(InvalidOperationException "The test journal cannot read right now.")
        else
            base.ReplayMessagesAsync(context, persistenceId, fromSequenceNr, toSequenceNr, max, recoveryCallback)

let private journalReadRetry =
    testCase "saga lifecycle: a recovery check retries while the journal cannot read"
    <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), $"fcqrs_saga_lifecycle_{Guid.NewGuid():N}.db")
        let configuration =
            VerifySerialization.configuration()
                .AddInMemoryCollection(
                    [ Collections.Generic.KeyValuePair<string, string | null>("config:akka:persistence:journal:plugin", "akka.persistence.journal.flaky")
                      Collections.Generic.KeyValuePair<string, string | null>(
                          "config:akka:persistence:journal:flaky:class", "SagaLifecycleTests+FlakyReadJournal, Facade.Tests") ])
                .Build()
        let api =
            Fcqrs.actor configuration NullLoggerFactory.Instance
                (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={db};")) "JournalReadRetry"
        try
            let checks =
                Fcqrs.aggregate api
                    { Name = "Retry"
                      Initial = 0
                      Decide = fun (_: Command<CheckCommand>) _ -> PersistEvent Checked
                      Fold = fun (_: Event<CheckEvent>) state -> state + 1
                      Snapshots = NoSnapshots
                      Passivation = PassivationPolicy.Default }
            Fcqrs.wireSagaStarters api []
            let cid = Fcqrs.newCid ()
            let send cid = Async.RunSynchronously(checks.Send cid (Fcqrs.aggregateId "retry") Check (fun _ -> true), 20000)
            let started = send cid
            // A later event makes the check read the journal.
            send (Fcqrs.newCid ()) |> ignore
            let saga, reply = fakeSaga api.System "retry" cid
            FlakyReadJournal.FailNextReads 1
            (checks.Factory "retry").Tell(recoveryCheck cid started, saga)
            match reply () with
            | Some(:? Event<CheckEvent> as event) ->
                Expect.equal event.Id started.Id "the second read found the stored starting event"
            | other -> failtestf "expected the starting event after a retry, got %A" other
        finally
            api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

type EarlyCommand = Early
type EarlyEvent = Arrived

let private starterWiredLate =
    testCase "saga lifecycle: a save before the saga starter is wired waits for it"
    <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), $"fcqrs_saga_lifecycle_{Guid.NewGuid():N}.db")
        let configuration =
            VerifySerialization.configuration()
                .AddInMemoryCollection(
                    // A regression fails the process after 10 seconds instead of 30.
                    [ Collections.Generic.KeyValuePair<string, string | null>("config:akka:fcqrs:saga-start-timeout", "10") ])
                .Build()
        let api =
            Fcqrs.actor configuration NullLoggerFactory.Instance
                (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={db};")) "StarterWiredLate"
        try
            let arrivals =
                Fcqrs.aggregate api
                    { Name = "Arrival"
                      Initial = 0
                      Decide = fun (_: Command<EarlyCommand>) _ -> PersistEvent Arrived
                      Fold = fun (_: Event<EarlyEvent>) state -> state + 1
                      Snapshots = NoSnapshots
                      Passivation = PassivationPolicy.Default }
            // The aggregate is live, as it is while remembered sagas recover, but no saga
            // starter exists yet.
            let pending =
                arrivals.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "early") Early (fun _ -> true) |> Async.StartAsTask
            Thread.Sleep 1000
            Fcqrs.wireSagaStarters api []
            let reply = pending.WaitAsync(TimeSpan.FromSeconds 20.0).GetAwaiter().GetResult()
            Expect.equal reply.EventDetails Arrived "the save completed its handshake once the starter was wired"
        finally
            api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

type ReviewCommand = Submit
type ReviewEvent = Submitted
type ReviewSagaState = Reviewing

let private customSagaNames =
    testCase "saga lifecycle: a custom saga name must keep the correlation id"
    <| fun _ ->
        withSystem "CustomSagaNames" <| fun api db ->
            let reviews =
                Fcqrs.aggregate api
                    { Name = "Review"
                      Initial = 0
                      Decide = fun (_: Command<ReviewCommand>) _ -> PersistEvent Submitted
                      Fold = fun (_: Event<ReviewEvent>) state -> state + 1
                      Snapshots = NoSnapshots
                      Passivation = PassivationPolicy.Default }
            let reviewing = Collections.Concurrent.ConcurrentDictionary<string, bool>()
            let saga name =
                Fcqrs.saga api
                    { Name = name
                      InitialData = ()
                      Originator = reviews.Factory
                      HandleEvent =
                        fun event state ->
                            match event, state.State with
                            | :? Event<ReviewEvent>, None -> StateChangedEvent Reviewing
                            | _ -> UnhandledEvent
                      ApplySideEffects =
                        fun state _ ->
                            match state.State with
                            | Reviewing ->
                                reviewing[name] <- true
                                StopSaga, []
                      StartOn = fun (_: Event<ReviewEvent>) -> true
                      Snapshots = NoSnapshots }
            let prefixed = saga "PrefixedReview"
            let renamed = saga "RenamedReview"
            api.InitializeSagaStarter(fun (event: obj) ->
                [ if prefixed.StartOn event then
                      yield prefixed.Factory, PrefixConversion(Some(fun cid -> "audit~" + cid)), event
                  if renamed.StartOn event then
                      yield renamed.Factory, PrefixConversion(Some(fun cid -> cid + "-renamed")), event ])
            let reply =
                Async.RunSynchronously(
                    reviews.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "review-1") Submit (fun _ -> true), 20000)
            Expect.equal reply.EventDetails Submitted "the refused saga does not hold up the originator's save"
            let deadline = DateTime.UtcNow.AddSeconds 10.0
            while not (reviewing.ContainsKey "PrefixedReview") && DateTime.UtcNow < deadline do
                Thread.Sleep 50
            Expect.isTrue (reviewing.ContainsKey "PrefixedReview")
                "a prefix that ends with '~' keeps the correlation id, so the saga receives the journaled start"
            Thread.Sleep 1000
            Expect.equal (journalCount db "RenamedReview/%") 0L
                "a saga whose name changes the correlation id would never receive its originator's events"

type BellCommand = Ring
type BellEvent = Rang

type BellWatch =
    | Waiting
    | Unanswered

let private bootBells
    (db: string)
    (lmdb: string)
    (exhausted: Collections.Concurrent.ConcurrentQueue<DateTime>)
    (recovered: ManualResetEventSlim)
    =
    let configuration =
        VerifySerialization.configuration()
            .AddInMemoryCollection(
                [ Collections.Generic.KeyValuePair<string, string | null>("config:akka:cluster:distributed-data:durable:lmdb", lmdb)
                  Collections.Generic.KeyValuePair<string, string | null>("config:akka:fcqrs:saga-start-timeout", "10") ])
            .Build()
    let api =
        Fcqrs.actor configuration NullLoggerFactory.Instance
            (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={db};")) "BellWatch"
    let bells =
        Fcqrs.aggregate api
            { Name = "Bell"
              Initial = 0
              Decide = fun (_: Command<BellCommand>) _ -> PersistEvent Rang
              Fold = fun (_: Event<BellEvent>) state -> state + 1
              Snapshots = NoSnapshots
              Passivation = PassivationPolicy.Default }
    // The low-level API: Waiting is the initial state, so no state entry is ever persisted for it.
    let sagas =
        api.InitializeSaga
            { Data = 0; State = Waiting }
            (fun event state ->
                match event, state.State with
                | :? ExpectationExhausted as exhaustion, Waiting ->
                    exhausted.Enqueue exhaustion.EnteredAt
                    StateChangedEvent Unanswered
                | _ -> UnhandledEvent)
            (fun state (_: SagaStarter.SagaStartingEvent<Event<BellEvent>> option) recovering ->
                if recovering then recovered.Set()
                match state.State with
                | Waiting -> expecting (TimeSpan.FromSeconds 6.0) (FixedInterval(TimeSpan.FromMinutes 1.0)) [], []
                | Unanswered -> StopSaga, [])
            id
            "BellWatch"
            NoSnapshots
    let factory (entityId: string) = sagas.RefFor DEFAULT_SHARD entityId
    api.InitializeSagaStarter(fun (event: obj) ->
        match event with
        | :? Event<BellEvent> -> [ factory ]
        | _ -> [])
    api, bells

let private initialStateDeadline =
    testCase "saga lifecycle: a restart does not postpone an initial-state expectation"
    <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), $"fcqrs_saga_deadline_{Guid.NewGuid():N}.db")
        let lmdb = Path.Combine(Path.GetTempPath(), $"fcqrs_saga_deadline_lmdb_{Guid.NewGuid():N}")
        let exhausted = Collections.Concurrent.ConcurrentQueue<DateTime>()
        use firstRecovery = new ManualResetEventSlim(false)
        let api1, bells1 = bootBells db lmdb exhausted firstRecovery
        let rung = DateTime.UtcNow
        try
            Async.RunSynchronously(bells1.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "door") Ring (fun _ -> true), 30000)
            |> ignore
            let deadline = DateTime.UtcNow.AddSeconds 10.0
            while journalCount db "BellWatch/%" < 1L && DateTime.UtcNow < deadline do
                Thread.Sleep 50
            Expect.isGreaterThanOrEqual (journalCount db "BellWatch/%") 1L "the saga journaled its start"
            // Stop halfway through the six-second expectation.
            Thread.Sleep 3000
        finally
            api1.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

        use recovered = new ManualResetEventSlim(false)
        let api2, _ = bootBells db lmdb exhausted recovered
        try
            Expect.isTrue (recovered.Wait(TimeSpan.FromSeconds 20.0)) "the saga recovered in its initial state"
            let deadline = DateTime.UtcNow.AddSeconds 15.0
            while exhausted.IsEmpty && DateTime.UtcNow < deadline do
                Thread.Sleep 50
            match exhausted.TryPeek() with
            | true, enteredAt ->
                Expect.isLessThan (enteredAt - rung) (TimeSpan.FromSeconds 2.0)
                    "the deadline is measured from the journaled start, not from recovery"
            | _ -> failtest "the expectation was never exhausted"
        finally
            api2.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

type GateCommand =
    | OpenGate
    | Bump

type GateEvent =
    | GateOpened
    | Bumped

type GateSagaState = Parked

type private CapturingLogger(category: string, sink: Collections.Concurrent.ConcurrentQueue<string>) =
    interface Microsoft.Extensions.Logging.ILogger with
        member _.BeginScope<'TState when 'TState: not null>(_state: 'TState) : IDisposable | null = null
        member _.IsEnabled(_level) = true
        member _.Log<'TState>(_level, _eventId, state: 'TState, error: exn | null, formatter: Func<'TState, exn | null, string>) =
            sink.Enqueue(category + " | " + formatter.Invoke(state, error))

type private CapturingLoggerFactory(sink: Collections.Concurrent.ConcurrentQueue<string>) =
    interface Microsoft.Extensions.Logging.ILoggerFactory with
        member _.CreateLogger(category: string) = CapturingLogger(category, sink) :> Microsoft.Extensions.Logging.ILogger
        member _.AddProvider(_provider) = ()
        member _.Dispose() = ()

/// `parked` lets the saga leave the framework's Started state on GateOpened and reports it.
/// Without it the saga never leaves Started.
let private bootGates
    (db: string)
    (lmdb: string)
    (loggerFactory: Microsoft.Extensions.Logging.ILoggerFactory)
    (parked: ManualResetEventSlim option)
    =
    let configuration =
        VerifySerialization.configuration()
            .AddInMemoryCollection(
                [ Collections.Generic.KeyValuePair<string, string | null>("config:akka:cluster:distributed-data:durable:lmdb", lmdb) ])
            .Build()
    let api =
        Fcqrs.actor configuration loggerFactory
            (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={db};")) "GateAbort"
    let gates =
        Fcqrs.aggregate api
            { Name = "Gate"
              Initial = 0
              Decide =
                fun (command: Command<GateCommand>) _ ->
                    match command.CommandDetails with
                    | OpenGate -> PersistEvent GateOpened
                    | Bump -> PersistEvent Bumped
              Fold = fun (_: Event<GateEvent>) state -> state + 1
              Snapshots = NoSnapshots
              Passivation = PassivationPolicy.Default }
    let saga =
        Fcqrs.saga api
            { Name = "GateSaga"
              InitialData = ()
              Originator = gates.Factory
              HandleEvent =
                fun event state ->
                    match event, state.State with
                    | :? Event<GateEvent> as opened, None when parked.IsSome && opened.EventDetails = GateOpened ->
                        StateChangedEvent Parked
                    | _ -> UnhandledEvent
              ApplySideEffects =
                fun (state: SagaState<unit, GateSagaState>) _ ->
                    match state.State with
                    | Parked ->
                        parked |> Option.iter (fun signal -> signal.Set())
                        StopSaga, []
              StartOn = fun (event: Event<GateEvent>) -> event.EventDetails = GateOpened
              Snapshots = NoSnapshots }
    Fcqrs.wireSagaStarters api [ saga ]
    api, gates

let private deleteRow (db: string) (persistenceIdPattern: string) (sequenceNr: int64) =
    use connection = new SqliteConnection($"Data Source={db};")
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- "DELETE FROM journal WHERE persistence_id LIKE $pattern AND sequence_number = $sequence"
    command.Parameters.AddWithValue("$pattern", persistenceIdPattern) |> ignore
    command.Parameters.AddWithValue("$sequence", sequenceNr) |> ignore
    command.ExecuteNonQuery() |> ignore

/// Starts GateSaga with GateOpened (gate version 1) and stops the system once the saga has
/// stored its starting event and Started. `thenSend` runs on the gate before the stop.
let private startGateSaga (db: string) (lmdb: string) (thenSend: GateCommand list) =
    let gate = Fcqrs.aggregateId "front"
    let api, gates = bootGates db lmdb NullLoggerFactory.Instance None
    try
        Async.RunSynchronously(gates.Send (Fcqrs.newCid ()) gate OpenGate (fun _ -> true), 20000) |> ignore
        let deadline = DateTime.UtcNow.AddSeconds 10.0
        while journalCount db "GateSaga/%" < 2L && DateTime.UtcNow < deadline do
            Thread.Sleep 50
        for command in thenSend do
            Async.RunSynchronously(gates.Send (Fcqrs.newCid ()) gate command (fun _ -> true), 20000) |> ignore
    finally
        api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore
    Expect.equal (journalCount db "GateSaga/%") 2L "the saga stored its starting event and Started"
    // A stop between the saga's two writes leaves only its starting event.
    deleteRow db "GateSaga/%" 2L
    Expect.equal (journalCount db "GateSaga/%") 1L "only the starting event remains"

let private storedStartContinues =
    testCase "saga lifecycle: a saga recovered before Started continues when its starting event is stored"
    <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), $"fcqrs_saga_gate_{Guid.NewGuid():N}.db")
        let lmdb = Path.Combine(Path.GetTempPath(), $"fcqrs_saga_gate_lmdb_{Guid.NewGuid():N}")
        // Bumped (gate version 2) is stored after the saga's starting event.
        startGateSaga db lmdb [ Bump ]
        // Remember-entities recovers the saga before Started. The gate is at version 2 and finds
        // version 1 in its journal, so it answers with the starting event.
        let logs = Collections.Concurrent.ConcurrentQueue<string>()
        use parked = new ManualResetEventSlim(false)
        let api, _ = bootGates db lmdb (new CapturingLoggerFactory(logs)) (Some parked)
        try
            Expect.isTrue (parked.Wait(TimeSpan.FromSeconds 25.0)) "the saga continued from its stored starting event"
            Expect.isFalse (logs.ToArray() |> Array.contains "GateSaga | Aborting") "the saga was not aborted"
        finally
            api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let private abortBeforeStarted =
    testCase "saga lifecycle: a saga recovered before Started ends when its starting event was never stored"
    <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), $"fcqrs_saga_gate_{Guid.NewGuid():N}.db")
        let lmdb = Path.Combine(Path.GetTempPath(), $"fcqrs_saga_gate_lmdb_{Guid.NewGuid():N}")
        startGateSaga db lmdb []
        // The gate's own write of the starting event failed after the saga started.
        deleteRow db "Gate/%" 1L
        Expect.equal (journalCount db "Gate/%") 0L "the gate's journal does not hold the starting event"
        let logs = Collections.Concurrent.ConcurrentQueue<string>()
        let api, _ = bootGates db lmdb (new CapturingLoggerFactory(logs)) None
        try
            let aborted () = logs.ToArray() |> Array.contains "GateSaga | Aborting"
            let deadline = DateTime.UtcNow.AddSeconds 25.0
            while not (aborted ()) && DateTime.UtcNow < deadline do
                Thread.Sleep 100
            Expect.isTrue (aborted ()) "the saga ends when its originator never stored its starting event"
        finally
            api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let tests =
    testSequenced (
        testList
            "saga lifecycle"
            [ deferredVerdict
              manifestsWithoutVersions
              wrapperTagRecovers
              recoveredReadiness
              stopDuringSave
              continueOrAbortIdentity
              snapshotRecoveryIdentity
              journalReadRetry
              starterWiredLate
              customSagaNames
              initialStateDeadline
              storedStartContinues
              abortBeforeStarted ]
    )
