module EventUpcastingTests

open System
open System.Collections.Concurrent
open System.Data.Common
open System.IO
open System.Reflection
open System.Runtime.ExceptionServices
open System.Threading.Tasks
open Akka.Actor
open Akka.Persistence.Query
open Expecto
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging.Abstractions
open FCQRS
open FCQRS.Common
open FCQRS.FSharp
open FCQRS.Model.Data
open FCQRS.ProjectionStorage
open FCQRS.Projections

type FirstEvent = { Amount: int }
type SecondEvent = { Amount: int; Currency: string }
type CurrentEvent = { MinorAmount: int; Currency: string }
type UnrelatedEvent = { Description: string }
type CounterCommand = Add of int | Read
type WorkflowState = Waiting of int
type HistoricalUnion = Credited of int | Debited of int

let private deadline = TimeSpan.FromSeconds 20.0
let private wait (work: Task) = work.WaitAsync(deadline).GetAwaiter().GetResult()
let private result (work: Task<'T>) = work.WaitAsync(deadline).GetAwaiter().GetResult()
let private signal<'T> () = TaskCompletionSource<'T>(TaskCreationOptions.RunContinuationsAsynchronously)
let private boxed value : obj = box value |> Unchecked.nonNull
let private firstToSecond (event: FirstEvent) : SecondEvent = { Amount = event.Amount; Currency = "EUR" }
let private secondToCurrent (event: SecondEvent) : CurrentEvent = { MinorAmount = event.Amount * 100; Currency = event.Currency }
let private register (api: IActor) =
    // Register in reverse order to prove chains do not depend on startup order.
    Fcqrs.withEventUpcaster<SecondEvent, CurrentEvent> api secondToCurrent |> ignore
    Fcqrs.withEventUpcaster<FirstEvent, SecondEvent> api firstToSecond |> ignore

let private registerNames () =
    Fcqrs.journalTypes
        [ typeof<FirstEvent>, "upcasting.test.first"
          typeof<SecondEvent>, "upcasting.test.second"
          typeof<CurrentEvent>, "upcasting.test.current" ]

let private convert (api: IActor) value =
    // Exercise the nonfatal conversion boundary without placing malformed
    // application code inside the aggregate's intentional fail-fast policy.
    let method =
        typeof<IActor>.Assembly.GetTypes()
        |> Array.filter (fun typ -> (string typ.FullName).Contains "EventUpcasting")
        |> Array.collect (fun typ -> typ.GetMethods(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static))
        |> Array.find (fun method -> method.Name = "upcastEvent" && method.GetParameters().Length = 2)
    try method.Invoke(null, [| boxed api.System; value |]) |> Unchecked.nonNull
    with :? TargetInvocationException as error when not (isNull error.InnerException) ->
        ExceptionDispatchInfo.Capture(error.InnerException |> Unchecked.nonNull).Throw()
        failwith "unreachable"

type private Database() =
    let suffix = Guid.NewGuid().ToString("N")
    let journal = Path.Combine(Path.GetTempPath(), $"fcqrs-upcasting-{suffix}.db")
    let readModel = Path.Combine(Path.GetTempPath(), $"fcqrs-upcasting-read-{suffix}.db")
    let lmdb = Path.Combine(Path.GetTempPath(), $"fcqrs-upcasting-lmdb-{suffix}")
    member _.Journal = journal
    member _.ReadModel = readModel
    member _.ConnectionString = $"Data Source={journal};Pooling=False"
    member _.Open() =
        let connection = new SqliteConnection($"Data Source={journal};Pooling=False")
        connection.Open()
        connection
    member this.Scalar(sql: string) =
        use connection = this.Open()
        use command = connection.CreateCommand()
        command.CommandText <- sql
        Convert.ToInt64(command.ExecuteScalar())
    member this.LegacyFirstManifest() =
        use connection = this.Open()
        use command = connection.CreateCommand()
        command.CommandText <- "UPDATE journal SET manifest = @manifest WHERE ordering = (SELECT MIN(ordering) FROM journal)"
        command.Parameters.AddWithValue("@manifest", typeof<Event<FirstEvent>>.AssemblyQualifiedName) |> ignore
        Expect.equal (command.ExecuteNonQuery()) 1 "one historical row uses the original CLR manifest"
    member this.JournalBytes() =
        use connection = this.Open()
        use command = connection.CreateCommand()
        command.CommandText <- "SELECT message FROM journal ORDER BY ordering"
        use reader = command.ExecuteReader()
        [| while reader.Read() do yield reader.GetFieldValue<byte[]>(0) |]
    member _.Start(?upcasters: bool, ?sameCluster: bool) =
        let configuration =
            VerifySerialization.configuration()
                .AddInMemoryCollection(
                    [ Collections.Generic.KeyValuePair<string, string | null>("config:akka:cluster:distributed-data:durable:lmdb", lmdb) ])
                .Build()
        let api =
            Fcqrs.actor configuration NullLoggerFactory.Instance
                (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={journal};Pooling=False"))
                ("Upcasting" + if defaultArg sameCluster false then suffix else Guid.NewGuid().ToString("N"))
        if defaultArg upcasters false then register api
        api
    interface IDisposable with
        member _.Dispose() =
            for database in [ journal; readModel ] do
                for path in [ database; database + "-wal"; database + "-shm" ] do
                    if File.Exists path then File.Delete path
            if Directory.Exists lmdb then Directory.Delete(lmdb, true)

let private firstAggregate api (folded: ConcurrentQueue<Event<FirstEvent>>) =
    Fcqrs.aggregate api
        { Name = "UpcastCounter"
          Initial = 0
          Decide = fun (command: Command<CounterCommand>) state ->
              match command.CommandDetails with
              | Add amount -> PersistEvent({ Amount = amount }: FirstEvent)
              | Read -> DeferEvent({ Amount = state }: FirstEvent)
          Fold = fun (event: Event<FirstEvent>) state ->
              folded.Enqueue event
              state + event.EventDetails.Amount
          Snapshots = NoSnapshots
          Passivation = PassivationPolicy.Never }

let private currentAggregate api (folded: ConcurrentQueue<Event<CurrentEvent>>) =
    Fcqrs.aggregate api
        { Name = "UpcastCounter"
          Initial = 0
          Decide = fun (command: Command<CounterCommand>) state ->
              match command.CommandDetails with
              | Add amount -> PersistEvent { MinorAmount = amount; Currency = "EUR" }
              | Read ->
                  PublishEvent
                      { EventDetails = { MinorAmount = state; Currency = "EUR" }
                        CreationDate = command.CreationDate; Id = command.Id; Sender = None
                        CorrelationId = command.CorrelationId; Version = Version.Zero; Metadata = command.Metadata }
          Fold = fun (event: Event<CurrentEvent>) state ->
              folded.Enqueue event
              state + event.EventDetails.MinorAmount
          Snapshots = NoSnapshots
          Passivation = PassivationPolicy.Never }

let private send (handle: AggregateHandle<CounterCommand, 'Event>) cid command =
    handle.Send cid (Fcqrs.aggregateId "account") command (fun _ -> true)
    |> fun work -> Async.RunSynchronously(work, int deadline.TotalMilliseconds)

let private seed (database: Database) =
    registerNames ()
    let api = database.Start()
    try
        let folded = ConcurrentQueue<Event<FirstEvent>>()
        let aggregate = firstAggregate api folded
        Fcqrs.wireSagaStarters api []
        send aggregate (Fcqrs.newCid()) (Add 2) |> ignore
        send aggregate (Fcqrs.newCid()) (Add 3) |> ignore
        folded.ToArray() |> Array.distinctBy (fun event -> event.Version)
    finally wait (api.Stop())

let private envelope () : Event<FirstEvent> =
    { EventDetails = { Amount = 7 }
      CreationDate = DateTime(2026, 9, 13, 11, 22, 33, DateTimeKind.Utc)
      Id = "01a08a65-a88d-73a1-87d0-ca508699d505" |> ValueLens.CreateAsResult |> Result.value
      Sender = Some(Fcqrs.aggregateId "original sender")
      CorrelationId = Fcqrs.cid "original-correlation"
      Version = 11L |> ValueLens.TryCreate |> Result.value
      Metadata = Map.ofList [ "tenant", "north"; "traceparent", "unchanged" ] }

let private sameEnvelope (original: Event<'Old>) (current: Event<'New>) =
    Expect.equal current.Id original.Id "message identity is preserved"
    Expect.equal current.CorrelationId original.CorrelationId "correlation is preserved"
    Expect.equal current.Sender original.Sender "sender is preserved"
    Expect.equal current.Version original.Version "persisted version is preserved"
    Expect.equal current.CreationDate original.CreationDate "creation time is preserved"
    Expect.equal current.Metadata original.Metadata "all metadata is preserved"

let private manifestsAndIdentity =
    testCase "event upcasting: legacy and stable fixtures retain every envelope field through a chain"
    <| fun _ ->
        registerNames ()
        use database = new Database()
        let api = database.Start(upcasters = true)
        try
            let serializer = FCQRS.ActorSerialization.STJSerializer(api.System :?> ExtendedActorSystem)
            let original = envelope ()
            // Literal historical JSON: a changed writer cannot silently update
            // both sides of this compatibility test to a new representation.
            let bytes =
                System.Text.Encoding.UTF8.GetBytes
                    """{"EventDetails":{"Amount":7},"CreationDate":"2026-09-13T11:22:33Z","Id":{"Case":"MessageId","Item":{"Case":"ShortString","Item":"01a08a65-a88d-73a1-87d0-ca508699d505"}},"Sender":{"Case":"AggregateId","Item":{"Case":"ShortString","Item":"original sender"}},"CorrelationId":{"Case":"CID","Item":{"Case":"ShortString","Item":"original-correlation"}},"Version":{"Case":"Version","Item":11},"Metadata":{"tenant":"north","traceparent":"unchanged"}}"""
            let before = Array.copy bytes
            let stable = serializer.Manifest original
            Expect.equal stable "fcqrs:ev(upcasting.test.first)" "the fixture uses its historical stable payload name"
            for manifest in [ stable; typeof<Event<FirstEvent>>.AssemblyQualifiedName |> string ] do
                let materialized = serializer.FromBinary(bytes, manifest)
                Expect.equal (materialized.GetType()) typeof<Event<FirstEvent>> "serialization materializes the historical type before conversion"
                let current = convert api materialized :?> Event<CurrentEvent>
                Expect.equal current.EventDetails { MinorAmount = 700; Currency = "EUR" } "both conversion steps run"
                sameEnvelope original current
                Expect.isTrue (obj.ReferenceEquals(convert api (boxed current), current)) "an already-current event is unchanged"
            Expect.sequenceEqual bytes before "upcasting cannot rewrite serialized journal bytes"
        finally wait (api.Stop())

let private runtimeIsolation =
    testCase "event upcasting: registrations are scoped to one ActorSystem and only Event envelopes"
    <| fun _ ->
        use first = new Database()
        use second = new Database()
        let upgraded = first.Start(upcasters = true)
        let untouched = second.Start()
        try
            let original = boxed (envelope ())
            Expect.equal ((convert upgraded original).GetType()) typeof<Event<CurrentEvent>> "the configured system applies its chain"
            Expect.isTrue (obj.ReferenceEquals(convert untouched original, original)) "another runtime retains the historical event"
            let payload = boxed ({ Amount = 4 }: FirstEvent)
            Expect.isTrue (obj.ReferenceEquals(convert upgraded payload, payload)) "bare payloads are not event reads"
            let command: Command<FirstEvent> =
                { CommandDetails = { Amount = 4 }; CreationDate = DateTime.UtcNow; Id = (envelope()).Id
                  Sender = None; CorrelationId = Fcqrs.newCid(); Metadata = Map.empty }
            Expect.isTrue (obj.ReferenceEquals(convert upgraded (boxed command), command)) "commands are never upcast"
        finally
            wait (upgraded.Stop())
            wait (untouched.Stop())

let private rejectedRegistrations =
    testCase "event upcasting: duplicate sources, self edges and cycles are rejected without corrupting the chain"
    <| fun _ ->
        use database = new Database()
        let api = database.Start(upcasters = true)
        try
            Expect.throws (fun () -> Fcqrs.withEventUpcaster<FirstEvent, CurrentEvent> api (firstToSecond >> secondToCurrent) |> ignore)
                "a source cannot acquire a second conversion"
            Expect.throws (fun () -> Fcqrs.withEventUpcaster<CurrentEvent, CurrentEvent> api id |> ignore)
                "self edges cannot terminate"
            Expect.throws (fun () -> Fcqrs.withEventUpcaster<CurrentEvent, FirstEvent> api (fun event -> { Amount = event.MinorAmount }) |> ignore)
                "a cycle is rejected during registration"
            let converted = convert api (boxed (envelope ())) :?> Event<CurrentEvent>
            Expect.equal converted.EventDetails.MinorAmount 700 "rejected registrations did not partially mutate the chain"
            currentAggregate api (ConcurrentQueue()) |> ignore
            Expect.throws (fun () -> Fcqrs.withEventUpcaster<UnrelatedEvent, FirstEvent> api (fun _ -> { Amount = 1 }) |> ignore)
                "aggregate registration freezes the runtime's conversions"
        finally wait (api.Stop())

let private declaredPayloadType =
    testCase "event upcasting: F# union cases dispatch through the envelope's declared payload type"
    <| fun _ ->
        use database = new Database()
        let api = database.Start()
        try
            Fcqrs.withEventUpcaster<HistoricalUnion, CurrentEvent> api (function
                | Credited amount -> { MinorAmount = amount; Currency = "EUR" }
                | Debited amount -> { MinorAmount = -amount; Currency = "EUR" }) |> ignore
            let template = envelope ()
            let original: Event<HistoricalUnion> =
                { EventDetails = Debited 23; CreationDate = template.CreationDate; Id = template.Id
                  Sender = template.Sender; CorrelationId = template.CorrelationId; Version = template.Version; Metadata = template.Metadata }
            Expect.notEqual (original.EventDetails.GetType()) typeof<HistoricalUnion> "the selected union case has a generated runtime subtype"
            let current = convert api (boxed original) :?> Event<CurrentEvent>
            Expect.equal current.EventDetails.MinorAmount -23 "lookup uses HistoricalUnion rather than its generated Debited subtype"
            sameEnvelope original current
        finally wait (api.Stop())

let private converterFailures =
    testCase "event upcasting: null and throwing converters fail with their source and target types"
    <| fun _ ->
        for converter in [ (fun (_: FirstEvent) -> Unchecked.defaultof<SecondEvent>); (fun _ -> failwith "converter-failed") ] do
            use database = new Database()
            let api = database.Start()
            try
                Fcqrs.withEventUpcaster<FirstEvent, SecondEvent> api converter |> ignore
                let error =
                    try
                        convert api (boxed (envelope ())) |> ignore
                        failtest "Invalid conversion unexpectedly succeeded."
                    with :? InvalidOperationException as error -> error
                Expect.stringContains error.Message "FirstEvent" "the failed source is named"
                Expect.stringContains error.Message "SecondEvent" "the failed target is named"
            finally wait (api.Stop())

let private mixedRecovery =
    testCase "event upcasting: SQLite mixed legacy and stable history recovers before new writes"
    <| fun _ ->
        use database = new Database()
        let original = seed database
        database.LegacyFirstManifest()
        let bytes = database.JournalBytes()
        let api = database.Start(upcasters = true)
        try
            let folded = ConcurrentQueue<Event<CurrentEvent>>()
            let aggregate = currentAggregate api folded
            Fcqrs.wireSagaStarters api []
            let reply = send aggregate (Fcqrs.newCid()) Read
            Expect.equal reply.EventDetails.MinorAmount 500 "the new aggregate replays both historical shapes"
            let recovered = folded.ToArray() |> Array.distinctBy (fun event -> event.Version) |> Array.take 2
            for index in 0..1 do sameEnvelope original[index] recovered[index]
            let next = send aggregate (Fcqrs.newCid()) (Add 700)
            Expect.equal (next.Version |> ValueLens.Value) 3L "upcasting did not insert extra journal positions"
            Expect.equal (send aggregate (Fcqrs.newCid()) Read).EventDetails.MinorAmount 1200 "new writes extend the recovered current state"
            Expect.equal (database.Scalar "SELECT COUNT(*) FROM journal") 3L "two historical events and one current event remain"
            let after = database.JournalBytes()
            for index in 0..1 do Expect.sequenceEqual after[index] bytes[index] "recovery left old payload bytes intact"
        finally wait (api.Stop())

let private projections =
    testCase "event upcasting: legacy and transactional projections rebuild one event per journal row"
    <| fun _ ->
        use database = new Database()
        seed database |> ignore
        database.LegacyFirstManifest()
        let api = database.Start(upcasters = true)
        try
            let aggregate = currentAggregate api (ConcurrentQueue())
            Fcqrs.wireSagaStarters api []
            send aggregate (Fcqrs.newCid()) (Add 700) |> ignore
            let legacy = ConcurrentQueue<int64 * Event<CurrentEvent>>()
            let completed = signal<unit>()
            Fcqrs.projection api
                (Projection.single 0L (fun offset value ->
                    match value with
                    | :? Event<CurrentEvent> as event ->
                        legacy.Enqueue(offset, event)
                        if legacy.Count = 3 then completed.TrySetResult() |> ignore
                    | _ -> failwith "The legacy projection received a historical event type."))
            |> ignore
            wait completed.Task
            let store =
                SqlProjectionStore(ProjectionSqlDialect.Sqlite,
                    Func<DbConnection>(fun () -> new SqliteConnection(database.ConnectionString)),
                    Func<DbConnection>(fun () -> new SqliteConnection($"Data Source={database.ReadModel};Pooling=False")))
            let options = TransactionalProjectionOptions("event-upcasting-rebuild", store)
            options.PollInterval <- TimeSpan.FromMilliseconds 50.0
            let transactional = ConcurrentQueue<int64 * Event<CurrentEvent>>()
            use projection =
                Fcqrs.transactionalProjection api options (fun _ _ envelope ->
                    match envelope.Event with
                    | :? Event<CurrentEvent> as event -> transactional.Enqueue(envelope.SequenceNr, event)
                    | _ -> failwith "The transactional projection received a historical event type."
                    Task.CompletedTask)
            wait (projection.CatchUpAsync())
            let legacy = legacy.ToArray()
            let transactional = transactional.ToArray()
            Expect.equal legacy.Length 3 "legacy rebuild retains one-to-one event cardinality"
            Expect.equal transactional.Length 3 "transactional rebuild retains one-to-one event cardinality"
            Expect.sequenceEqual (legacy |> Array.map (snd >> fun event -> event.EventDetails.MinorAmount)) [| 200; 300; 700 |]
                "legacy projection sees historical upgrades and the current write"
            Expect.sequenceEqual (transactional |> Array.map fst) [| 1L; 2L; 3L |] "original journal sequences survive"
            for index in 0..2 do
                sameEnvelope (snd legacy[index]) (snd transactional[index])
                Expect.equal (snd legacy[index]).EventDetails (snd transactional[index]).EventDetails "both read paths use the same conversion chain"
        finally wait (api.Stop())

let private aggregateSnapshot =
    testCase "event upcasting: a compatible old aggregate snapshot keeps its state and upcasts only the journal tail"
    <| fun _ ->
        use database = new Database()
        let old = database.Start()
        try
            let aggregate =
                Fcqrs.aggregate old
                    { Name = "UpcastCounter"
                      Initial = 0
                      Decide = fun (command: Command<CounterCommand>) _ ->
                          match command.CommandDetails with
                          | Add amount -> PersistEvent({ Amount = amount }: FirstEvent)
                          | Read -> IgnoreEvent
                      // Old and new aggregate snapshots both contain an int in
                      // minor units, although their journal payloads differ.
                      Fold = fun (event: Event<FirstEvent>) state -> state + event.EventDetails.Amount * 100
                      Snapshots = Every 2
                      Passivation = PassivationPolicy.Never }
            Fcqrs.wireSagaStarters old []
            send aggregate (Fcqrs.newCid()) (Add 2) |> ignore
            send aggregate (Fcqrs.newCid()) (Add 3) |> ignore
            let snapshotReady () = database.Scalar "SELECT COALESCE(MAX(sequence_number), 0) FROM snapshot" = 2L
            let elapsed = Diagnostics.Stopwatch.StartNew()
            while not (snapshotReady()) && elapsed.Elapsed < deadline do Task.Delay(20).GetAwaiter().GetResult()
            Expect.isTrue (snapshotReady()) "the old snapshot durably covers exactly the first two historical events"
            send aggregate (Fcqrs.newCid()) (Add 4) |> ignore
        finally wait (old.Stop())
        let current = database.Start(upcasters = true)
        try
            let folded = ConcurrentQueue<Event<CurrentEvent>>()
            let aggregate = currentAggregate current folded
            Fcqrs.wireSagaStarters current []
            Expect.equal (send aggregate (Fcqrs.newCid()) Read).EventDetails.MinorAmount 900 "snapshot state 500 is retained and the historical tail adds 400"
            Expect.sequenceEqual (folded.ToArray() |> Array.distinctBy (fun event -> event.Version) |> Array.map (fun event -> event.EventDetails.MinorAmount)) [| 400 |]
                "NoSnapshots still loads an existing compatible snapshot instead of replaying its covered history"
            let next = send aggregate (Fcqrs.newCid()) (Add 100)
            Expect.equal (next.Version |> ValueLens.Value) 4L "the recovered snapshot and tail preserve the next persisted version"
            Expect.equal (send aggregate (Fcqrs.newCid()) Read).EventDetails.MinorAmount 1000 "new events extend the recovered snapshot state"
        finally wait (current.Stop())

let private liveWrites =
    testCase "event upcasting: registration does not rewrite a live writer's persisted or deferred replies"
    <| fun _ ->
        use database = new Database()
        let api = database.Start(upcasters = true)
        try
            let folded = ConcurrentQueue<Event<FirstEvent>>()
            let aggregate = firstAggregate api folded
            Fcqrs.wireSagaStarters api []
            let persisted = send aggregate (Fcqrs.newCid()) (Add 4)
            let deferred = send aggregate (Fcqrs.newCid()) Read
            Expect.equal persisted.EventDetails.Amount 4 "the live command returns its declared payload type"
            Expect.equal deferred.EventDetails.Amount 4 "the live deferred reply also retains its declared type"
            Expect.sequenceEqual (folded.ToArray() |> Array.map (fun event -> event.EventDetails.Amount)) [| 4; 4; 4 |]
                "the live and journal-only folds, then the deferred fold, all retain original payloads"
            Expect.equal (database.Scalar "SELECT COUNT(*) FROM journal") 1L "the deferred reply remains unjournaled"
        finally wait (api.Stop())

let private projectionFailure =
    testCase "event upcasting: failed conversion stops transactional progress and corrected replay resumes once"
    <| fun _ ->
        use database = new Database()
        seed database |> ignore
        let readString = $"Data Source={database.ReadModel};Pooling=False"
        let count () =
            use connection = new SqliteConnection(readString)
            connection.Open()
            use command = connection.CreateCommand()
            command.CommandText <- "SELECT COUNT(*) FROM applied"
            Convert.ToInt64(command.ExecuteScalar())
        do
            use connection = new SqliteConnection(readString)
            connection.Open()
            use command = connection.CreateCommand()
            command.CommandText <- "CREATE TABLE applied (sequence_number BIGINT PRIMARY KEY, amount INTEGER NOT NULL)"
            command.ExecuteNonQuery() |> ignore
        let start api =
            let store =
                SqlProjectionStore(ProjectionSqlDialect.Sqlite,
                    Func<DbConnection>(fun () -> new SqliteConnection(database.ConnectionString)),
                    Func<DbConnection>(fun () -> new SqliteConnection(readString)))
            let options = TransactionalProjectionOptions("upcasting-failure", store)
            Fcqrs.transactionalProjection api options (fun connection transaction envelope ->
                let event = envelope.Event :?> Event<CurrentEvent>
                use command = connection.CreateCommand()
                command.Transaction <- transaction
                command.CommandText <- "INSERT INTO applied (sequence_number, amount) VALUES (@sequence, @amount)"
                let add name value =
                    let parameter = command.CreateParameter()
                    parameter.ParameterName <- name
                    parameter.Value <- value
                    command.Parameters.Add parameter |> ignore
                add "@sequence" (boxed envelope.SequenceNr)
                add "@amount" (boxed event.EventDetails.MinorAmount)
                command.ExecuteNonQuery() |> ignore
                Task.CompletedTask)
        let broken = database.Start()
        try
            Fcqrs.withEventUpcaster<FirstEvent, SecondEvent> broken firstToSecond |> ignore
            Fcqrs.withEventUpcaster<SecondEvent, CurrentEvent> broken (fun event ->
                if event.Amount = 3 then failwith "historical conversion requires correction"
                secondToCurrent event) |> ignore
            use projection = start broken
            Expect.throws (fun () -> wait projection.Completion) "a converter failure faults projection completion"
            Expect.throws (fun () -> wait (projection.CatchUpAsync())) "catch-up never declares the failed source range complete"
            Expect.equal (count()) 1L "only the successfully converted first event committed"
        finally wait (broken.Stop())
        let corrected = database.Start(upcasters = true)
        try
            use projection = start corrected
            wait (projection.CatchUpAsync())
            Expect.equal (count()) 2L "the corrected runtime retries the failed position without reapplying the committed one"
        finally wait (corrected.Stop())

let private sagaRecovery snapshots =
    testCase $"event upcasting: saga originator wrappers recover through {snapshots} without rewriting user state"
    <| fun _ ->
        use database = new Database()
        let cid = Fcqrs.newCid()
        let registerSaga api (aggregate: AggregateHandle<CounterCommand, 'Event>) amount (entered: TaskCompletionSource<int>) =
            Fcqrs.saga api
                { Name = "UpcastWorkflow"
                  InitialData = "unchanged-user-data"
                  Originator = aggregate.Factory
                  HandleEvent = fun value state ->
                      match value, state.State with
                      | (:? Event<'Event> as event), None -> StateChangedEvent(Waiting(amount event.EventDetails))
                      | _ -> UnhandledEvent
                  ApplySideEffects = fun state recovering ->
                      let (Waiting amount) = state.State
                      if recovering then
                          Expect.equal state.Data "unchanged-user-data" "upcasting leaves application saga data intact"
                          entered.TrySetResult amount |> ignore
                      Stay, []
                  StartOn = fun (_: Event<'Event>) -> true
                  Snapshots = snapshots }
        let old = database.Start(sameCluster = true)
        try
            let aggregate = firstAggregate old (ConcurrentQueue())
            let saga = registerSaga old aggregate (fun event -> event.Amount) (signal())
            Fcqrs.wireSagaStarters old [ saga ]
            send aggregate cid (Add 2) |> ignore
            let table, required = if snapshots = NoSnapshots then "journal", 3L else "snapshot", 1L
            let ready () = database.Scalar ("SELECT COUNT(*) FROM " + table + " WHERE persistence_id LIKE '%~Saga%'") >= required
            let started = Diagnostics.Stopwatch.StartNew()
            while not (ready()) && started.Elapsed < deadline do Task.Delay(20).GetAwaiter().GetResult()
            Expect.isTrue (ready()) "the intended historical saga recovery records are durable"
        finally wait (old.Stop())
        let current = database.Start(upcasters = true, sameCluster = true)
        try
            let entered = signal<int>()
            let aggregate = currentAggregate current (ConcurrentQueue())
            let saga = registerSaga current aggregate (fun event -> event.MinorAmount) entered
            Fcqrs.wireSagaStarters current [ saga ]
            // Explicit same-CID delivery also activates a passivated entity;
            // success requires recovery of its historical typed start wrapper.
            send aggregate cid (Add 700) |> ignore
            Expect.equal (result entered.Task) 2 "only originator events are upgraded; the persisted workflow state remains exactly two"
        finally wait (current.Stop())

type HostedCurrentAggregate() =
    inherit FCQRS.CSharp.Aggregate<int, CounterCommand, CurrentEvent>()
    override _.InitialState = 0
    override _.EntityName = "UpcastCounter"
    override _.HandleCommand(command, state) =
        match command.CommandDetails with
        | Add amount -> PersistEvent { MinorAmount = amount; Currency = "EUR" }
        | Read -> DeferEvent { MinorAmount = state; Currency = "EUR" }
    override _.ApplyEvent(event, state) = state + event.EventDetails.MinorAmount

let private hostedRegistration =
    testCase "event upcasting: C# host builder installs converters before aggregate recovery"
    <| fun _ ->
        use database = new Database()
        seed database |> ignore
        let builder = HostBuilder()
        builder.ConfigureServices(Action<HostBuilderContext, IServiceCollection>(fun _ services ->
            services.AddFcqrs(database.ConnectionString, "UpcastHost" + Guid.NewGuid().ToString("N"))
                .WithEventUpcaster<FirstEvent, SecondEvent>(Func<FirstEvent, SecondEvent>(firstToSecond))
                .WithEventUpcaster<SecondEvent, CurrentEvent>(Func<SecondEvent, CurrentEvent>(secondToCurrent))
                .AddAggregate<HostedCurrentAggregate, int, CounterCommand, CurrentEvent>()
            |> ignore)) |> ignore
        use host = builder.Build()
        try
            wait (host.StartAsync())
            let handler = host.Services.GetRequiredService<FCQRS.CSharp.Handler<CounterCommand, CurrentEvent>>()
            let reply = handler.Invoke(Func<CurrentEvent, bool>(fun _ -> true), Fcqrs.newCid(), Fcqrs.aggregateId "account", Read) |> result
            Expect.equal reply.EventDetails.MinorAmount 500 "host startup registers the full chain before readers activate"
        finally wait (host.StopAsync())

let tests =
    testSequenced (testList "event upcasting"
        [ manifestsAndIdentity; runtimeIsolation; rejectedRegistrations; declaredPayloadType; converterFailures
          mixedRecovery; aggregateSnapshot; projections; liveWrites; projectionFailure; sagaRecovery NoSnapshots; sagaRecovery (Every 2)
          hostedRegistration ])
