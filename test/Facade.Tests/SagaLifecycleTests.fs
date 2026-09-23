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
        Fcqrs.actor (ConfigurationBuilder().Build()) NullLoggerFactory.Instance
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

type DoorCommand =
    | Open
    | Knock

type DoorEvent =
    | Opened
    | Knocked

type DoorWatchState = Watching

let private bootDoors (db: string) (lmdb: string) (recovered: ManualResetEventSlim) =
    let configuration =
        ConfigurationBuilder()
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
    api, doors

let private recoveredReadiness =
    testCase "saga lifecycle: a recovered saga still answers a repeated start after expectation retries"
    <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), $"fcqrs_saga_readiness_{Guid.NewGuid():N}.db")
        let lmdb = Path.Combine(Path.GetTempPath(), $"fcqrs_saga_readiness_lmdb_{Guid.NewGuid():N}")
        let cid = Fcqrs.newCid ()
        let door = Fcqrs.aggregateId "front"
        let send (doors: AggregateHandle<DoorCommand, DoorEvent>) command =
            Async.RunSynchronously(doors.Send cid door command (fun _ -> true), 30000)

        use firstRecovery = new ManualResetEventSlim(false)
        let api1, doors1 = bootDoors db lmdb firstRecovery
        try
            send doors1 Open |> ignore
            // Rows: the starting event, Started, then Watching. The retries need Watching.
            let deadline = DateTime.UtcNow.AddSeconds 10.0
            while journalCount db "DoorWatch/%" < 3L && DateTime.UtcNow < deadline do
                Thread.Sleep 50
            Expect.isGreaterThanOrEqual (journalCount db "DoorWatch/%") 3L "the saga journaled its start and Watching"
        finally
            api1.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

        // Remember-entities recovers the saga in Watching; its subscription is acknowledged afterwards.
        use recovered = new ManualResetEventSlim(false)
        let api2, doors2 = bootDoors db lmdb recovered
        try
            Expect.isTrue (recovered.Wait(TimeSpan.FromSeconds 20.0)) "the saga recovered and resubscribed"
            // Let several expectation retry ticks run after the resubscription.
            Thread.Sleep 1500
            // The same correlation ID starts the same saga again; it must confirm readiness.
            let repeated = send doors2 Open
            Expect.equal repeated.Journaled (Some true) "the repeated start completed its handshake and was journaled"
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

let private continueOrAbortIdentity =
    testCase "saga lifecycle: recovery continues only on the event journaled at that version"
    <| fun _ ->
        withSystem "ContinueOrAbortIdentity" <| fun api _ ->
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

            // The recovery check a saga sends its originator, built as the saga runtime builds it.
            let assembly = typeof<IActor>.Assembly
            let continueOrAbort =
                match assembly.GetType("FCQRS.Common+ContinueOrAbort`1") with
                | null -> failwith "ContinueOrAbort was not found."
                | definition -> definition.MakeGenericType [| typeof<CheckEvent> |]
            let flags = Reflection.BindingFlags.Public ||| Reflection.BindingFlags.NonPublic
            let case = Reflection.FSharpType.GetUnionCases(continueOrAbort, flags) |> Array.exactlyOne
            let recoveryCheck (startingEvent: Event<CheckEvent>) =
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

            // Replies reach the asker only when its name marks it as a saga of this originator.
            let replies = Collections.Concurrent.BlockingCollection<obj>()
            let saga =
                Akkling.Spawn.spawn api.System $"check~Saga~{cid}" (Akkling.Props.props (fun (mailbox: Akkling.Actors.Actor<obj>) ->
                    let rec receive () =
                        Akkling.ComputationExpressions.actor {
                            let! message = mailbox.Receive()
                            replies.Add message
                            return! receive ()
                        }
                    receive ()))
                |> Akkling.ActorRefs.untyped
            let entity = checks.Factory "check"
            let aborted () =
                let mutable reply: obj = null
                if replies.TryTake(&reply, TimeSpan.FromSeconds 3.0) then
                    let replyType = reply.GetType()
                    replyType.IsGenericType && replyType.GetGenericArguments().[0].Name = "AbortedEvent"
                else
                    false

            entity.Tell(recoveryCheck journaled, saga)
            Expect.isFalse (aborted ()) "the event journaled at that version lets the saga continue"
            let otherEvent = { journaled with Id = Guid.CreateVersion7().ToString() |> ValueLens.CreateAsResult |> Result.value }
            entity.Tell(recoveryCheck otherEvent, saga)
            Expect.isTrue (aborted ()) "a different event at the same version aborts the saga"

type EarlyCommand = Early
type EarlyEvent = Arrived

let private starterWiredLate =
    testCase "saga lifecycle: a save before the saga starter is wired waits for it"
    <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), $"fcqrs_saga_lifecycle_{Guid.NewGuid():N}.db")
        let configuration =
            ConfigurationBuilder()
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
        ConfigurationBuilder()
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

let private bootGates (db: string) (lmdb: string) (loggerFactory: Microsoft.Extensions.Logging.ILoggerFactory) =
    let configuration =
        ConfigurationBuilder()
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
              // The saga never leaves the framework's Started state.
              HandleEvent = fun _ _ -> UnhandledEvent
              ApplySideEffects = fun (_: SagaState<unit, GateSagaState>) _ -> Stay, []
              StartOn = fun (event: Event<GateEvent>) -> event.EventDetails = GateOpened
              Snapshots = NoSnapshots }
    Fcqrs.wireSagaStarters api [ saga ]
    api, gates

let private abortBeforeStarted =
    testCase "saga lifecycle: a saga recovered before Started honours its originator's abort"
    <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), $"fcqrs_saga_gate_{Guid.NewGuid():N}.db")
        let lmdb = Path.Combine(Path.GetTempPath(), $"fcqrs_saga_gate_lmdb_{Guid.NewGuid():N}")
        let gate = Fcqrs.aggregateId "front"
        let sagaRows () = journalCount db "GateSaga/%"
        // GateOpened (version 1) starts the saga, which journals its starting event and Started.
        // Bumped (version 2) moves the originator past the starting event.
        let api1, gates1 = bootGates db lmdb NullLoggerFactory.Instance
        try
            Async.RunSynchronously(gates1.Send (Fcqrs.newCid ()) gate OpenGate (fun _ -> true), 20000) |> ignore
            let deadline = DateTime.UtcNow.AddSeconds 10.0
            while sagaRows () < 2L && DateTime.UtcNow < deadline do
                Thread.Sleep 50
            Async.RunSynchronously(gates1.Send (Fcqrs.newCid ()) gate Bump (fun _ -> true), 20000) |> ignore
        finally
            api1.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore
        Expect.equal (sagaRows ()) 2L "the saga journaled its starting event and Started"
        // A stop between the two saga writes leaves only the starting event.
        (use connection = new SqliteConnection($"Data Source={db};")
         connection.Open()
         use command = connection.CreateCommand()
         command.CommandText <- "DELETE FROM journal WHERE persistence_id LIKE 'GateSaga/%' AND sequence_number = 2"
         command.ExecuteNonQuery() |> ignore)
        Expect.equal (sagaRows ()) 1L "only the starting event remains"
        // Remember-entities recovers the saga before Started; the originator is at version 2,
        // so it answers the saga's recovery check with an abort.
        let logs = Collections.Concurrent.ConcurrentQueue<string>()
        let api2, _ = bootGates db lmdb (new CapturingLoggerFactory(logs))
        try
            let aborted () = logs.ToArray() |> Array.exists (fun line -> line = "GateSaga | Aborting")
            let deadline = DateTime.UtcNow.AddSeconds 25.0
            while not (aborted ()) && DateTime.UtcNow < deadline do
                Thread.Sleep 100
            Expect.isTrue (aborted ()) "the saga ends when its starting event was never the originator's latest"
        finally
            api2.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let tests =
    testSequenced (
        testList
            "saga lifecycle"
            [ deferredVerdict
              manifestsWithoutVersions
              recoveredReadiness
              stopDuringSave
              continueOrAbortIdentity
              starterWiredLate
              customSagaNames
              initialStateDeadline
              abortBeforeStarted ]
    )
