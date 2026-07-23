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
    /// Inspectable DATA description of a side effect — returned by decide via
    /// `dispatch`, executed by the registered runner. decide never touches the
    /// oracle, so it stays pure and unit-testable by structural equality.
    type Effect =
        | AskThenAdd of int // "oracle" yields n; runner -> Increment n
        | AskThenFail // "oracle" throws; total -> Reset

    type Command =
        | Increment of int
        | Split of int * int
        | Checkpoint
        | Reset
        // acked to the caller but never journaled — exercises DeferEvent delivery
        | Poke
        // deferred AND state-changing: a fold that must never reach a snapshot
        | Freebie of int
        // deferred read of the live state (nothing journaled)
        | Report
        // decide returns a RunAsync effect (mini saga); the runner self-dispatches
        | Dispatch of Effect

    type Event =
        | Incremented of int
        | WasReset
        | Poked
        | Freebied of int
        | Reported of int

    type State = { Total: int }
    let initial = { Total = 0 }

    let decide (cmd: Command<Command>) state =
        match cmd.CommandDetails with
        | Increment n -> Incremented n |> PersistEvent
        // one command, two events, one journal AtomicWrite
        | Split(a, b) -> persistAll [ Incremented a; Incremented b ]
        // manual checkpoint: persist + immediate snapshot, no cadence involved
        | Checkpoint -> WasReset |> persistAndSnapshot
        | Reset -> WasReset |> PersistEvent
        | Poke -> Poked |> DeferEvent
        | Freebie n -> Freebied n |> DeferEvent
        | Report -> Reported state.Total |> DeferEvent
        // pure: returns the effect DESCRIPTION, no oracle in sight
        | Dispatch eff -> dispatch eff

    let fold (e: Event<Event>) state =
        match e.EventDetails with
        | Incremented n -> { state with Total = state.Total + n }
        | WasReset -> { Total = 0 }
        | Poked -> state
        | Freebied n -> { state with Total = state.Total + n }
        | Reported _ -> state

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

/// A saga that starts on a big increment and then never leaves the framework's
/// Started state (its handleEvent ignores everything). Used to prove restart
/// detection: a saga recovering in Started re-sends ContinueOrAbort carrying its
/// starting event, and an originator that has moved past that event's version
/// must answer with AbortedEvent (observable as an "Abort:" span) instead of
/// re-publishing a stale event.
module Handshake =
    type State = Idle // never entered: the saga parks in the framework's Started

    let private handleEvent (_: obj) (_: SagaState<unit, State option>) : EventAction<State> = UnhandledEvent

    let private applySideEffects (sagaState: SagaState<unit, State>) _recovering =
        match sagaState.State with
        | Idle -> StopSaga, [] // unreachable: handleEvent never transitions here

    let private startsOn (e: Event<Counter.Event>) =
        match e.EventDetails with
        | Counter.Incremented n -> n >= 100
        | _ -> false

    let definition counterFactory =
        { Name = "Handshake"
          InitialData = ()
          Originator = counterFactory
          HandleEvent = handleEvent
          ApplySideEffects = applySideEffects
          StartOn = startsOn
          Snapshots = Default }

/// A CLR "rename" of Counter.Event: same cases/shape, different type identity.
module RenamedCounter =
    type Event =
        | Incremented of int
        | WasReset
        | Poked

/// A second, trivially different aggregate type. Two aggregate TYPES can share
/// an entity id ("shared") — the saga-starter handshake used to key its batches
/// by the bare entity id, so their concurrent handshakes collided and overwrote
/// each other's reply-to: one originator timed out and FailFasted the process.
module CounterB =
    type Command = Increment of int

    type Event = Incremented of int

    type State = { Total: int }
    let initial = { Total = 0 }

    let decide (cmd: Command<Command>) _state =
        match cmd.CommandDetails with
        | Increment n -> Incremented n |> PersistEvent

    let fold (e: Event<Event>) state =
        match e.EventDetails with
        | Incremented n -> { state with Total = state.Total + n }

/// Minimal saga for the cross-type handshake test: it starts and parks in the
/// framework's Started state — only the start handshake matters.
module DualStart =
    type State = Idle // never entered: the saga parks in the framework's Started

    let private handleEvent (_: obj) (_: SagaState<unit, State option>) : EventAction<State> = UnhandledEvent

    let private applySideEffects (sagaState: SagaState<unit, State>) _recovering =
        match sagaState.State with
        | Idle -> StopSaga, [] // unreachable: handleEvent never transitions here

    let private startsOnA (e: Event<Counter.Event>) =
        match e.EventDetails with
        | Counter.Incremented n -> n >= 100
        | _ -> false

    let private startsOnB (e: Event<CounterB.Event>) =
        match e.EventDetails with
        | CounterB.Incremented n -> n >= 100

    let definitionA counterFactory =
        { Name = "DualStartA"
          InitialData = ()
          Originator = counterFactory
          HandleEvent = handleEvent
          ApplySideEffects = applySideEffects
          StartOn = startsOnA
          Snapshots = Default }

    let definitionB counterFactory =
        { Name = "DualStartB"
          InitialData = ()
          Originator = counterFactory
          HandleEvent = handleEvent
          ApplySideEffects = applySideEffects
          StartOn = startsOnB
          Snapshots = Default }

// Multi-event handler shape: return exactly what to publish.
let private projection (_offset: int64) (ev: obj) : IMessageWithCID list =
    match ev with
    | :? (Event<Counter.Event>) as e -> [ e :> IMessageWithCID ]
    | _ -> []

// Single-event handler shape: just side-effect (here: count what flows
// through); every aggregate event auto-publishes — equivalent to the multi
// handler above for this domain, which is exactly the point of `single`.
let private singleHandled = ref 0

let private registerJournalTypes () =
    Fcqrs.journalTypes [ journalType<Counter.Event> "counter.event"; journalType<CounterB.Event> "counterb.event" ]

/// A total effect runner for Counter: maps each effect description to a
/// command, and `total` maps any oracle exception to a command (never lets it
/// escape). This is the ONLY place the "oracle" lives; decide stays pure.
let private counterRunner (eff: Counter.Effect) : Async<Counter.Command> =
    match eff with
    | Counter.AskThenAdd n ->
        total (fun _ -> Counter.Reset) (async {
            do! Async.Sleep 15
            return Counter.Increment n
        })
    | Counter.AskThenFail ->
        total (fun _ -> Counter.Reset) (async {
            do! Async.Sleep 15
            return failwith "oracle down"
        })

let private mkCommand (details: 'c) : Command<'c> =
    { CommandDetails = details
      Id = Guid.CreateVersion7().ToString() |> ValueLens.CreateAsResult |> Result.value
      CreationDate = DateTime.UtcNow
      CorrelationId = Fcqrs.newCid ()
      Sender = None
      Metadata = Map.empty }

/// Boot a system whose Counter is registered WITH the effect runner, so
/// RunAsync effects have somewhere to run.
let private bootWithRunner () =
    registerJournalTypes ()
    let db = Path.Combine(Path.GetTempPath(), sprintf "fcqrs_facade_run_%s.db" (Guid.NewGuid().ToString("N")))
    let api =
        Fcqrs.actor (ConfigurationBuilder().Build()) NullLoggerFactory.Instance
            (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite (sprintf "Data Source=%s;" db))) "RunAsyncSmoke"
    let counter =
        Fcqrs.aggregateWithEffects api
            { Name = "Counter"; Initial = Counter.initial; Decide = Counter.decide; Fold = Counter.fold; Snapshots = Default }
            counterRunner
    Fcqrs.wireSagaStarters api []
    counter

let private boot () =
    registerJournalTypes ()
    let db = Path.Combine(Path.GetTempPath(), sprintf "fcqrs_facade_%s.db" (Guid.NewGuid().ToString("N")))
    let api =
        Fcqrs.actor (ConfigurationBuilder().Build()) NullLoggerFactory.Instance
            (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite (sprintf "Data Source=%s;" db))) "FacadeSmoke"
    let counter =
        Fcqrs.aggregate api { Name = "Counter"; Initial = Counter.initial; Decide = Counter.decide; Fold = Counter.fold; Snapshots = Default }
    let saga = Fcqrs.saga api (AutoReset.definition counter.Factory)
    Fcqrs.wireSagaStarters api [ saga ]
    // The projection stream invokes the handler sequentially, so a plain incr is safe.
    let subs = Fcqrs.projection api (Projection.single 0 (fun _ _ -> incr singleHandled))
    counter, subs

/// Boot a system with BOTH aggregate types and a DualStart saga per type, so
/// two originators of different types can start sagas for the same entity id.
let private bootDual () =
    registerJournalTypes ()
    let db = Path.Combine(Path.GetTempPath(), sprintf "fcqrs_dual_%s.db" (Guid.NewGuid().ToString("N")))
    let api =
        Fcqrs.actor (ConfigurationBuilder().Build()) NullLoggerFactory.Instance
            (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite (sprintf "Data Source=%s;" db))) "DualSmoke"
    let counterA =
        Fcqrs.aggregate api { Name = "Counter"; Initial = Counter.initial; Decide = Counter.decide; Fold = Counter.fold; Snapshots = Default }
    let counterB =
        Fcqrs.aggregate api { Name = "CounterB"; Initial = CounterB.initial; Decide = CounterB.decide; Fold = CounterB.fold; Snapshots = Default }
    let sagaA = Fcqrs.saga api (DualStart.definitionA counterA.Factory)
    let sagaB = Fcqrs.saga api (DualStart.definitionB counterB.Factory)
    Fcqrs.wireSagaStarters api [ sagaA; sagaB ]
    counterA, counterB

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
    registerJournalTypes ()
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

/// Boot a system for the restart-detection test: same durable-ddata pattern as
/// bootParked (two sequential incarnations, remember-entities resurrects the saga).
let private bootAbort (db: string) (lmdb: string) =
    registerJournalTypes ()
    let cfg =
        ConfigurationBuilder()
            .AddInMemoryCollection(
                [ Collections.Generic.KeyValuePair<string, string | null>(
                      "config:akka:cluster:distributed-data:durable:lmdb", lmdb) ])
            .Build()

    let api =
        Fcqrs.actor cfg NullLoggerFactory.Instance
            (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite (sprintf "Data Source=%s;" db))) "AbortSmoke"

    let counter =
        Fcqrs.aggregate api { Name = "Counter"; Initial = Counter.initial; Decide = Counter.decide; Fold = Counter.fold; Snapshots = Default }

    let saga = Fcqrs.saga api (Handshake.definition counter.Factory)
    Fcqrs.wireSagaStarters api [ saga ]
    api, counter

let private restartDetectionTest =
    testCase "facade: restart detection - a saga recovering against a rolled-forward originator aborts"
    <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), sprintf "fcqrs_abort_%s.db" (Guid.NewGuid().ToString("N")))
        let lmdb = Path.Combine(Path.GetTempPath(), sprintf "fcqrs_abort_lmdb_%s" (Guid.NewGuid().ToString("N")))

        // The abort is observed through its "Abort:" span: the version-mismatch
        // branch is the only place the aggregate emits one.
        let aborts = Collections.Concurrent.ConcurrentBag<string>()
        use listener = new ActivityListener()
        listener.ShouldListenTo <- fun src -> src.Name = Telemetry.ActivitySourceName
        listener.Sample <- SampleActivity<ActivityContext>(fun _ -> ActivitySamplingResult.AllDataAndRecorded)
        listener.ActivityStopped <-
            fun a ->
                if a.OperationName.StartsWith "Abort:" then
                    aborts.Add a.OperationName
        ActivitySource.AddActivityListener listener

        // Phase 1: Incremented 100 (v1) starts the saga, which parks in the
        // framework's Started state. Saga journal: wrapper(seq1), Started(seq2).
        let api1, counter1 = bootAbort db lmdb

        counter1.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "drift") (Counter.Increment 100)
            (function Counter.Incremented _ -> true | _ -> false)
        |> Async.RunSynchronously
        |> ignore

        // Roll the originator forward: v2 makes the saga's starting event (v1) stale.
        counter1.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "drift") (Counter.Increment 5)
            (function Counter.Incremented _ -> true | _ -> false)
        |> Async.RunSynchronously
        |> ignore

        // The Started transition is journaled asynchronously; wait for it so the
        // reboot actually recovers a saga parked in Started.
        let sagaRows () =
            use conn = new Microsoft.Data.Sqlite.SqliteConnection(sprintf "Data Source=%s;" db)
            conn.Open()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT COUNT(*) FROM journal WHERE persistence_id LIKE '%~Saga~%'"
            cmd.ExecuteScalar() :?> int64

        let mutable attempts = 0
        while (try sagaRows () with _ -> 0L) < 2L && attempts < 40 do
            attempts <- attempts + 1
            Threading.Thread.Sleep 250

        Expect.isGreaterThanOrEqual (sagaRows ()) 2L "phase 1: the saga journaled its Started state"
        api1.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore
        Expect.isEmpty aborts "phase 1: no abort while the saga was live"

        // Phase 2: reboot on the same journal. remember-entities resurrects the
        // saga; recovering in Started it sends ContinueOrAbort with its v1 starting
        // event, the originator recovers at v2, versions differ -> the aggregate
        // must answer AbortedEvent (and emit the Abort: span) instead of
        // re-publishing the stale event.
        let api2, _ = bootAbort db lmdb

        let mutable waits = 0
        while aborts.IsEmpty && waits < 80 do
            waits <- waits + 1
            Threading.Thread.Sleep 250

        Expect.isFalse aborts.IsEmpty "phase 2: the version mismatch fired restart detection (AbortedEvent path)"
        api2.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

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

        // 3. both notifications above arrived via Projection.single auto-publish —
        //    and the unit handler itself demonstrably ran for each journal event.
        Expect.isGreaterThan singleHandled.Value 0 "the single-event handler was invoked"

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

        // The async SaveSnapshot must be durable before the kill — otherwise
        // phase 2 recovers from journal replay and the test passes vacuously.
        // Poll the snapshot table instead of sleeping a fixed 3s.
        let sagaSnapshots () =
            use conn = new Microsoft.Data.Sqlite.SqliteConnection(sprintf "Data Source=%s;" db)
            conn.Open()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT COUNT(*) FROM snapshot WHERE persistence_id LIKE '%~Saga~%'"
            cmd.ExecuteScalar() :?> int64

        let mutable snapCount = 0L
        let mutable snapAttempts = 0

        while snapCount = 0L && snapAttempts < 40 do
            snapAttempts <- snapAttempts + 1
            Threading.Thread.Sleep 250
            snapCount <- try sagaSnapshots () with _ -> 0L

        Expect.isGreaterThanOrEqual snapCount 1L "phase 1: the saga snapshot is durable before the kill"
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
    testCase "facade: spans flow command -> event -> projection under the caller's trace, with low-cardinality names"
    <| fun _ ->
        // Capture each span's name, trace id, and the payload tag it carried.
        let captured =
            Collections.Concurrent.ConcurrentBag<string * ActivityTraceId * (obj | null)>()
        use listener = new ActivityListener()
        listener.ShouldListenTo <- fun src -> Array.contains src.Name Telemetry.AllActivitySources
        listener.Sample <- SampleActivity<ActivityContext>(fun _ -> ActivitySamplingResult.AllDataAndRecorded)
        listener.ActivityStopped <-
            fun a ->
                let payloadTag =
                    match a.GetTagItem "command.type" with
                    | null -> a.GetTagItem "event.type"
                    | t -> t
                captured.Add((a.OperationName, a.TraceId, payloadTag))
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

        let named (name: string) =
            captured |> Seq.exists (fun (n, tid, _) -> n = name && tid = root.TraceId)

        while not (named "Command:Increment" && named "Event:Incremented"
                   && (captured |> Seq.exists (fun (n, tid, _) -> n.StartsWith "Projection:" && tid = root.TraceId)))
              && attempts < 40 do
            attempts <- attempts + 1
            Threading.Thread.Sleep 250

        // Span NAMES are exactly the case name — no field values, so per-operation
        // tracing rules (AddTracing) and trace-viewer grouping work, and no payload
        // leaks into an indexed span name.
        Expect.isTrue (named "Command:Increment") "Command span is named by case only (no payload)"
        Expect.isTrue (named "Event:Incremented") "Event span is named by case only (no payload)"

        // The payload detail still rides in the tag (IncludePayloads defaults on).
        let commandTag =
            captured
            |> Seq.tryPick (fun (n, _, tag) -> if n = "Command:Increment" then Option.ofObj tag else None)
        Expect.equal (commandTag |> Option.map string) (Some "Increment 7")
            "the command.type tag carries the full payload by default"

let private payloadSwitchTest =
    testCase "facade: WithPayloadDiagnostics(off) reduces span tags to the case name"
    <| fun _ ->
        let captured = Collections.Concurrent.ConcurrentBag<string * (obj | null)>()
        use listener = new ActivityListener()
        listener.ShouldListenTo <- fun src -> src.Name = Telemetry.ActivitySourceName
        listener.Sample <- SampleActivity<ActivityContext>(fun _ -> ActivitySamplingResult.AllDataAndRecorded)
        listener.ActivityStopped <- fun a -> captured.Add((a.OperationName, a.GetTagItem "command.type"))
        ActivitySource.AddActivityListener listener

        // Global switch — restore it no matter what so the rest of the suite is
        // unaffected (tests run sequenced).
        Telemetry.IncludePayloads <- false
        try
            let counter, _subs = boot ()
            counter.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "redacted") (Counter.Increment 42)
                (function Counter.Incremented _ -> true | _ -> false)
            |> Async.RunSynchronously
            |> ignore

            let mutable attempts = 0
            let commandTag () =
                captured
                |> Seq.tryPick (fun (n, tag) -> if n = "Command:Increment" then Some(Option.ofObj tag) else None)
            while (commandTag ()).IsNone && attempts < 40 do
                attempts <- attempts + 1
                Threading.Thread.Sleep 250

            // Name unchanged (always low-cardinality); tag is now the case name,
            // NOT "Increment 42" — the value 42 never reaches the tag.
            Expect.equal (commandTag () |> Option.flatten |> Option.map string) (Some "Increment")
                "with payloads off, command.type is the case name only"
        finally
            Telemetry.IncludePayloads <- true

let private overflowTest =
    testCase "facade: unconsumed notifications overflow by dropping, not crashing"
    <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), sprintf "fcqrs_overflow_%s.db" (Guid.NewGuid().ToString("N")))

        // Tiny notification buffer so a few dozen events overflow it.
        let cfg =
            ConfigurationBuilder()
                .AddInMemoryCollection(
                    [ Collections.Generic.KeyValuePair<string, string | null>(
                          "config:akka:fcqrs:notification-buffer", "8") ])
                .Build()

        let api =
            Fcqrs.actor cfg NullLoggerFactory.Instance
                (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite (sprintf "Data Source=%s;" db))) "OverflowSmoke"

        let counter =
            Fcqrs.aggregate api { Name = "Counter"; Initial = Counter.initial; Decide = Counter.decide; Fold = Counter.fold; Snapshots = Default }

        Fcqrs.wireSagaStarters api []
        let subs = Fcqrs.projection api { LastOffset = 0; Handle = projection }

        // 40 notification-producing events with NO subscriber attached: with the
        // old Fail strategy this faulted the offer and killed the process.
        for i in 1..40 do
            counter.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "flood") (Counter.Increment i)
                (function Counter.Incremented _ -> true | _ -> false)
            |> Async.RunSynchronously
            |> ignore

        // The stream must still be alive: a subscriber attached NOW must see the
        // next notification.
        use awaiter =
            subs.Subscribe(
                (fun m ->
                    match m with
                    | :? (Event<Counter.Event>) as e -> e.EventDetails = Counter.Incremented 999
                    | _ -> false),
                1)

        counter.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "flood") (Counter.Increment 999)
            (function Counter.Incremented _ -> true | _ -> false)
        |> Async.RunSynchronously
        |> ignore

        Expect.isTrue
            (awaiter.Task.Wait(TimeSpan.FromSeconds 20.0))
            "the notification stream survived the overflow and delivered to a late subscriber"

        api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let private manifestTest =
    testCase "facade: stable journal manifests - scheme, legacy fallback, rename survival"
    <| fun _ ->
        registerJournalTypes ()
        use sys = Akka.Actor.ActorSystem.Create("manifest-test")
        let ser = FCQRS.ActorSerialization.STJSerializer(sys :?> Akka.Actor.ExtendedActorSystem)

        let ev: Event<Counter.Event> =
            { EventDetails = Counter.Incremented 3
              CreationDate = DateTime.UtcNow
              Id = Guid.CreateVersion7().ToString() |> ValueLens.CreateAsResult |> Result.value
              Sender = None
              CorrelationId = Fcqrs.newCid ()
              Version = 1L |> ValueLens.TryCreate |> Result.value
              Metadata = Map.empty }

        // 1. registered payload -> stable structured manifest
        let manifest = ser.Manifest ev
        Expect.equal manifest "fcqrs:ev(counter.event)" "stable manifest for a registered payload"

        let bytes = ser.ToBinary ev
        let back = ser.FromBinary(bytes, manifest) :?> Event<Counter.Event>
        Expect.equal back.EventDetails (Counter.Incremented 3) "stable manifest round-trips"

        // 2. legacy AQN manifest (what every pre-existing journal row carries)
        let aqn = typeof<Event<Counter.Event>>.AssemblyQualifiedName |> string
        let legacy = ser.FromBinary(bytes, aqn) :?> Event<Counter.Event>
        Expect.equal legacy.EventDetails (Counter.Incremented 3) "legacy AQN manifests keep reading"

        // 3. unregistered payload -> legacy manifest (no fcqrs: scheme)
        let unregistered = ser.Manifest(box "plain string")
        Expect.isFalse (unregistered.StartsWith "fcqrs:") "unregistered types fall back to AQN"

        // 4. THE rename test: point the logical name at a different CLR type and
        // the same journal bytes deserialize as the new type. This is the exact
        // scenario that bricks AQN-manifest journals.
        JournalTypes.Remap(typeof<RenamedCounter.Event>, "counter.event")

        try
            let renamed = ser.FromBinary(bytes, "fcqrs:ev(counter.event)") :?> Event<RenamedCounter.Event>
            Expect.equal renamed.EventDetails (RenamedCounter.Incremented 3) "old rows deserialize as the renamed type"
        finally
            // restore for the rest of the suite
            JournalTypes.Remap(typeof<Counter.Event>, "counter.event")

/// The filtered single-event handler (Projection.filtered / Query.filterPublish):
/// a per-event Publish/Suppress say over whether read-your-writes wakes. Pure, so
/// no actor system — just the adapter's three branches.
let private filteredProjectionTest =
    testCase "filtered projection: Publish forwards a CID-bearing event, Suppress forwards nothing"
    <| fun _ ->
        let cid = Fcqrs.newCid ()
        let msg = { new IMessageWithCID with member _.CID = cid }

        let publishAll = FCQRS.Query.filterPublish (fun _ _ -> Publish)
        let suppressAll = FCQRS.Query.filterPublish (fun _ _ -> Suppress)

        let boxedMsg = box msg |> Unchecked.nonNull
        let boxedInt = box 42 |> Unchecked.nonNull
        Expect.equal (publishAll 0L boxedMsg) [ msg ] "Publish forwards the event when it carries a CID"
        Expect.isEmpty (publishAll 0L boxedInt) "Publish on a non-CID event notifies nothing"
        Expect.isEmpty (suppressAll 0L boxedMsg) "Suppress notifies nothing even for a CID-bearing event"

/// persistIf (C# EventActions.PersistConditionally): persist when the guard holds,
/// otherwise defer the same event (returned to the caller but not journalled).
let private persistIfTest =
    testCase "persistIf: true persists, false defers the same event"
    <| fun _ ->
        let e = Counter.Incremented 1
        Expect.equal (persistIf true e) (PersistEvent e) "true -> PersistEvent"
        Expect.equal (persistIf false e) (DeferEvent e) "false -> DeferEvent"

/// ExpectoTickSpec bridge: the feature resource and its steps live in THIS
/// assembly, so this test fails if the bridge ever resolves steps against its
/// own assembly instead of the one it is handed. Its @pending scenario has a
/// deliberately WRONG expectation (999), so it fails loudly if pending ever
/// executes instead of skipping.
let private bridgeTest =
    FCQRS.ExpectoTickSpec.FeatureTest.createTest (Reflection.Assembly.GetExecutingAssembly()) "Facade" "bridge"

/// Feature-level @pending: built from parsed source only — its steps do not
/// exist anywhere, so this line THROWING is the regression (binding ran).
let private pendingBridgeTest =
    FCQRS.ExpectoTickSpec.FeatureTest.createTest (Reflection.Assembly.GetExecutingAssembly()) "Facade" "pending"

/// @focus maps to Expecto Focused. The focused tree is only INSPECTED — never
/// registered — so it cannot skip the real suite.
let private focusShapeTest =
    testCase "bridge: @focus tag maps to Expecto Focused"
    <| fun _ ->
        let tree =
            FCQRS.ExpectoTickSpec.FeatureTest.createTest (Reflection.Assembly.GetExecutingAssembly()) "Facade" "focused"

        let rec states t =
            match t with
            | TestCase(_, s) -> [ s ]
            | TestList(ts, _) -> ts |> List.collect states
            | TestLabel(_, inner, _) -> states inner
            | Test.Sequenced(_, inner) -> states inner

        let ss = states tree
        Expect.contains ss Focused "the @focus scenario is Focused"
        Expect.contains ss Normal "the untagged scenario stays Normal"

/// Delivery stamp + sendAwaiting: a persisted ack is marked journaled and
/// sendAwaiting returns only after the projection processed it; a deferred
/// ack is marked not-journaled and sendAwaiting must NOT wait for a
/// projection event that will never come (the naive await would hang).
let private journaledStampTest =
    testCase "journaled stamp: persisted true, deferred false, sendAwaiting never phantom-waits"
    <| fun _ ->
        let counter, subs = boot ()
        let id = Fcqrs.aggregateId (Guid.NewGuid().ToString "N")
        let incremented = function Counter.Incremented _ -> true | _ -> false
        let poked = function Counter.Poked -> true | _ -> false

        let persisted =
            counter.Send (Fcqrs.newCid ()) id (Counter.Increment 1) incremented
            |> Async.RunSynchronously
        Expect.equal persisted.Journaled (Some true) "persisted ack stamps journaled=true"

        let deferred =
            counter.Send (Fcqrs.newCid ()) id Counter.Poke poked |> Async.RunSynchronously
        Expect.equal deferred.Journaled (Some false) "deferred ack stamps journaled=false"

        let before = singleHandled.Value

        Fcqrs.sendAwaiting subs counter (Fcqrs.newCid ()) id (Counter.Increment 1) incremented
        |> Async.RunSynchronously
        |> ignore
        Expect.isGreaterThan singleHandled.Value before "projection ran before sendAwaiting returned"

        let sw = Stopwatch.StartNew()

        Fcqrs.sendAwaiting subs counter (Fcqrs.newCid ()) id Counter.Poke poked
        |> Async.RunSynchronously
        |> ignore
        sw.Stop()
        Expect.isLessThan sw.Elapsed.TotalSeconds 5.0 "deferred sendAwaiting returned without waiting for a projection event"

/// RunAsync (mini saga): decide stays a PURE, inspectable function; the
/// registered runner self-dispatches a command on success, and `total` turns
/// an oracle failure into a command (not a crash).
let private runAsyncTest =
    testCase "RunAsync: decide pure/inspectable; runner self-dispatches on success and total-failure"
    <| fun _ ->
        // (1) decide is pure — assert the effect description by structural
        //     equality, with no runtime, no oracle, no FCQRS.
        Expect.equal
            (Counter.decide (mkCommand (Counter.Dispatch(Counter.AskThenAdd 7))) Counter.initial)
            (dispatch (Counter.AskThenAdd 7))
            "decide returns the RunAsync description by structural equality"

        let counter = bootWithRunner ()
        let incremented = function Counter.Incremented _ -> true | _ -> false
        let wasReset = function Counter.WasReset -> true | _ -> false

        // Capture spans so we can assert the runner (which runs OFF the mailbox)
        // is traced under the caller's trace, low-cardinality name.
        let captured = Collections.Concurrent.ConcurrentBag<string * ActivityTraceId>()
        use listener = new ActivityListener()
        listener.ShouldListenTo <- fun src -> src.Name = Telemetry.ActivitySourceName
        listener.Sample <- SampleActivity<ActivityContext>(fun _ -> ActivitySamplingResult.AllDataAndRecorded)
        listener.ActivityStopped <- fun a -> captured.Add((a.OperationName, a.TraceId))
        ActivitySource.AddActivityListener listener

        use root = new Activity("test-root")
        root.Start() |> ignore

        // (2) success: runner maps AskThenAdd 7 -> Increment 7, self-dispatched
        //     and re-validated by decide -> Incremented 7.
        let id1 = Fcqrs.aggregateId (Guid.NewGuid().ToString "N")
        let ev =
            counter.Send (Fcqrs.newCid ()) id1 (Counter.Dispatch(Counter.AskThenAdd 7)) incremented
            |> Async.RunSynchronously
        Expect.equal ev.EventDetails (Counter.Incremented 7) "success self-dispatched Increment 7"

        root.Stop()

        // The runner's span (off the mailbox) rides the command's trace context.
        let mutable attempts = 0
        while not (captured |> Seq.exists (fun (n, tid) -> n = "Dispatch:AskThenAdd" && tid = root.TraceId))
              && attempts < 40 do
            attempts <- attempts + 1
            Threading.Thread.Sleep 100
        Expect.isTrue
            (captured |> Seq.exists (fun (n, tid) -> n = "Dispatch:AskThenAdd" && tid = root.TraceId))
            "the runner execution is traced as a low-cardinality Dispatch span under the caller's trace"

        // (3) totality: the oracle throws; `total` maps it to Reset -> WasReset,
        //     NOT an exception/crash.
        let id2 = Fcqrs.aggregateId (Guid.NewGuid().ToString "N")
        let ev2 =
            counter.Send (Fcqrs.newCid ()) id2 (Counter.Dispatch Counter.AskThenFail) wasReset
            |> Async.RunSynchronously
        Expect.equal ev2.EventDetails Counter.WasReset "oracle failure became a command, not a crash"

/// A capped counter: decide depends on recovered state, so a wrong replay is
/// observable as a wrong decision (persist instead of reject).
module Capped =
    type Command = Add of int

    type Event =
        | Added of int
        | Rejected

    type State = { Total: int }
    let initial = { Total = 0 }

    let decide (cmd: Command<Command>) state =
        match cmd.CommandDetails with
        | Add n ->
            if state.Total + n <= 10 then
                Added n |> PersistEvent
            else
                Rejected |> DeferEvent

    let fold (e: Event<Event>) state =
        match e.EventDetails with
        | Added n -> { Total = state.Total + n }
        | Rejected -> state

/// A saga whose terminal state returns StopSaga WITH a delayed command. The
/// command is the saga's final act; it must still be delivered (the old
/// cleanup-on-stop cancelled it together with the saga's reminders).
module FinalPing =
    type State = Finishing

    let private handleEvent (evt: obj) (sagaState: SagaState<_, _>) =
        match evt, sagaState.State with
        | :? (Event<Counter.Event>) as e, None ->
            match e.EventDetails with
            | Counter.Incremented n when n >= 100 -> Finishing |> StateChangedEvent
            | _ -> UnhandledEvent
        | _ -> UnhandledEvent

    let private applySideEffects counterFactory (sagaState: SagaState<_, _>) _recovering =
        match sagaState.State with
        | Finishing ->
            StopSaga, [ toOriginatorAfter counterFactory 500L "final-reset" (Counter.Reset :> obj) ]

    let private startsOn (e: Event<Counter.Event>) =
        match e.EventDetails with
        | Counter.Incremented n -> n >= 100
        | _ -> false

    let definition counterFactory =
        { Name = "FinalPing"
          InitialData = ()
          Originator = counterFactory
          HandleEvent = handleEvent
          ApplySideEffects = applySideEffects counterFactory
          StartOn = startsOn
          Snapshots = Default }

let private bootCapped (db: string) =
    registerJournalTypes ()

    let api =
        Fcqrs.actor (ConfigurationBuilder().Build()) NullLoggerFactory.Instance
            (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite (sprintf "Data Source=%s;" db))) "RecoverySmoke"

    let capped =
        Fcqrs.aggregate api { Name = "Capped"; Initial = Capped.initial; Decide = Capped.decide; Fold = Capped.fold; Snapshots = Default }

    Fcqrs.wireSagaStarters api []
    api, capped

let private aggregateRecoveryTest =
    testCase "facade: a rebooted aggregate rebuilds decision state from its journal"
    <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), sprintf "fcqrs_recovery_%s.db" (Guid.NewGuid().ToString("N")))

        // Phase 1: Total becomes 8 (v1), then stop the system.
        let api1, capped1 = bootCapped db

        let ev1 =
            capped1.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "cap") (Capped.Add 8)
                (function Capped.Added _ -> true | _ -> false)
            |> Async.RunSynchronously

        Expect.equal ev1.EventDetails (Capped.Added 8) "phase 1: first add persisted"
        api1.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

        // Phase 2: reboot on the same journal. If replay rebuilds Total=8, then
        // Add 8 exceeds the cap and is REJECTED; if state were lost (Total=0) it
        // would persist instead — the decision itself proves the replay.
        let api2, capped2 = bootCapped db

        let rejected =
            capped2.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "cap") (Capped.Add 8)
                (function Capped.Rejected -> true | _ -> false)
            |> Async.RunSynchronously

        Expect.equal rejected.EventDetails Capped.Rejected "phase 2: recovered Total=8 rejects the over-cap add"

        // The next persisted event continues at version 2 — replay count proof.
        let added =
            capped2.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "cap") (Capped.Add 1)
                (function Capped.Added _ -> true | _ -> false)
            |> Async.RunSynchronously

        Expect.equal (added.Version |> ValueLens.Value) 2L "phase 2: replay left the aggregate at version 1, next is v2"
        api2.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let private commandTimeoutTest =
    testCase "facade: a command whose awaited event never arrives raises TimeoutException"
    <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), sprintf "fcqrs_cmdtimeout_%s.db" (Guid.NewGuid().ToString("N")))

        let cfg =
            ConfigurationBuilder()
                .AddInMemoryCollection(
                    [ Collections.Generic.KeyValuePair<string, string | null>(
                          // bare number = SECONDS (same rule as saga-start-timeout)
                          "config:akka:fcqrs:command-timeout", "2") ])
                .Build()

        let api =
            Fcqrs.actor cfg NullLoggerFactory.Instance
                (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite (sprintf "Data Source=%s;" db))) "CmdTimeoutSmoke"

        let counter =
            Fcqrs.aggregate api { Name = "Counter"; Initial = Counter.initial; Decide = Counter.decide; Fold = Counter.fold; Snapshots = Default }

        Fcqrs.wireSagaStarters api []

        // Poke produces Poked; the filter waits for Incremented — never matches.
        // The old behavior hung here forever.
        let sw = Stopwatch.StartNew()

        Expect.throwsT<TimeoutException>
            (fun () ->
                counter.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "poke") Counter.Poke
                    (function Counter.Incremented _ -> true | _ -> false)
                |> Async.RunSynchronously
                |> ignore)
            "the subscription timed out instead of hanging forever"

        sw.Stop()
        Expect.isLessThan sw.Elapsed.TotalSeconds 15.0 "the configured 2s timeout applied (not the 30s default)"
        api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let private concurrencyTest =
    testCase "facade: concurrent commands to one aggregate are serialized with sequential versions"
    <| fun _ ->
        let counter, _subs = boot ()

        let versions =
            [ for _ in 1..30 ->
                counter.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "conc") (Counter.Increment 1)
                    (function Counter.Incremented _ -> true | _ -> false) ]
            |> Async.Parallel
            |> Async.RunSynchronously
            |> Array.map (fun e -> e.Version |> ValueLens.Value)

        Expect.equal (versions |> Array.sort) [| 1L..30L |]
            "30 concurrent commands produced unique, sequential versions 1..30"

let private stopSagaDelayedTest =
    testCase "facade: a delayed command returned with StopSaga is still delivered"
    <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), sprintf "fcqrs_finalping_%s.db" (Guid.NewGuid().ToString("N")))
        registerJournalTypes ()

        let api =
            Fcqrs.actor (ConfigurationBuilder().Build()) NullLoggerFactory.Instance
                (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite (sprintf "Data Source=%s;" db))) "FinalPingSmoke"

        let counter =
            Fcqrs.aggregate api { Name = "Counter"; Initial = Counter.initial; Decide = Counter.decide; Fold = Counter.fold; Snapshots = Default }

        let saga = Fcqrs.saga api (FinalPing.definition counter.Factory)
        Fcqrs.wireSagaStarters api [ saga ]
        let subs = Fcqrs.projection api { LastOffset = 0; Handle = projection }
        use sawReset = subs.Subscribe(isWasReset, 1)

        counter.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "fin") (Counter.Increment 100)
            (function Counter.Incremented _ -> true | _ -> false)
        |> Async.RunSynchronously
        |> ignore

        Expect.isTrue (sawReset.Task.Wait(TimeSpan.FromSeconds 20.0))
            "the saga's final delayed command fired after the saga stopped"

        api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let private timeProviderTest =
    testCase "facade: TimeProvider timestamps are unit-consistent and an infinite due time never fires"
    <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), sprintf "fcqrs_time_%s.db" (Guid.NewGuid().ToString("N")))

        let api =
            Fcqrs.actor (ConfigurationBuilder().Build()) NullLoggerFactory.Instance
                (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite (sprintf "Data Source=%s;" db))) "TimeSmoke"

        Fcqrs.wireSagaStarters api []
        let tp = api.TimeProvider

        // Unit consistency: a real ~150ms sleep must measure in real milliseconds
        // (the old Stopwatch.Frequency mismatch read ~100x too small off Windows).
        let t0 = tp.GetTimestamp()
        Threading.Thread.Sleep 150
        let elapsed = tp.GetElapsedTime(t0)
        Expect.isGreaterThan elapsed.TotalMilliseconds 50.0 "elapsed time is real milliseconds, not ~100x too small"
        Expect.isLessThan elapsed.TotalMilliseconds 5000.0 "elapsed time is sane"

        // BCL semantics: Threading.Timeout.InfiniteTimeSpan means the timer NEVER fires
        // (the old clamp fired it immediately).
        let fired = ref 0

        use _timer =
            tp.CreateTimer(Threading.TimerCallback(fun _ -> incr fired), box (), Threading.Timeout.InfiniteTimeSpan, Threading.Timeout.InfiniteTimeSpan)

        Threading.Thread.Sleep 400
        Expect.equal fired.Value 0 "an infinite due time never fires"

        // A real one-shot fires exactly once.
        use _t2 =
            tp.CreateTimer(Threading.TimerCallback(fun _ -> incr fired), box (), TimeSpan.FromMilliseconds 50.0, Threading.Timeout.InfiniteTimeSpan)

        let mutable attempts = 0

        while fired.Value = 0 && attempts < 40 do
            attempts <- attempts + 1
            Threading.Thread.Sleep 100

        Threading.Thread.Sleep 300 // give a hypothetical second fire a chance
        Expect.equal fired.Value 1 "a one-shot timer fired exactly once"

        // BCL parity: out-of-range due times and periods throw instead of
        // silently clamping (the old clamp fired a 30-day timer ~5 days early,
        // and a negative period hung with no diagnostic).
        Expect.throwsT<ArgumentOutOfRangeException>
            (fun () ->
                tp.CreateTimer(Threading.TimerCallback(fun _ -> ()), box (), TimeSpan.FromMilliseconds -2.0, Threading.Timeout.InfiniteTimeSpan)
                |> ignore)
            "a due time below -1 ms throws"

        Expect.throwsT<ArgumentOutOfRangeException>
            (fun () ->
                tp.CreateTimer(Threading.TimerCallback(fun _ -> ()), box (), TimeSpan.FromMilliseconds 10.0, TimeSpan.FromDays -1.0)
                |> ignore)
            "a period below -1 ms throws"

        Expect.throwsT<ArgumentOutOfRangeException>
            (fun () ->
                tp.CreateTimer(Threading.TimerCallback(fun _ -> ()), box (), TimeSpan.FromDays 50.0, Threading.Timeout.InfiniteTimeSpan)
                |> ignore)
            "a due time beyond the BCL maximum (~49.7 days) throws"

        // Beyond the old Int32 ms clamp (~24.86 days) but within the BCL
        // range: schedules fine and does NOT fire early.
        use _t3 =
            tp.CreateTimer(Threading.TimerCallback(fun _ -> incr fired), box (), TimeSpan.FromDays 30.0, Threading.Timeout.InfiniteTimeSpan)

        Threading.Thread.Sleep 300
        Expect.equal fired.Value 1 "a 30-day timer does not fire early"

        api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

/// DynamicConfig: GetSectionAsDynamic returns the requested section itself
/// (the old off-by-one returned its parent), dense numeric runs become HOCON
/// arrays, sparse numeric keys and scalar+children collisions (legal
/// IConfiguration shapes, typical of env-var overrides) must not crash or
/// corrupt, and a section with no keys yields an empty object.
let private dynamicConfigTest =
    testCase "dynamic config: returns the requested section; dense runs become arrays; missing section is empty"
    <| fun _ ->
        let kvp k v = Collections.Generic.KeyValuePair<string, string | null>(k, v)

        let cfg =
            ConfigurationBuilder()
                .AddInMemoryCollection(
                    [ kvp "config:akka:remote:seed-nodes:0" "akka.tcp://a@127.0.0.1:4053"
                      kvp "config:akka:remote:seed-nodes:1" "akka.tcp://a@127.0.0.1:4054"
                      // sparse numeric key: NOT a dense run — must stay an object
                      // (the old code sized the array by key count and crashed)
                      kvp "config:akka:sparse:1" "x"
                      // one key with BOTH a value and children (env-var style)
                      kvp "config:akka:collision" "scalar"
                      kvp "config:akka:collision:child" "y"
                      kvp "config:akka:persistence:journal:plugin" "akka.persistence.journal.sql" ])
                .Build()

        // The requested section itself: config:akka -> the akka node, so its
        // children sit at the HOCON root. This call crashed on the sparse key
        // before an earlier fix.
        let dyn =
            FCQRS.DynamicConfig.ConfigExtension.GetSectionAsDynamic(cfg, "config:akka")
            :?> System.Dynamic.ExpandoObject

        let hocon = Akka.Configuration.ConfigurationFactory.FromObject dyn

        Expect.equal
            (hocon.GetStringList("remote.seed-nodes") |> List.ofSeq)
            [ "akka.tcp://a@127.0.0.1:4053"; "akka.tcp://a@127.0.0.1:4054" ]
            "a dense 0..n-1 run becomes a HOCON array"

        Expect.equal (hocon.GetString("sparse.1")) "x" "sparse numeric keys stay a plain object"

        // A deeper section returns that section, not an ancestor.
        let persistenceHocon =
            Akka.Configuration.ConfigurationFactory.FromObject(
                FCQRS.DynamicConfig.ConfigExtension.GetSectionAsDynamic(cfg, "config:akka:persistence"))

        Expect.equal
            (persistenceHocon.GetString("journal.plugin"))
            "akka.persistence.journal.sql"
            "config:akka:persistence returns the persistence node"

        // The internal call site (Actor.api) asks for "config": the wrapper
        // node whose akka child becomes the HOCON root.
        let configHocon =
            Akka.Configuration.ConfigurationFactory.FromObject(
                FCQRS.DynamicConfig.ConfigExtension.GetSectionAsDynamic(cfg, "config"))

        Expect.equal
            (configHocon.GetStringList("akka.remote.seed-nodes") |> List.ofSeq)
            [ "akka.tcp://a@127.0.0.1:4053"; "akka.tcp://a@127.0.0.1:4054" ]
            "config returns the wrapper node feeding ConfigurationFactory.FromObject"

        // A section with no keys yields an empty object instead of throwing.
        // Callers (Akka) then fall back to their own defaults.
        let missing =
            FCQRS.DynamicConfig.ConfigExtension.GetSectionAsDynamic(cfg, "config:does-not-exist")
            :?> Collections.Generic.IDictionary<string, obj>

        Expect.equal missing.Count 0 "a missing section returns an empty object, no KeyNotFoundException"

/// A saga whose handleEvent keys ONLY on the event type — it never inspects
/// its current state — and counts every delivery of its starting event. A
/// fresh start that spuriously runs the recovery re-drive makes the originator
/// re-publish the starting event, so this saga would observe it twice; the
/// count makes the duplicate visible where state-keyed sagas mask it.
module CountingSaga =
    type State = Seen

    let deliveries = ref 0

    let private handleEvent (evt: obj) (_: SagaState<_, _>) =
        match evt with
        | :? (Event<Counter.Event>) as e ->
            match e.EventDetails with
            | Counter.Incremented n when n >= 100 ->
                Threading.Interlocked.Increment deliveries |> ignore
                Seen |> StateChangedEvent
            | _ -> UnhandledEvent
        | _ -> UnhandledEvent

    let private applySideEffects (sagaState: SagaState<_, _>) _recovering =
        match sagaState.State with
        // Stay (not StopSaga): the saga must remain alive and subscribed so a
        // duplicate re-published starting event would still reach it.
        | Seen -> Stay, []

    let private startsOn (e: Event<Counter.Event>) =
        match e.EventDetails with
        | Counter.Incremented n -> n >= 100
        | _ -> false

    let definition counterFactory =
        { Name = "CountingSaga"
          InitialData = ()
          Originator = counterFactory
          HandleEvent = handleEvent
          ApplySideEffects = applySideEffects
          StartOn = startsOn
          Snapshots = Default }

/// A saga that completes (StopSaga) immediately after starting, with a
/// snapshot cadence that covers its whole journal (Started=v1, Done=v2 →
/// snapshot at v2). A same-CID re-trigger after a reboot resurrects it through
/// the SNAPSHOT — the recovery path that must still complete the saga-start
/// handshake instead of dropping the re-delivered starting event.
module OneShot =
    type State = Done

    let private handleEvent (evt: obj) (_: SagaState<_, _>) =
        match evt with
        | :? (Event<Counter.Event>) as e ->
            match e.EventDetails with
            | Counter.Incremented n when n >= 100 -> Done |> StateChangedEvent
            | _ -> UnhandledEvent
        | _ -> UnhandledEvent

    let private applySideEffects (sagaState: SagaState<_, _>) _recovering =
        match sagaState.State with
        | Done -> StopSaga, []

    let private startsOn (e: Event<Counter.Event>) =
        match e.EventDetails with
        | Counter.Incremented n -> n >= 100
        | _ -> false

    let definition counterFactory =
        { Name = "OneShot"
          InitialData = ()
          Originator = counterFactory
          HandleEvent = handleEvent
          ApplySideEffects = applySideEffects
          StartOn = startsOn
          Snapshots = Every 2 }

let private freshStartSingleDeliveryTest =
    testCase "facade: a fresh saga start delivers the starting event exactly once"
    <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), sprintf "fcqrs_counting_%s.db" (Guid.NewGuid().ToString("N")))
        registerJournalTypes ()

        let api =
            Fcqrs.actor (ConfigurationBuilder().Build()) NullLoggerFactory.Instance
                (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite (sprintf "Data Source=%s;" db))) "CountingSmoke"

        let counter =
            Fcqrs.aggregate api { Name = "Counter"; Initial = Counter.initial; Decide = Counter.decide; Fold = Counter.fold; Snapshots = Default }

        let saga = Fcqrs.saga api (CountingSaga.definition counter.Factory)
        Fcqrs.wireSagaStarters api [ saga ]
        CountingSaga.deliveries.Value <- 0

        counter.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "count") (Counter.Increment 100)
            (function Counter.Incremented _ -> true | _ -> false)
        |> Async.RunSynchronously
        |> ignore

        let mutable attempts = 0

        while CountingSaga.deliveries.Value = 0 && attempts < 50 do
            attempts <- attempts + 1
            Threading.Thread.Sleep 100

        Expect.equal CountingSaga.deliveries.Value 1 "the saga saw its starting event"

        // A spurious fresh-start re-drive re-publishes the starting event within
        // milliseconds of the handshake; give a would-be duplicate ample time.
        Threading.Thread.Sleep 2000
        Expect.equal CountingSaga.deliveries.Value 1 "no duplicate delivery of the starting event"
        api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let private concurrentSagaStartTest =
    testCase "facade: a saga start racing a concurrent command still completes its workflow"
    <| fun _ ->
        let counter, subs = boot ()
        use sawReset = subs.Subscribe(isWasReset, 1)

        // Before the fix, the fresh start's spurious ContinueOrAbort could lose
        // the mailbox race to the second command, version-mismatch, and abort
        // the saga mid-flight — its Reset then never arrived.
        [ counter.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "race") (Counter.Increment 100)
              (function Counter.Incremented _ -> true | _ -> false)
          counter.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "race") (Counter.Increment 1)
              (function Counter.Incremented _ -> true | _ -> false) ]
        |> Async.Parallel
        |> Async.RunSynchronously
        |> ignore

        Expect.isTrue (sawReset.Task.Wait(TimeSpan.FromSeconds 20.0))
            "the saga completed despite a concurrent command advancing the originator"

let private bootOneShot (db: string) (lmdb: string) =
    registerJournalTypes ()

    let cfg =
        ConfigurationBuilder()
            .AddInMemoryCollection(
                [ Collections.Generic.KeyValuePair<string, string | null>(
                      "config:akka:cluster:distributed-data:durable:lmdb", lmdb)
                  // Short handshake bound so the pre-fix failure mode (FailFast
                  // after the saga-start timeout) surfaces quickly, not at 30s.
                  Collections.Generic.KeyValuePair<string, string | null>(
                      "config:akka:fcqrs:saga-start-timeout", "5") ])
            .Build()

    let api =
        Fcqrs.actor cfg NullLoggerFactory.Instance
            (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite (sprintf "Data Source=%s;" db))) "OneShotSmoke"

    let counter =
        Fcqrs.aggregate api { Name = "Counter"; Initial = Counter.initial; Decide = Counter.decide; Fold = Counter.fold; Snapshots = Default }

    let saga = Fcqrs.saga api (OneShot.definition counter.Factory)
    Fcqrs.wireSagaStarters api [ saga ]
    api, counter

let private snapshotResurrectionTest =
    testCase "facade: a same-CID re-trigger of a snapshot-covered completed saga completes the handshake"
    <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), sprintf "fcqrs_oneshot_%s.db" (Guid.NewGuid().ToString("N")))
        let lmdb = Path.Combine(Path.GetTempPath(), sprintf "fcqrs_oneshot_lmdb_%s" (Guid.NewGuid().ToString("N")))

        // Phase 1: start and complete the saga; its Every-2 cadence snapshots at
        // Done (v2), covering the whole journal including the wrapper at seq 1.
        let api1, counter1 = bootOneShot db lmdb
        let cid = Fcqrs.newCid ()

        counter1.Send cid (Fcqrs.aggregateId "snapres") (Counter.Increment 100)
            (function Counter.Incremented _ -> true | _ -> false)
        |> Async.RunSynchronously
        |> ignore

        // The snapshot must be durable before the reboot, or phase 2 recovers
        // from the journal and the test proves nothing.
        let sagaSnapshots () =
            use conn = new Microsoft.Data.Sqlite.SqliteConnection(sprintf "Data Source=%s;" db)
            conn.Open()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT COUNT(*) FROM snapshot WHERE persistence_id LIKE '%~Saga~%'"
            cmd.ExecuteScalar() :?> int64

        let mutable snapCount = 0L
        let mutable snapAttempts = 0

        while snapCount = 0L && snapAttempts < 40 do
            snapAttempts <- snapAttempts + 1
            Threading.Thread.Sleep 250
            snapCount <- try sagaSnapshots () with _ -> 0L

        Expect.isGreaterThanOrEqual snapCount 1L "phase 1: the saga snapshot is durable"
        api1.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

        // Phase 2: reboot on the same journal and re-trigger with the SAME
        // correlation id: the saga resurrects through its snapshot and must
        // still signal Continue (previously the re-delivered starting event was
        // dropped and the originator FailFasted after the saga-start timeout).
        let api2, counter2 = bootOneShot db lmdb

        let ev =
            counter2.Send cid (Fcqrs.aggregateId "snapres") (Counter.Increment 100)
                (function Counter.Incremented _ -> true | _ -> false)
            |> Async.RunSynchronously

        Expect.equal ev.EventDetails (Counter.Incremented 100) "phase 2: the re-triggered command completed"
        api2.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let private sendAwaitingTimeoutTest =
    testCase "facade: sendAwaiting raises TimeoutException when the projection suppresses the notification"
    <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), sprintf "fcqrs_sat_%s.db" (Guid.NewGuid().ToString("N")))
        registerJournalTypes ()

        let cfg =
            ConfigurationBuilder()
                .AddInMemoryCollection(
                    [ Collections.Generic.KeyValuePair<string, string | null>(
                          "config:akka:fcqrs:command-timeout", "2") ])
                .Build()

        let api =
            Fcqrs.actor cfg NullLoggerFactory.Instance
                (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite (sprintf "Data Source=%s;" db))) "AwaitTimeoutSmoke"

        let counter =
            Fcqrs.aggregate api { Name = "Counter"; Initial = Counter.initial; Decide = Counter.decide; Fold = Counter.fold; Snapshots = Default }

        Fcqrs.wireSagaStarters api []

        // A multi handler that returns [] suppresses every notification — the
        // exact shape whose awaited ack never arrives. Before the bound was
        // added, this hung the caller forever (unrunnable as a failing test).
        let subs = Fcqrs.projection api { LastOffset = 0; Handle = fun _ _ -> [] }

        let sw = Stopwatch.StartNew()

        Expect.throwsT<TimeoutException>
            (fun () ->
                Fcqrs.sendAwaiting subs counter (Fcqrs.newCid ()) (Fcqrs.aggregateId "sat") (Counter.Increment 1)
                    (function Counter.Incremented _ -> true | _ -> false)
                |> Async.RunSynchronously
                |> ignore)
            "the projection wait timed out instead of hanging"

        sw.Stop()
        Expect.isLessThan sw.Elapsed.TotalSeconds 15.0 "the configured 2s bound applied (not the 30s default)"
        api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let private slowSubscriberIsolationTest =
    testCase "facade: a blocking subscriber does not starve a concurrent read-your-writes await"
    <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), sprintf "fcqrs_slow_%s.db" (Guid.NewGuid().ToString("N")))
        registerJournalTypes ()

        // Tiny notification buffer so a pinned BroadcastHub head is reachable
        // with a handful of events instead of thousands.
        let cfg =
            ConfigurationBuilder()
                .AddInMemoryCollection(
                    [ Collections.Generic.KeyValuePair<string, string | null>(
                          "config:akka:fcqrs:notification-buffer", "8") ])
                .Build()

        let api =
            Fcqrs.actor cfg NullLoggerFactory.Instance
                (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite (sprintf "Data Source=%s;" db))) "SlowSubSmoke"

        let counter =
            Fcqrs.aggregate api { Name = "Counter"; Initial = Counter.initial; Decide = Counter.decide; Fold = Counter.fold; Snapshots = Default }

        Fcqrs.wireSagaStarters api []
        let subs = Fcqrs.projection api { LastOffset = 0; Handle = projection }

        // A standing subscriber that blocks hard on its first event. Without
        // per-consumer isolation it pinned the shared hub: every later
        // notification (including the awaiter's) was silently shed.
        use _blocker = subs.Subscribe(fun (_: IMessageWithCID) -> Threading.Thread.Sleep 60000)

        // Enough events to exhaust the 8-element hub/queue slack while the
        // blocker is stuck.
        for _ in 1..30 do
            counter.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "slow") (Counter.Increment 1)
                (function Counter.Incremented _ -> true | _ -> false)
            |> Async.RunSynchronously
            |> ignore

        // The read-your-writes await must still complete.
        let ev =
            Fcqrs.sendAwaiting subs counter (Fcqrs.newCid ()) (Fcqrs.aggregateId "slow") (Counter.Increment 1)
                (function Counter.Incremented _ -> true | _ -> false)
            |> Async.RunSynchronously

        Expect.equal ev.EventDetails (Counter.Incremented 1) "sendAwaiting completed despite the blocked subscriber"
        api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let private specialCharEntityIdTest =
    testCase "facade: entity ids needing actor-name escaping round-trip their events"
    <| fun _ ->
        // The shard names entity actors Uri.EscapeDataString(entityId), so the
        // published topic carries the ESCAPED id ("counter 1" -> "counter%201").
        // The command subscriber escapes identically — that symmetry is what makes
        // these ids work; do not "fix" one side to the raw form.
        let counter, _subs = boot ()

        for id in [ "counter 1"; "user+1" ] do
            let ev =
                counter.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId id) (Counter.Increment 5)
                    (function Counter.Incremented _ -> true | _ -> false)
                |> Async.RunSynchronously

            Expect.equal ev.EventDetails (Counter.Incremented 5) (sprintf "the awaited event arrived for entity id '%s'" id)

let private filterThrowTest =
    testCase "facade: a throwing event filter fails the caller instead of hanging the subscription"
    <| fun _ ->
        // A filter exception escaping the ephemeral subscriber actor restarted it
        // without its Execute message or receive timeout, hanging the caller's ask
        // forever. The fix fails the ask loudly with the original exception.
        let counter, _subs = boot ()

        let mutable caught: exn option = None

        try
            counter.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "filterboom") (Counter.Increment 1)
                (fun _ -> failwith "filter exploded")
            |> Async.RunSynchronously
            |> ignore
        with e ->
            caught <- Some e

        let ex = Expect.wantSome caught "the filter exception reaches the caller instead of hanging"
        Expect.isTrue (ex :? InvalidOperationException) (sprintf "expected InvalidOperationException, got %s" (ex.GetType().Name))
        Expect.equal (Unchecked.nonNull ex.InnerException).Message "filter exploded" "the original filter exception is preserved"

let private hoconConnectionStringTest =
    testCase "facade: a connection string with a backslash or quote survives HOCON injection"
    <| fun _ ->
        // Windows paths, SqlServer named instances (localhost\SQLEXPRESS) and
        // passwords with quotes are legitimate connection strings; injected
        // unescaped into default.hocon they broke the HOCON tokenizer at startup.
        registerJournalTypes ()

        let db =
            Path.Combine(Path.GetTempPath(), sprintf "fcqrs_bs_%s" (Guid.NewGuid().ToString("N")))
            |> fun p -> p + "\\nested\"quoted.db"

        let api =
            Fcqrs.actor (ConfigurationBuilder().Build()) NullLoggerFactory.Instance
                (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite (sprintf "Data Source=%s;" db))) "HoconSmoke"

        api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore
        try File.Delete db with _ -> ()

let private crossTypeHandshakeTest =
    testCase "facade: concurrent saga starts from two aggregate types sharing an entity id both complete"
    <| fun _ ->
        // The handshake used to key batches by bare entity id, so the two
        // handshakes below collided: the second overwrote the first's reply-to,
        // and the first timed out and FailFasted the process.
        let counterA, counterB = bootDual ()

        let results =
            [ async {
                let! ev =
                    counterA.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "shared") (Counter.Increment 100)
                        (function Counter.Incremented _ -> true | _ -> false)

                return ev.EventDetails :> obj
              }
              async {
                let! ev =
                    counterB.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "shared") (CounterB.Increment 100)
                        (function CounterB.Incremented _ -> true)

                return ev.EventDetails :> obj
              } ]
            |> Async.Parallel
            |> Async.RunSynchronously

        Expect.equal results.[0] (box (Counter.Incremented 100)) "the Counter handshake completed"
        Expect.equal results.[1] (box (CounterB.Incremented 100)) "the CounterB handshake completed"

/// Boot a Counter with an Every-2 snapshot cadence, for the defer/snapshot test.
let private bootDeferSnap (db: string) =
    registerJournalTypes ()
    let api =
        Fcqrs.actor (ConfigurationBuilder().Build()) NullLoggerFactory.Instance
            (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite (sprintf "Data Source=%s;" db))) "DeferSnapSmoke"
    let counter =
        Fcqrs.aggregate api { Name = "Counter"; Initial = Counter.initial; Decide = Counter.decide; Fold = Counter.fold; Snapshots = Every 2 }
    Fcqrs.wireSagaStarters api []
    api, counter

let private deferSnapshotTest =
    testCase "facade: a DeferEvent fold never leaks into a snapshot"
    <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), sprintf "fcqrs_defersnap_%s.db" (Guid.NewGuid().ToString("N")))

        // Phase 1: Freebie 7 is DEFERRED (in-memory Total=7, nothing journaled),
        // then two increments persist v1 and v2; the Every-2 cadence snapshots at
        // v2. Journal-only Total=10; the defer-polluted behavior state is 17.
        let api1, counter1 = bootDeferSnap db

        counter1.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "defer") (Counter.Freebie 7)
            (function Counter.Freebied _ -> true | _ -> false)
        |> Async.RunSynchronously
        |> ignore

        counter1.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "defer") (Counter.Increment 5)
            (function Counter.Incremented _ -> true | _ -> false)
        |> Async.RunSynchronously
        |> ignore

        counter1.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "defer") (Counter.Increment 5)
            (function Counter.Incremented _ -> true | _ -> false)
        |> Async.RunSynchronously
        |> ignore

        // The snapshot must be durable before the reboot, or phase 2 recovers
        // from the journal alone and the test proves nothing.
        let snapshotCount () =
            use conn = new Microsoft.Data.Sqlite.SqliteConnection(sprintf "Data Source=%s;" db)
            conn.Open()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT COUNT(*) FROM snapshot WHERE persistence_id LIKE '%defer%'"
            cmd.ExecuteScalar() :?> int64

        let mutable snaps = 0L
        let mutable attempts = 0

        while snaps = 0L && attempts < 40 do
            attempts <- attempts + 1
            Threading.Thread.Sleep 250
            snaps <- try snapshotCount () with _ -> 0L

        Expect.isGreaterThanOrEqual snaps 1L "phase 1: the v2 snapshot is durable"
        api1.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

        // Phase 2: reboot on the same journal and read the recovered state via a
        // deferred Report. The deferred +7 must be gone: recovery from a snapshot
        // must match recovery from the journal (Total=10), not the polluted
        // behavior state that used to be snapshotted (17).
        let api2, counter2 = bootDeferSnap db

        let ev =
            counter2.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "defer") Counter.Report
                (function Counter.Reported _ -> true | _ -> false)
            |> Async.RunSynchronously

        Expect.equal ev.EventDetails (Counter.Reported 10) "recovered state is the journal-only Total; the deferred fold disappeared"
        api2.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let private cidSeparatorTest =
    testCase "facade: a CID containing the correlation separator is rejected"
    <| fun _ ->
        // A CID with "~" breaks the topic/entity-name correlation parsing
        // (toRawGuid/toCid) and leaves sagas permanently deaf; CreateCID now
        // fails fast instead.
        let mutable caught: exn option = None

        try
            FCQRS.CSharp.Values.CreateCID "tenant~abc" |> ignore
        with e ->
            caught <- Some e

        let ex = Expect.wantSome caught "CreateCID rejects a CID containing '~'"
        Expect.isTrue (ex :? ArgumentException) (sprintf "expected ArgumentException, got %s" (ex.GetType().Name))

/// Saga exercising StayExpecting. Waiting expects the counter's WasReset while
/// re-sending an Increment 1 marker (observable in the journal as the retry
/// count); exhaustion escalates to Failed, whose side effect resets the counter
/// (the compensation the tests observe). Saga names are per-test so sequential
/// tests never share a journal identity.
module Expecting =
    type State =
        | Waiting
        | Failed
        | Finished

    let private handleEvent (evt: obj) (sagaState: SagaState<_, _>) =
        match evt, sagaState.State with
        | :? (Event<Counter.Event>) as e, None ->
            match e.EventDetails with
            | Counter.Incremented n when n >= 100 -> Waiting |> StateChangedEvent
            | _ -> UnhandledEvent
        | :? ExpectationExhausted, Some Waiting -> Failed |> StateChangedEvent
        | :? (Event<Counter.Event>) as e, Some Waiting ->
            match e.EventDetails with
            | Counter.WasReset -> Finished |> StateChangedEvent
            | _ -> UnhandledEvent
        | :? (Event<Counter.Event>) as e, Some Failed ->
            match e.EventDetails with
            | Counter.WasReset -> Finished |> StateChangedEvent
            | _ -> UnhandledEvent
        | _ -> UnhandledEvent

    let private applySideEffects deadline retry counterFactory (sagaState: SagaState<_, _>) _recovering =
        match sagaState.State with
        | Waiting -> expecting deadline retry [ toOriginator counterFactory (Counter.Increment 1 :> obj) ], []
        | Failed -> Stay, [ toOriginator counterFactory (Counter.Reset :> obj) ]
        | Finished -> StopSaga, []

    let private startsOn (e: Event<Counter.Event>) =
        match e.EventDetails with
        | Counter.Incremented n -> n >= 100
        | _ -> false

    let definition name deadline retry counterFactory =
        { Name = name
          InitialData = ()
          Originator = counterFactory
          HandleEvent = handleEvent
          ApplySideEffects = applySideEffects deadline retry counterFactory
          StartOn = startsOn
          Snapshots = Default }

/// Saga that refuses to handle exhaustion: the first delivery throws, later
/// ones are recorded and left unhandled — proving the framework downgrades the
/// failure instead of fatally killing the process, and re-delivers.
module ExpectIgnored =
    type State = Waiting

    let deliveries = ref 0

    let private handleEvent (evt: obj) (sagaState: SagaState<_, _>) =
        match evt, sagaState.State with
        | :? (Event<Counter.Event>) as e, None ->
            match e.EventDetails with
            | Counter.Incremented n when n >= 100 -> Waiting |> StateChangedEvent
            | _ -> UnhandledEvent
        | :? ExpectationExhausted, Some Waiting ->
            incr deliveries

            if deliveries.Value = 1 then
                failwith "domain forgot the exhaustion case"

            UnhandledEvent
        | _ -> UnhandledEvent

    let private applySideEffects counterFactory (sagaState: SagaState<_, _>) _recovering =
        match sagaState.State with
        | Waiting ->
            expecting (TimeSpan.FromMilliseconds 600.0) (FixedInterval(TimeSpan.FromSeconds 30.0)) [
                toOriginator counterFactory (Counter.Poke :> obj)
            ],
            []

    let private startsOn (e: Event<Counter.Event>) =
        match e.EventDetails with
        | Counter.Incremented n -> n >= 100
        | _ -> false

    let definition counterFactory =
        { Name = "ExpectIgnored"
          InitialData = ()
          Originator = counterFactory
          HandleEvent = handleEvent
          ApplySideEffects = applySideEffects counterFactory
          StartOn = startsOn
          Snapshots = Default }

let private bootExpecting (systemName: string) (sagaName: string) deadline retry (db: string) (lmdb: string option) =
    registerJournalTypes ()

    let cfg =
        match lmdb with
        | Some l ->
            ConfigurationBuilder()
                .AddInMemoryCollection(
                    [ Collections.Generic.KeyValuePair<string, string | null>(
                          "config:akka:cluster:distributed-data:durable:lmdb", l) ])
                .Build()
        | None -> ConfigurationBuilder().Build()

    let api =
        Fcqrs.actor cfg NullLoggerFactory.Instance
            (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite (sprintf "Data Source=%s;" db))) systemName

    let counter =
        Fcqrs.aggregate api { Name = "Counter"; Initial = Counter.initial; Decide = Counter.decide; Fold = Counter.fold; Snapshots = Default }

    let saga = Fcqrs.saga api (Expecting.definition sagaName deadline retry counter.Factory)
    Fcqrs.wireSagaStarters api [ saga ]
    api, counter

/// Projection counting the expectation's Increment 1 markers while forwarding
/// every counter event for read-your-writes subscriptions.
let private markerProjection (markers: int ref) =
    { LastOffset = 0
      Handle =
        fun (_offset: int64) (ev: obj) ->
            match ev with
            | :? (Event<Counter.Event>) as e ->
                (match e.EventDetails with
                 | Counter.Incremented 1 -> incr markers
                 | _ -> ())

                [ e :> IMessageWithCID ]
            | _ -> [] }

let private expectationSatisfiedTest =
    testCase "expectation: a reply before the deadline stops the retry chain"
    <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), sprintf "fcqrs_expsat_%s.db" (Guid.NewGuid().ToString("N")))

        let api, counter =
            bootExpecting "ExpectSatSmoke" "ExpectSat" (TimeSpan.FromSeconds 20.0)
                (FixedInterval(TimeSpan.FromMilliseconds 400.0)) db None

        let markers = ref 0
        let subs = Fcqrs.projection api (markerProjection markers)
        let cid = Fcqrs.newCid ()

        counter.Send cid (Fcqrs.aggregateId "sat1") (Counter.Increment 100)
            (function Counter.Incremented _ -> true | _ -> false)
        |> Async.RunSynchronously
        |> ignore

        // The entry marker proves the saga is IN Waiting (its expectation sent
        // Increment 1) before the test satisfies it from outside.
        let mutable waits = 0
        while markers.Value < 1 && waits < 40 do
            waits <- waits + 1
            Threading.Thread.Sleep 250

        Expect.isGreaterThanOrEqual markers.Value 1 "the expectation's Resend was sent on state entry"

        // Same CID: the saga's subscription is keyed by its starting correlation
        // id, so the satisfying WasReset must carry it.
        counter.Send cid (Fcqrs.aggregateId "sat1") Counter.Reset
            (function Counter.WasReset -> true | _ -> false)
        |> Async.RunSynchronously
        |> ignore

        // Give the transition a moment to land, then require marker stability
        // across three retry intervals: a live reminder chain would keep adding.
        Threading.Thread.Sleep 1000
        let afterSatisfied = markers.Value
        Threading.Thread.Sleep 1300
        Expect.equal markers.Value afterSatisfied "no re-send after the expectation was satisfied"
        ignore subs
        api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let private expectationExhaustionTest =
    testCase "expectation: retries re-send only the declared commands, then exhaustion escalates to the domain's Failed state"
    <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), sprintf "fcqrs_expexh_%s.db" (Guid.NewGuid().ToString("N")))

        let api, counter =
            bootExpecting "ExpectExhSmoke" "ExpectExh" (TimeSpan.FromMilliseconds 1300.0)
                (FixedInterval(TimeSpan.FromMilliseconds 300.0)) db None

        let markers = ref 0
        let subs = Fcqrs.projection api (markerProjection markers)
        use sawReset = subs.Subscribe(isWasReset, 1)

        counter.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "exh1") (Counter.Increment 100)
            (function Counter.Incremented _ -> true | _ -> false)
        |> Async.RunSynchronously
        |> ignore

        // Nobody replies: the saga must retry, exhaust, transition to Failed,
        // and Failed's side effect resets the counter (the compensation).
        Expect.isTrue (sawReset.Task.Wait(TimeSpan.FromSeconds 25.0))
            "exhaustion escalated to Failed, whose compensation reset the counter"

        Expect.isGreaterThanOrEqual markers.Value 2
            "the expectation re-sent its command at least once beyond state entry"

        api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let private expectationUnhandledTest =
    testCase "expectation: a thrown or unhandled exhaustion is downgraded, re-delivered, and never kills the process"
    <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), sprintf "fcqrs_expign_%s.db" (Guid.NewGuid().ToString("N")))
        registerJournalTypes ()

        let api =
            Fcqrs.actor (ConfigurationBuilder().Build()) NullLoggerFactory.Instance
                (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite (sprintf "Data Source=%s;" db))) "ExpectIgnSmoke"

        let counter =
            Fcqrs.aggregate api { Name = "Counter"; Initial = Counter.initial; Decide = Counter.decide; Fold = Counter.fold; Snapshots = Default }

        let saga = Fcqrs.saga api (ExpectIgnored.definition counter.Factory)
        Fcqrs.wireSagaStarters api [ saga ]

        ExpectIgnored.deliveries.Value <- 0

        counter.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "ign1") (Counter.Increment 100)
            (function Counter.Incremented _ -> true | _ -> false)
        |> Async.RunSynchronously
        |> ignore

        // First delivery (~0.6s) throws inside handleEvent; the framework must
        // downgrade it and re-deliver one deadline period later.
        let mutable waits = 0
        while ExpectIgnored.deliveries.Value < 2 && waits < 60 do
            waits <- waits + 1
            Threading.Thread.Sleep 250

        Expect.isGreaterThanOrEqual ExpectIgnored.deliveries.Value 2
            "ExpectationExhausted was re-delivered after being thrown/unhandled"

        // And the process (this test run) is demonstrably still alive.
        let ev =
            counter.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "alive") (Counter.Increment 3)
                (function Counter.Incremented _ -> true | _ -> false)
            |> Async.RunSynchronously

        Expect.equal ev.EventDetails (Counter.Incremented 3) "the system still processes commands"
        api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let private expectationRestartTest =
    testCase "expectation: a restart mid-wait keeps the deadline anchored to the persisted entry time"
    <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), sprintf "fcqrs_expreb_%s.db" (Guid.NewGuid().ToString("N")))
        let lmdb = Path.Combine(Path.GetTempPath(), sprintf "fcqrs_expreb_lmdb_%s" (Guid.NewGuid().ToString("N")))
        let deadline = TimeSpan.FromMilliseconds 1500.0

        // Phase 1: enter Waiting, prove it is journaled, then kill the system
        // while the expectation is pending.
        let api1, counter1 =
            bootExpecting "ExpectRebootSmoke" "ExpectReboot" deadline (FixedInterval(TimeSpan.FromMilliseconds 500.0)) db (Some lmdb)

        counter1.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "reb1") (Counter.Increment 100)
            (function Counter.Incremented _ -> true | _ -> false)
        |> Async.RunSynchronously
        |> ignore

        let sagaRows () =
            use conn = new Microsoft.Data.Sqlite.SqliteConnection(sprintf "Data Source=%s;" db)
            conn.Open()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT COUNT(*) FROM journal WHERE persistence_id LIKE '%~Saga~%'"
            cmd.ExecuteScalar() :?> int64

        let mutable attempts = 0
        while (try sagaRows () with _ -> 0L) < 3L && attempts < 40 do
            attempts <- attempts + 1
            Threading.Thread.Sleep 250

        Expect.isGreaterThanOrEqual (sagaRows ()) 3L "phase 1: Waiting (and its entry time) is journaled"
        api1.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

        // Phase 2: reboot on the same journal. The deadline anchor is the
        // journaled entry time — by now long past — so the resurrected saga must
        // exhaust promptly, escalate to Failed, and drive the compensation Reset.
        let api2, _ =
            bootExpecting "ExpectRebootSmoke" "ExpectReboot" deadline (FixedInterval(TimeSpan.FromMilliseconds 500.0)) db (Some lmdb)

        let mutable seen = false
        let mutable tries = 0

        while not seen && tries < 15 do
            tries <- tries + 1
            let subs = Fcqrs.projection api2 { LastOffset = 0; Handle = projection }
            use awaiter = subs.Subscribe(isWasReset, 1)
            seen <- awaiter.Task.Wait(TimeSpan.FromSeconds 2.0)

        Expect.isTrue seen "phase 2: the recovered expectation exhausted from its persisted entry time and compensated"
        api2.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let tests =
    testSequenced (
        testList "facade" [ manifestTest; roundTripTest; persistAllTest; manualSnapshotTest; telemetryTest; payloadSwitchTest; overflowTest; snapshotRecoveryTest; restartDetectionTest; filteredProjectionTest; persistIfTest; bridgeTest; pendingBridgeTest; focusShapeTest; journaledStampTest; runAsyncTest; aggregateRecoveryTest; commandTimeoutTest; concurrencyTest; stopSagaDelayedTest; timeProviderTest; dynamicConfigTest; freshStartSingleDeliveryTest; concurrentSagaStartTest; snapshotResurrectionTest; sendAwaitingTimeoutTest; slowSubscriberIsolationTest; specialCharEntityIdTest; filterThrowTest; hoconConnectionStringTest; crossTypeHandshakeTest; deferSnapshotTest; cidSeparatorTest; expectationSatisfiedTest; expectationExhaustionTest; expectationUnhandledTest; expectationRestartTest ]
    )

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv tests
