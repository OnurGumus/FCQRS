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
    Fcqrs.journalTypes [ journalType<Counter.Event> "counter.event" ]

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

        Expect.equal (publishAll 0L (box msg)) [ msg ] "Publish forwards the event when it carries a CID"
        Expect.isEmpty (publishAll 0L (box 42)) "Publish on a non-CID event notifies nothing"
        Expect.isEmpty (suppressAll 0L (box msg)) "Suppress notifies nothing even for a CID-bearing event"

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
/// own assembly instead of the one it is handed.
let private bridgeTest =
    FCQRS.ExpectoTickSpec.FeatureTest.createTest (Reflection.Assembly.GetExecutingAssembly()) "Facade" "bridge"

let tests =
    testSequenced (
        testList "facade" [ manifestTest; roundTripTest; persistAllTest; manualSnapshotTest; telemetryTest; payloadSwitchTest; overflowTest; snapshotRecoveryTest; restartDetectionTest; filteredProjectionTest; persistIfTest; bridgeTest ]
    )

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv tests
