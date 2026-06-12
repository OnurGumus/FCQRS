/// End-to-end smoke test for the FCQRS.FSharp facade against a real (SQLite-backed)
/// actor system. Exercises: Fcqrs.actor / connect / newCid / aggregateId,
/// Fcqrs.aggregate + the typed handle .Send, Fcqrs.saga + toOriginator +
/// wireSagaStarters, and Fcqrs.projection + the ISubscribe stream.
///
/// The saga assertion uses a timeout, so a facade that registers a saga which
/// never fires (e.g. a miswired originator-event type) fails cleanly instead of
/// hanging.
module FacadeTests

open System
open System.Diagnostics
open System.IO
open Expecto
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging.Abstractions
open FCQRS.Common
open FCQRS.Model.Data
open FCQRS.FSharp

/// A trivial counter aggregate.
module Counter =
    type Command =
        | Increment of int
        | Split of int * int
        | Checkpoint
        | Reset

    type Event =
        | Incremented of int
        | WasReset

    type State = { Total: int }
    let initial = { Total = 0 }

    let decide (cmd: Command<Command>) _state =
        match cmd.CommandDetails with
        | Increment n -> Incremented n |> PersistEvent
        // one command, two events, one journal AtomicWrite
        | Split(a, b) -> persistAll [ Incremented a; Incremented b ]
        // manual checkpoint: persist + immediate snapshot, no cadence involved
        | Checkpoint -> WasReset |> persistAndSnapshot
        | Reset -> WasReset |> PersistEvent

    let fold (e: Event<Event>) state =
        match e.EventDetails with
        | Incremented n -> { state with Total = state.Total + n }
        | WasReset -> { Total = 0 }

/// A saga that resets the counter whenever it sees a "big" (>= 100) increment —
/// proving the saga reacts to an originator event and drives a command back into
/// the originator via toOriginator.
module AutoReset =
    type State =
        | Resetting
        | Finished

    let private handleEvent (evt: obj) (sagaState: SagaState<_, _>) =
        match evt, sagaState.State with
        | :? (Event<Counter.Event>) as e, None ->
            match e.EventDetails with
            | Counter.Incremented n when n >= 100 -> Resetting |> StateChangedEvent
            | _ -> UnhandledEvent
        | :? (Event<Counter.Event>) as e, Some Resetting ->
            match e.EventDetails with
            | Counter.WasReset -> Finished |> StateChangedEvent
            | _ -> UnhandledEvent
        | _ -> UnhandledEvent

    let private applySideEffects counterFactory (sagaState: SagaState<_, _>) _recovering =
        match sagaState.State with
        | Resetting -> Stay, [ toOriginator counterFactory (Counter.Reset :> obj) ]
        | Finished -> StopSaga, []

    let private startsOn (e: Event<Counter.Event>) =
        match e.EventDetails with
        | Counter.Incremented n -> n >= 100
        | _ -> false

    let definition counterFactory =
        { Name = "AutoReset"
          InitialData = ()
          Originator = counterFactory
          HandleEvent = handleEvent
          ApplySideEffects = applySideEffects counterFactory
          StartOn = startsOn
          Snapshots = Default }

/// A saga that parks: on a big increment it enters Pinging, whose side effect
/// sends Reset to the originator — and then it ignores everything, staying in
/// Pinging forever. Used to prove that a saga recovered THROUGH A SNAPSHOT
/// still re-drives its side effects (the SagaStartingEventWrapper at journal
/// seq 1 is never replayed past a snapshot; the snapshot must carry it).
module Parked =
    type State = Pinging

    let private handleEvent (evt: obj) (sagaState: SagaState<_, _>) =
        match evt, sagaState.State with
        | :? (Event<Counter.Event>) as e, None ->
            match e.EventDetails with
            | Counter.Incremented n when n >= 100 -> Pinging |> StateChangedEvent
            | _ -> UnhandledEvent
        | _ -> UnhandledEvent // parked: WasReset and everything else is ignored

    let private applySideEffects counterFactory (sagaState: SagaState<_, _>) _recovering =
        match sagaState.State with
        | Pinging -> Stay, [ toOriginator counterFactory (Counter.Reset :> obj) ]

    let private startsOn (e: Event<Counter.Event>) =
        match e.EventDetails with
        | Counter.Incremented n -> n >= 100
        | _ -> false

    let definition counterFactory =
        { Name = "Parked"
          InitialData = ()
          Originator = counterFactory
          HandleEvent = handleEvent
          ApplySideEffects = applySideEffects counterFactory
          StartOn = startsOn
          // The cadence under test: snapshot every 2 versions, set via the API
          // (no config key) — Started=v1, Pinging=v2 -> snapshot covers the journal.
          Snapshots = Every 2 }

let private projection (_offset: int64) (ev: obj) : IMessageWithCID list =
    match ev with
    | :? (Event<Counter.Event>) as e -> [ e :> IMessageWithCID ]
    | _ -> []

let private boot () =
    let db = Path.Combine(Path.GetTempPath(), sprintf "fcqrs_facade_%s.db" (Guid.NewGuid().ToString("N")))
    let api =
        Fcqrs.actor (ConfigurationBuilder().Build()) NullLoggerFactory.Instance
            (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite (sprintf "Data Source=%s;" db))) "FacadeSmoke"
    let counter =
        Fcqrs.aggregate api { Name = "Counter"; Initial = Counter.initial; Decide = Counter.decide; Fold = Counter.fold; Snapshots = Default }
    let saga = Fcqrs.saga api (AutoReset.definition counter.Factory)
    Fcqrs.wireSagaStarters api [ saga ]
    let subs = Fcqrs.projection api { LastOffset = 0; Handle = projection }
    counter, subs

let private isWasReset (m: IMessageWithCID) =
    match m with
    | :? (Event<Counter.Event>) as e ->
        match e.EventDetails with
        | Counter.WasReset -> true
        | _ -> false
    | _ -> false

/// Boot a system for the snapshot-recovery test. The saga's cadence comes from
/// its definition (Snapshots = Every 2 — the per-entity API override under
/// test); per-test durable-ddata directory so two sequential incarnations
/// share sharding state but tests don't contend.
let private bootParked (db: string) (lmdb: string) =
    let cfg =
        ConfigurationBuilder()
            .AddInMemoryCollection(
                [ Collections.Generic.KeyValuePair<string, string | null>(
                      "config:akka:cluster:distributed-data:durable:lmdb", lmdb) ])
            .Build()

    let api =
        Fcqrs.actor cfg NullLoggerFactory.Instance
            (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite (sprintf "Data Source=%s;" db))) "SnapshotSmoke"

    let counter =
        Fcqrs.aggregate api { Name = "Counter"; Initial = Counter.initial; Decide = Counter.decide; Fold = Counter.fold; Snapshots = Default }

    let saga = Fcqrs.saga api (Parked.definition counter.Factory)
    Fcqrs.wireSagaStarters api [ saga ]
    api, counter

let private roundTripTest =
    testCase "facade: aggregate handle round-trips a command, and a saga drives a follow-up into the originator"
    <| fun _ ->
        let counter, subs = boot ()

        // 1. the typed handle round-trips command -> event
        let ev =
            counter.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "c1") (Counter.Increment 5)
                (function Counter.Incremented _ -> true | _ -> false)
            |> Async.RunSynchronously
        Expect.equal ev.EventDetails (Counter.Incremented 5) "Send returns the persisted event"

        // 2. a big increment must trigger the saga -> Reset -> WasReset (seen via the projection)
        use sawReset = subs.Subscribe(isWasReset, 1)
        counter.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "big") (Counter.Increment 100)
            (function Counter.Incremented _ -> true | _ -> false)
        |> Async.RunSynchronously
        |> ignore
        Expect.isTrue (sawReset.Task.Wait(TimeSpan.FromSeconds 20.0)) "the saga reset the counter (WasReset observed through the projection)"

let private snapshotRecoveryTest =
    testCase "facade: a saga recovered through a snapshot re-drives its side effects"
    <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), sprintf "fcqrs_snap_%s.db" (Guid.NewGuid().ToString("N")))
        let lmdb = Path.Combine(Path.GetTempPath(), sprintf "fcqrs_snap_lmdb_%s" (Guid.NewGuid().ToString("N")))

        // Phase 1: park the saga past a snapshot boundary, then kill the system.
        // Saga journal: wrapper(seq1), Started(v1), Pinging(v2 -> snapshot).
        // Counter journal: Incremented(v1), WasReset(v2) from the parked side effect.
        let api1, counter1 = bootParked db lmdb
        let subs1 = Fcqrs.projection api1 { LastOffset = 0; Handle = projection }
        use sawReset = subs1.Subscribe(isWasReset, 1)

        counter1.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "big") (Counter.Increment 100)
            (function Counter.Incremented _ -> true | _ -> false)
        |> Async.RunSynchronously
        |> ignore

        Expect.isTrue (sawReset.Task.Wait(TimeSpan.FromSeconds 20.0)) "phase 1: the parked saga sent Reset once"
        Threading.Thread.Sleep 3000 // let the async SaveSnapshot land before the kill
        api1.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

        // Phase 2: reboot on the same journal. remember-entities resurrects the
        // saga; its recovery passes through the snapshot (the wrapper event is
        // NOT replayed). A WasReset at version 3 can only exist if the recovered
        // saga re-issued its pending Reset — the regression under test.
        let api2, _ = bootParked db lmdb

        let isFreshReset (m: IMessageWithCID) =
            match m with
            | :? (Event<Counter.Event>) as e ->
                (match e.EventDetails with
                 | Counter.WasReset -> true
                 | _ -> false)
                && (e.Version |> ValueLens.Value) = 3L
            | _ -> false

        // Fresh replay-from-0 projection per attempt: immune to broadcast-hub
        // attach races, and replays the v3 event once it exists.
        let mutable seen = false
        let mutable attempts = 0

        while not seen && attempts < 15 do
            attempts <- attempts + 1
            let subs = Fcqrs.projection api2 { LastOffset = 0; Handle = projection }
            use awaiter = subs.Subscribe(isFreshReset, 1)
            seen <- awaiter.Task.Wait(TimeSpan.FromSeconds 2.0)

        Expect.isTrue seen "phase 2: the recovered saga re-drove Reset (WasReset v3 in the journal)"
        api2.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let private persistAllTest =
    testCase "facade: PersistAllEvents journals a batch atomically with sequential versions"
    <| fun _ ->
        let counter, _subs = boot ()

        // One command -> two events. Awaiting the SECOND event proves the whole
        // batch was journaled and published; its version proves sequencing.
        let second =
            counter.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "batch") (Counter.Split(3, 4))
                (function Counter.Incremented 4 -> true | _ -> false)
            |> Async.RunSynchronously

        Expect.equal (second.Version |> ValueLens.Value) 2L "the batch's second event is version 2"

        // A follow-up single event lands at version 3 — the batch advanced the
        // aggregate's version by exactly two.
        let next =
            counter.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "batch") (Counter.Increment 1)
                (function Counter.Incremented 1 -> true | _ -> false)
            |> Async.RunSynchronously

        Expect.equal (next.Version |> ValueLens.Value) 3L "a follow-up event continues after the batch"

let private manualSnapshotTest =
    testCase "facade: PersistAndSnapshot writes a snapshot row immediately"
    <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), sprintf "fcqrs_manualsnap_%s.db" (Guid.NewGuid().ToString("N")))
        let api =
            Fcqrs.actor (ConfigurationBuilder().Build()) NullLoggerFactory.Instance
                (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite (sprintf "Data Source=%s;" db))) "ManualSnapSmoke"
        let counter =
            Fcqrs.aggregate api { Name = "Counter"; Initial = Counter.initial; Decide = Counter.decide; Fold = Counter.fold; Snapshots = Default }
        Fcqrs.wireSagaStarters api []

        let snapshotCount () =
            use conn = new Microsoft.Data.Sqlite.SqliteConnection(sprintf "Data Source=%s;" db)
            conn.Open()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT COUNT(*) FROM snapshot WHERE persistence_id LIKE '%snapcheck%'"
            cmd.ExecuteScalar() :?> int64

        // v1: plain persist — default cadence (30) means no snapshot
        counter.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "snapcheck") (Counter.Increment 1)
            (function Counter.Incremented _ -> true | _ -> false)
        |> Async.RunSynchronously |> ignore

        // v2: manual checkpoint — must produce a snapshot row despite cadence 30
        counter.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "snapcheck") Counter.Checkpoint
            (function Counter.WasReset -> true | _ -> false)
        |> Async.RunSynchronously |> ignore

        // SaveSnapshot is async; poll briefly
        let mutable count = 0L
        let mutable attempts = 0
        while count = 0L && attempts < 20 do
            attempts <- attempts + 1
            Threading.Thread.Sleep 250
            count <- try snapshotCount () with _ -> 0L

        Expect.isTrue (count >= 1L) "the manual checkpoint wrote a snapshot row (version 2, far below the cadence of 30)"
        api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let private telemetryTest =
    testCase "facade: spans flow command -> event -> projection under the caller's trace"
    <| fun _ ->
        let captured = Collections.Concurrent.ConcurrentBag<string * ActivityTraceId>()
        use listener = new ActivityListener()
        listener.ShouldListenTo <- fun src -> Array.contains src.Name Telemetry.AllActivitySources
        listener.Sample <- SampleActivity<ActivityContext>(fun _ -> ActivitySamplingResult.AllDataAndRecorded)
        listener.ActivityStopped <- fun a -> captured.Add((a.OperationName, a.TraceId))
        ActivitySource.AddActivityListener listener

        let counter, _subs = boot ()

        // The "HTTP request": an ambient activity current while the command is
        // created. Its context must ride the command's metadata into every span.
        use root = new Activity("test-root")
        root.Start() |> ignore

        counter.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "traced") (Counter.Increment 7)
            (function Counter.Incremented _ -> true | _ -> false)
        |> Async.RunSynchronously
        |> ignore

        root.Stop()

        // Spans are emitted on actor/stream threads; the projection one is last.
        let mutable attempts = 0

        let has (prefix: string) =
            captured |> Seq.exists (fun (name, tid) -> name.StartsWith prefix && tid = root.TraceId)

        while not (has "Command:" && has "Event:" && has "Projection:") && attempts < 40 do
            attempts <- attempts + 1
            Threading.Thread.Sleep 250

        Expect.isTrue (has "Command:") "a Command span carries the caller's TraceId"
        Expect.isTrue (has "Event:") "an Event span carries the caller's TraceId"
        Expect.isTrue (has "Projection:") "a Projection span carries the caller's TraceId"

let tests =
    testSequenced (
        testList "facade" [ roundTripTest; persistAllTest; manualSnapshotTest; telemetryTest; snapshotRecoveryTest ]
    )

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv tests
