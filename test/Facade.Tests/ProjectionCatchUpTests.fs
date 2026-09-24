module ProjectionCatchUpTests

open System
open System.Data
open System.Data.Common
open System.IO
open System.Threading
open System.Threading.Tasks
open Akka.Persistence.Query
open Expecto
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging.Abstractions
open Npgsql
open FCQRS
open FCQRS.Common
open FCQRS.FSharp
open FCQRS.Model.Data
open FCQRS.ProjectionStorage
open FCQRS.Projections

type CatchUpEvent = Added of int

type BookkeepingCommand = Open
type BookkeepingEvent = Opened
type BookkeepingState = Closed

type CausalOriginCommand = Begin
type CausalOriginEvent = Begun
type CausalFollowUpCommand = FollowUp
type CausalFollowUpEvent = FollowedUp
type CausalRelayState = Relaying
type TallyCommand = Tally of int
type TallyEvent = Tallied of int

let private wait (work: Task) =
    work.WaitAsync(TimeSpan.FromSeconds 20.0).GetAwaiter().GetResult()

let private signal () = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

let private execute (connection: DbConnection) (transaction: DbTransaction option) sql parameters =
    use command = connection.CreateCommand()
    command.CommandText <- sql
    transaction |> Option.iter (fun tx -> command.Transaction <- tx)
    for name, value in parameters do
        let parameter = command.CreateParameter()
        parameter.ParameterName <- name
        parameter.Value <- value
        command.Parameters.Add parameter |> ignore
    command.ExecuteNonQuery() |> ignore

type private Fixture(postgres: string option, ?readJournalOverride: string) =
    let suffix = Guid.NewGuid().ToString("N")
    let journalPath = Path.Combine(Path.GetTempPath(), $"fcqrs-catchup-journal-{suffix}.db")
    let projectionPath = Path.Combine(Path.GetTempPath(), $"fcqrs-catchup-read-{suffix}.db")
    let journalDatabase = "catchup_j_" + suffix
    let projectionDatabase = "catchup_p_" + suffix
    let connectionString database =
        match postgres with
        | Some root ->
            let builder = NpgsqlConnectionStringBuilder(root)
            builder.Database <- database
            builder.ConnectionString
        | None -> $"Data Source={database};Pooling=False;Default Timeout=5"
    let journalString = connectionString (if postgres.IsSome then journalDatabase else journalPath)
    let projectionString = connectionString (if postgres.IsSome then projectionDatabase else projectionPath)
    let connection (text: string) : DbConnection =
        if postgres.IsSome then new NpgsqlConnection(text) else new SqliteConnection(text)
    do
        match postgres with
        | None -> ()
        | Some root ->
            use admin = new NpgsqlConnection(root)
            admin.Open()
            execute admin None $"CREATE DATABASE {journalDatabase}" []
            execute admin None $"CREATE DATABASE {projectionDatabase}" []
    let api =
        let configuration = VerifySerialization.configuration()
        readJournalOverride |> Option.iter (fun value ->
            configuration.AddInMemoryCollection(
                [ Collections.Generic.KeyValuePair<string, string | null>("config:akka:persistence:query:journal:sql:connection-string", value) ])
            |> ignore)
        Fcqrs.actor (configuration.Build()) NullLoggerFactory.Instance
            (Some(Fcqrs.connect
                (if postgres.IsSome then FCQRS.Actor.DBType.PostgreSQL15 else FCQRS.Actor.DBType.Sqlite)
                journalString)) ("CatchUp" + suffix)
    let counter =
        Fcqrs.aggregate api
            { Name = "CatchUpCounter"
              Initial = 0
              Decide = fun (command: Command<int>) _ -> PersistEvent(Added command.CommandDetails)
              Fold = fun (event: Event<CatchUpEvent>) state -> let (Added amount) = event.EventDetails in state + amount
              Snapshots = NoSnapshots
              Passivation = PassivationPolicy.Default }
    do
        Fcqrs.wireSagaStarters api []
        use read = connection projectionString
        read.Open()
        execute read None
            "CREATE TABLE applied_events (persistence_id TEXT NOT NULL, sequence_nr BIGINT NOT NULL, amount INTEGER NOT NULL)" []
    let capturesGate = obj ()
    let mutable capturesClosed = 0
    let captureWaiters = ResizeArray<int * TaskCompletionSource>()
    let journalConnection () =
        let journal = connection journalString
        // Capture owns a fresh connection and closes it after consuming the
        // snapshot reader. Factory-validation connections are never opened.
        journal.StateChange.Add(fun changed ->
            if changed.OriginalState = ConnectionState.Open && changed.CurrentState = ConnectionState.Closed then
                lock capturesGate (fun () ->
                    capturesClosed <- capturesClosed + 1
                    for target, waiter in captureWaiters do
                        if capturesClosed >= target then waiter.TrySetResult() |> ignore))
        journal
    let store =
        SqlProjectionStore(
            (if postgres.IsSome then ProjectionSqlDialect.PostgreSql else ProjectionSqlDialect.Sqlite),
            Func<DbConnection>(journalConnection),
            Func<DbConnection>(fun () -> connection projectionString))
    member _.Api = api
    member _.Store = store
    member _.WaitForCaptures(count) : Task =
        lock capturesGate (fun () ->
            if capturesClosed >= count then Task.CompletedTask
            else
                let completed = signal ()
                captureWaiters.Add((count, completed))
                completed.Task)
    member _.OpenJournal() =
        let journal = connection journalString
        journal.Open()
        journal
    member _.Send(id, amount) =
        counter.Send (Fcqrs.newCid()) (Fcqrs.aggregateId id) amount (fun _ -> true)
        |> fun work -> Async.RunSynchronously(work, 20000)
        |> ignore
    member _.Options(name) =
        let options = TransactionalProjectionOptions(name, store)
        options.PollInterval <- TimeSpan.FromMilliseconds 50.0
        options.CatchUpTimeout <- TimeSpan.FromSeconds 10.0
        options.BatchSize <- 2
        options
    member _.Apply(connection: DbConnection) (transaction: DbTransaction) (envelope: EventEnvelope) =
        match envelope.Event with
        | :? Event<CatchUpEvent> as event ->
            let (Added amount) = event.EventDetails
            execute connection (Some transaction)
                "INSERT INTO applied_events (persistence_id, sequence_nr, amount) VALUES (@pid, @seq, @amount)"
                [ "@pid", box envelope.PersistenceId; "@seq", box envelope.SequenceNr; "@amount", box amount ]
        | _ -> ()
        Task.CompletedTask
    member _.Scalar(sql: string) =
        use read = connection projectionString
        read.Open()
        use command = read.CreateCommand()
        command.CommandText <- sql
        Convert.ToInt64(command.ExecuteScalar())
    interface IDisposable with
        member _.Dispose() =
            wait (api.Stop())
            match postgres with
            | None ->
                for database in [ journalPath; projectionPath ] do
                    for path in [ database; database + "-wal"; database + "-shm" ] do
                        if File.Exists path then File.Delete path
            | Some root ->
                NpgsqlConnection.ClearAllPools()
                use admin = new NpgsqlConnection(root)
                admin.Open()
                execute admin None $"DROP DATABASE {journalDatabase} WITH (FORCE)" []
                execute admin None $"DROP DATABASE {projectionDatabase} WITH (FORCE)" []

let private allAggregates postgres =
    use fixture = new Fixture(postgres)
    fixture.Send("first", 2)
    fixture.Send("second", 3)
    fixture.Send("first", 5)
    use projection = Fcqrs.transactionalProjection fixture.Api (fixture.Options "all-aggregates") fixture.Apply
    wait (projection.CatchUpAsync())
    Expect.equal (fixture.Scalar "SELECT COUNT(*) FROM applied_events") 3L "catch-up includes every aggregate captured from the journal"
    Expect.equal (fixture.Scalar "SELECT SUM(amount) FROM applied_events") 10L "completion follows committed read-model updates"
    fixture.Send("third", 7)
    wait (projection.CatchUpAsync())
    Expect.equal (fixture.Scalar "SELECT COUNT(*) FROM applied_events") 4L "subsequent barriers discover new persistence IDs"

let private rollbackAndRecovery =
    testCase "catch-up: failed transactions roll back and a restarted projection replays them"
    <| fun _ ->
        use fixture = new Fixture(None)
        fixture.Send("rollback", 9)
        let failed = signal ()
        let brokenHandler connection transaction envelope =
            task {
                do! fixture.Apply connection transaction envelope
                failed.TrySetResult() |> ignore
                return raise (InvalidOperationException("deliberate transactional failure"))
            } :> Task
        let broken = Fcqrs.transactionalProjection fixture.Api (fixture.Options "recoverable") brokenHandler
        try
            let barrier = broken.CatchUpAsync()
            wait failed.Task
            try wait barrier with :? InvalidOperationException -> ()
            Expect.equal (fixture.Scalar "SELECT COUNT(*) FROM applied_events") 0L "neither app writes nor checkpoint may survive the failed transaction"
        finally
            broken.Dispose()
        use recovered = Fcqrs.transactionalProjection fixture.Api (fixture.Options "recoverable") fixture.Apply
        wait (recovered.CatchUpAsync())
        Expect.equal (fixture.Scalar "SELECT SUM(amount) FROM applied_events") 9L "the uncommitted event is replayed after restart"
        recovered.Dispose()
        use resumed = Fcqrs.transactionalProjection fixture.Api (fixture.Options "recoverable") fixture.Apply
        wait (resumed.CatchUpAsync())
        Expect.equal (fixture.Scalar "SELECT COUNT(*) FROM applied_events") 1L "a committed checkpoint prevents replaying completed writes"

let private cancellationAndDispose =
    testCase "catch-up: caller cancellation and projection disposal release pending waits"
    <| fun _ ->
        use fixture = new Fixture(None)
        fixture.Send("blocked", 1)
        let entered = signal ()
        let release = signal ()
        let disposalEntered = signal ()
        let disposalRelease = signal ()
        let handler connection transaction (envelope: EventEnvelope) =
            task {
                match envelope.Event with
                | :? Event<CatchUpEvent> as event when event.EventDetails = Added 2 ->
                    disposalEntered.TrySetResult() |> ignore
                    do! disposalRelease.Task
                | _ ->
                    entered.TrySetResult() |> ignore
                    do! release.Task
                do! fixture.Apply connection transaction envelope
            } :> Task
        use projection = Fcqrs.transactionalProjection fixture.Api (fixture.Options "canceled") handler
        try
            wait entered.Task
            use alreadyCanceled = new CancellationTokenSource()
            alreadyCanceled.Cancel()
            let early = projection.CatchUpAsync(alreadyCanceled.Token)
            try wait early with :? OperationCanceledException -> ()
            Expect.isTrue early.IsCanceled "an already-canceled caller cannot succeed"
            use cancellation = new CancellationTokenSource()
            let canceled = projection.CatchUpAsync(cancellation.Token)
            let surviving = projection.CatchUpAsync()
            cancellation.Cancel()
            try wait canceled with :? OperationCanceledException -> ()
            Expect.isTrue canceled.IsCanceled "cancellation releases the individual waiting caller"
            Expect.isFalse surviving.IsCompleted "another caller continues waiting for the transaction"
            release.TrySetResult() |> ignore
            wait surviving
            Expect.equal (fixture.Scalar "SELECT COUNT(*) FROM applied_events") 1L "caller cancellation does not cancel projection work"
            fixture.Send("blocked", 2)
            wait disposalEntered.Task
            let pendingAtDisposal = projection.CatchUpAsync()
            projection.Dispose()
            try wait pendingAtDisposal with :? OperationCanceledException -> () | :? ObjectDisposedException -> ()
            Expect.isFalse pendingAtDisposal.IsCompletedSuccessfully "disposal releases an incomplete wait without claiming success"
            let rejected =
                try
                    let stopped = projection.CatchUpAsync()
                    try wait stopped with :? OperationCanceledException -> () | :? ObjectDisposedException -> ()
                    not stopped.IsCompletedSuccessfully
                with :? ObjectDisposedException -> true
            Expect.isTrue rejected "disposed projections cannot acknowledge new barriers"
        finally
            release.TrySetResult() |> ignore
            disposalRelease.TrySetResult() |> ignore

let private deletedHistory =
    testCase "catch-up: a deleted sequence cannot be mistaken for a completed prefix"
    <| fun _ ->
        use fixture = new Fixture(None)
        fixture.Send("missing", 1)
        fixture.Send("missing", 2)
        use journal = fixture.OpenJournal()
        execute journal None "DELETE FROM journal WHERE sequence_number = 1" []
        use projection = Fcqrs.transactionalProjection fixture.Api (fixture.Options "deleted") fixture.Apply
        let barrier = projection.CatchUpAsync()
        try wait barrier with :? InvalidOperationException -> ()
        Expect.isTrue barrier.IsFaulted "an incomplete retained history faults the barrier"
        Expect.equal (fixture.Scalar "SELECT COUNT(*) FROM applied_events") 0L "the projection does not advance past the missing first event"

let private fixedBoundary =
    testCase "catch-up: the captured boundary excludes later persisted events"
    <| fun _ ->
        use fixture = new Fixture(None)
        let firstEntered = signal ()
        let releaseFirst = signal ()
        let laterEntered = signal ()
        let releaseLater = signal ()
        let handler connection transaction (envelope: EventEnvelope) =
            task {
                match envelope.Event with
                | :? Event<CatchUpEvent> as event ->
                    match event.EventDetails with
                    | Added 1 ->
                        firstEntered.TrySetResult() |> ignore
                        do! releaseFirst.Task
                    | Added 2 ->
                        laterEntered.TrySetResult() |> ignore
                        do! releaseLater.Task
                    | _ -> ()
                | _ -> ()
                do! fixture.Apply connection transaction envelope
            } :> Task
        let options = fixture.Options "fixed"
        options.PollInterval <- TimeSpan.FromHours 1.0
        use projection = Fcqrs.transactionalProjection fixture.Api options handler
        try
            wait (projection.CatchUpAsync())
            wait (fixture.WaitForCaptures 2)
            fixture.Send("fixed", 1)
            let firstBoundary = projection.CatchUpAsync()
            // Both startup captures are finished and the background poll is
            // parked. This handler can only belong to firstBoundary's snapshot.
            wait firstEntered.Task
            fixture.Send("fixed", 2)
            releaseFirst.TrySetResult() |> ignore
            wait firstBoundary
            let secondBoundary = projection.CatchUpAsync()
            wait laterEntered.Task
            Expect.equal (fixture.Scalar "SELECT COUNT(*) FROM applied_events") 1L "the first wait completes while a later transaction remains blocked"
            Expect.isFalse secondBoundary.IsCompleted "a new barrier includes the later event"
            releaseLater.TrySetResult() |> ignore
            wait secondBoundary
        finally
            releaseFirst.TrySetResult() |> ignore
            releaseLater.TrySetResult() |> ignore

let private wrongJournalSource =
    testCase "catch-up: a reader pointed at another database is rejected"
    <| fun _ ->
        let wrongConnection = "Data Source=:memory:"
        use fixture = new Fixture(None, readJournalOverride = wrongConnection)
        let wrongStore =
            SqlProjectionStore(ProjectionSqlDialect.Sqlite,
                Func<DbConnection>(fun () -> new SqliteConnection(wrongConnection)))
        let options = TransactionalProjectionOptions("wrong-source", wrongStore)
        Expect.throwsT<ArgumentException>
            (fun () ->
                use _projection = Fcqrs.transactionalProjection fixture.Api options fixture.Apply
                ())
            "the capture store and read journal cannot agree on a different database from the write journal"

let private timeoutIsolation =
    testCase "catch-up: timeout bounds one waiter without stopping the projection"
    <| fun _ ->
        use fixture = new Fixture(None)
        fixture.Send("shutdown", 1)
        let entered = signal ()
        let release = signal ()
        let handler connection transaction envelope =
            task {
                entered.TrySetResult() |> ignore
                do! release.Task
                do! fixture.Apply connection transaction envelope
            } :> Task
        let options = fixture.Options "shutdown"
        options.CatchUpTimeout <- TimeSpan.FromMilliseconds 500.0
        use projection = Fcqrs.transactionalProjection fixture.Api options handler
        try
            wait entered.Task
            let timedOut = projection.CatchUpAsync()
            try wait timedOut with :? TimeoutException -> ()
            Expect.isTrue timedOut.IsFaulted "an incomplete transaction causes a bounded timeout"
            Expect.isFalse projection.Completion.IsCompleted "one caller timing out does not stop projection work"
            release.TrySetResult() |> ignore
            wait (projection.CatchUpAsync())
            Expect.equal (fixture.Scalar "SELECT COUNT(*) FROM applied_events") 1L "a later waiter sees the transaction complete"
        finally
            release.TrySetResult() |> ignore
            projection.Dispose()
            wait projection.Completion

let private actorShutdown =
    testCase "catch-up: actor-system shutdown cancels an incomplete wait"
    <| fun _ ->
        use fixture = new Fixture(None)
        fixture.Send("shutdown", 1)
        let entered = signal ()
        let release = signal ()
        let handler connection transaction envelope =
            task {
                entered.TrySetResult() |> ignore
                do! release.Task
                do! fixture.Apply connection transaction envelope
            } :> Task
        use projection = Fcqrs.transactionalProjection fixture.Api (fixture.Options "shutdown") handler
        try
            wait entered.Task
            let pending = projection.CatchUpAsync()
            wait (fixture.Api.Stop())
            try wait pending with :? OperationCanceledException -> ()
            Expect.isTrue pending.IsCanceled "actor-system shutdown releases the incomplete wait without claiming success"
        finally
            release.TrySetResult() |> ignore
            projection.Dispose()
            wait projection.Completion

type HostedCatchUpAggregate() =
    inherit FCQRS.CSharp.Aggregate<int, int, int>()
    override _.InitialState = 0
    override _.EntityName = "HostedCatchUp"
    override _.HandleCommand(command, state) = PersistEvent(state + command.CommandDetails)
    override _.ApplyEvent(event, _) = event.EventDetails

type HostedCatchUpResult() =
    member val Completed = false with get, set

type HostedCatchUpWorker(handler: FCQRS.CSharp.Handler<int, int>, projection: IProjection, result: HostedCatchUpResult) =
    interface IHostedService with
        member _.StartAsync(ct) =
            task {
                let! reply = handler.Invoke(Func<int, bool>(fun _ -> true), FCQRS.CSharp.Values.NewCID(), FCQRS.CSharp.Values.CreateAggregateId "hosted", 42)
                Expect.equal reply.EventDetails 42 "the constructor-injected aggregate handler is ready at startup"
                do! projection.CatchUpAsync(ct)
                result.Completed <- true
            } :> Task
        member _.StopAsync(_) = Task.CompletedTask

let private hostedProjectionInjection =
    testCase "catch-up: hosted services can inject IProjection before startup and await committed writes"
    <| fun _ ->
        let database = Path.Combine(Path.GetTempPath(), $"fcqrs-hosted-catchup-{Guid.NewGuid():N}.db")
        let connectionString = $"Data Source={database};Pooling=False"
        let factory = Func<DbConnection>(fun () -> new SqliteConnection(connectionString))
        do
            use connection = factory.Invoke()
            connection.Open()
            execute connection None "CREATE TABLE hosted_applied (amount INTEGER NOT NULL)" []
        let options = TransactionalProjectionOptions("hosted", SqlProjectionStore(ProjectionSqlDialect.Sqlite, factory))
        let result = HostedCatchUpResult()
        let builder = HostBuilder()
        builder.ConfigureServices(Action<HostBuilderContext, IServiceCollection>(fun _ services ->
            services
                .AddFcqrs(connectionString, "HostedCatchUpTest")
                .AddAggregate<HostedCatchUpAggregate, int, int, int>()
                .AddTransactionalProjection(options,
                    Func<DbConnection, DbTransaction, EventEnvelope, Task>(fun connection transaction envelope ->
                        match envelope.Event with
                        | :? Event<int> as event ->
                            execute connection (Some transaction) "INSERT INTO hosted_applied (amount) VALUES (@amount)" [ "@amount", box event.EventDetails ]
                        | _ -> ()
                        Task.CompletedTask))
            |> ignore
            services.AddSingleton<HostedCatchUpResult>(result) |> ignore
            services.AddHostedService<HostedCatchUpWorker>() |> ignore))
        |> ignore
        use host = builder.Build()
        try
            wait (host.StartAsync())
            Expect.isTrue result.Completed "the injected IProjection catches up during hosted-service startup"
            use connection = factory.Invoke()
            connection.Open()
            use command = connection.CreateCommand()
            command.CommandText <- "SELECT SUM(amount) FROM hosted_applied"
            Expect.equal (Convert.ToInt64(command.ExecuteScalar())) 42L "CatchUpAsync observes the host projection's committed transaction"
        finally
            wait (host.StopAsync())
            for path in [ database; database + "-wal"; database + "-shm" ] do
                if File.Exists path then File.Delete path

let private sqlitePaths () =
    let suffix = Guid.NewGuid().ToString("N")
    suffix,
    Path.Combine(Path.GetTempPath(), $"fcqrs-catchup-journal-{suffix}.db"),
    Path.Combine(Path.GetTempPath(), $"fcqrs-catchup-read-{suffix}.db")

let private sqliteString (path: string) = $"Data Source={path};Pooling=False;Default Timeout=5"

let private sqliteScalar (path: string) (sql: string) =
    use connection = new SqliteConnection(sqliteString path)
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- sql
    Convert.ToInt64(command.ExecuteScalar())

let private sqliteStore journalPath projectionPath =
    SqlProjectionStore(
        ProjectionSqlDialect.Sqlite,
        Func<DbConnection>(fun () -> new SqliteConnection(sqliteString journalPath)),
        Func<DbConnection>(fun () -> new SqliteConnection(sqliteString projectionPath)))

let private deleteDatabases paths =
    for database in paths do
        for path in [ database; database + "-wal"; database + "-shm" ] do
            if File.Exists path then File.Delete path

let private shardingBookkeeping =
    testCase "catch-up: a new projection skips Akka's trimmed sharding history"
    <| fun _ ->
        let suffix, journalPath, projectionPath = sqlitePaths ()
        // Sagas remember their entities through Akka cluster sharding, which journals that
        // bookkeeping under "/sharding/..." and deletes its early history after each snapshot.
        // Akka snapshots every 1000 updates by default; 10 trims the first rows after 30.
        let configuration =
            VerifySerialization.configuration()
                .AddInMemoryCollection(
                    [ Collections.Generic.KeyValuePair<string, string | null>("config:akka:cluster:sharding:snapshot-after", "10") ])
                .Build()
        let api =
            Fcqrs.actor configuration NullLoggerFactory.Instance
                (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite (sqliteString journalPath))) ("Bookkeeping" + suffix)
        try
            let accounts =
                Fcqrs.aggregate api
                    { Name = "BookkeepingAccount"
                      Initial = 0
                      Decide = fun (_: Command<BookkeepingCommand>) _ -> PersistEvent Opened
                      Fold = fun (_: Event<BookkeepingEvent>) state -> state + 1
                      Snapshots = NoSnapshots
                      Passivation = PassivationPolicy.Default }
            let saga =
                Fcqrs.saga api
                    { Name = "BookkeepingSaga"
                      InitialData = ()
                      Originator = accounts.Factory
                      HandleEvent =
                        fun event state ->
                            match event, state.State with
                            | :? Event<BookkeepingEvent>, None -> StateChangedEvent Closed
                            | _ -> UnhandledEvent
                      ApplySideEffects = fun state _ -> match state.State with Closed -> StopSaga, []
                      StartOn = fun (_: Event<BookkeepingEvent>) -> true
                      Snapshots = NoSnapshots }
            Fcqrs.wireSagaStarters api [ saga ]
            // Each correlation ID starts and stops its own saga.
            for _ in 1..40 do
                accounts.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "account") Open (fun _ -> true)
                |> fun work -> Async.RunSynchronously(work, 20000)
                |> ignore
            let firstShardRow () =
                sqliteScalar journalPath "SELECT COALESCE(MIN(sequence_number), 0) FROM journal WHERE persistence_id LIKE '/sharding/%'"
            let deadline = DateTime.UtcNow.AddSeconds 20.0
            while firstShardRow () <= 1L && DateTime.UtcNow < deadline do
                Thread.Sleep 100
            Expect.isGreaterThan (firstShardRow ()) 1L "Akka deleted the start of its sharding history"
            let applied = ref 0
            use projection =
                Fcqrs.transactionalProjection api (TransactionalProjectionOptions("bookkeeping", sqliteStore journalPath projectionPath))
                    (fun _ _ envelope ->
                        match envelope.Event with
                        | :? Event<BookkeepingEvent> -> Interlocked.Increment(&applied.contents) |> ignore
                        | _ -> ()
                        Task.CompletedTask)
            wait (projection.CatchUpAsync())
            Expect.equal applied.Value 40 "every aggregate event is projected"
        finally
            wait (api.Stop())
            deleteDatabases [ journalPath; projectionPath ]

let private causalNotification =
    testCase "catch-up: a correlation waiter wakes only after the event that caused a saga follow-up commits"
    <| fun _ ->
        let suffix, journalPath, projectionPath = sqlitePaths ()
        let api =
            Fcqrs.actor (VerifySerialization.configuration().Build()) NullLoggerFactory.Instance
                (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite (sqliteString journalPath))) ("Causal" + suffix)
        try
            (use read = new SqliteConnection(sqliteString projectionPath)
             read.Open()
             execute read None "CREATE TABLE seen (kind TEXT NOT NULL)" [])
            // The follow-up's persistence ID sorts before the originator's.
            let followUps =
                Fcqrs.aggregate api
                    { Name = "CausalFollowUp"
                      Initial = 0
                      Decide = fun (_: Command<CausalFollowUpCommand>) _ -> PersistEvent FollowedUp
                      Fold = fun (_: Event<CausalFollowUpEvent>) state -> state + 1
                      Snapshots = NoSnapshots
                      Passivation = PassivationPolicy.Default }
            let origins =
                Fcqrs.aggregate api
                    { Name = "CausalOrigin"
                      Initial = 0
                      Decide = fun (_: Command<CausalOriginCommand>) _ -> PersistEvent Begun
                      Fold = fun (_: Event<CausalOriginEvent>) state -> state + 1
                      Snapshots = NoSnapshots
                      Passivation = PassivationPolicy.Default }
            // The saga's command reuses the originator's correlation ID.
            let relay =
                Fcqrs.saga api
                    { Name = "CausalRelay"
                      InitialData = ()
                      Originator = origins.Factory
                      HandleEvent =
                        fun event state ->
                            match event, state.State with
                            | :? Event<CausalOriginEvent>, None -> StateChangedEvent Relaying
                            | _ -> UnhandledEvent
                      ApplySideEffects =
                        fun state _ ->
                            match state.State with
                            | Relaying -> StopSaga, [ toAggregate followUps.Factory "follow-up" (box FollowUp) ]
                      StartOn = fun (_: Event<CausalOriginEvent>) -> true
                      Snapshots = NoSnapshots }
            Fcqrs.wireSagaStarters api [ relay ]
            let options = TransactionalProjectionOptions("causal", sqliteStore journalPath projectionPath)
            // Leave both events to the explicit catch-up below, so they share one snapshot.
            options.PollInterval <- TimeSpan.FromMinutes 10.0
            let handler (connection: DbConnection) (transaction: DbTransaction) (envelope: EventEnvelope) : Task =
                task {
                    let kind =
                        match envelope.Event with
                        | :? Event<CausalOriginEvent> -> Some "origin"
                        | :? Event<CausalFollowUpEvent> -> Some "follow-up"
                        | _ -> None
                    match kind with
                    | Some kind ->
                        // Keep the originator's event uncommitted long enough to observe an early wake.
                        if kind = "origin" then do! Task.Delay 1000
                        execute connection (Some transaction) "INSERT INTO seen (kind) VALUES (@kind)" [ "@kind", box kind ]
                    | None -> ()
                }
                :> Task
            use projection = Fcqrs.transactionalProjection api options handler
            wait (projection.CatchUpAsync())
            let waiting =
                Fcqrs.sendAwaiting (projection :> FCQRS.Query.ISubscribe<IMessageWithCID>)
                    origins (Fcqrs.newCid ()) (Fcqrs.aggregateId "origin") Begin (fun _ -> true)
                |> Async.StartAsTask
            let journaled () =
                sqliteScalar journalPath "SELECT COUNT(*) FROM journal WHERE persistence_id LIKE 'CausalOrigin/%' OR persistence_id LIKE 'CausalFollowUp/%'"
            let deadline = DateTime.UtcNow.AddSeconds 20.0
            while journaled () < 2L && DateTime.UtcNow < deadline do
                Thread.Sleep 50
            Expect.equal (journaled ()) 2L "the originator event and the saga follow-up are journaled"
            let catchUp = projection.CatchUpAsync()
            let reply = waiting.WaitAsync(TimeSpan.FromSeconds 20.0).GetAwaiter().GetResult()
            let originRows = sqliteScalar projectionPath "SELECT COUNT(*) FROM seen WHERE kind = 'origin'"
            wait catchUp
            Expect.equal reply.EventDetails Begun "sendAwaiting returns the command's event"
            Expect.equal originRows 1L "the command's own event was committed when the waiter woke"
        finally
            wait (api.Stop())
            deleteDatabases [ journalPath; projectionPath ]

let private tallyAggregate api name =
    Fcqrs.aggregate api
        { Name = name
          Initial = 0
          Decide = fun (command: Command<TallyCommand>) _ -> let (Tally amount) = command.CommandDetails in PersistEvent(Tallied amount)
          Fold = fun (_: Event<TallyEvent>) state -> state + 1
          Snapshots = NoSnapshots
          Passivation = PassivationPolicy.Default }

let private heldWaiterOrder =
    testCase "catch-up: a held correlation waiter does not reorder other subscribers"
    <| fun _ ->
        let suffix, journalPath, projectionPath = sqlitePaths ()
        let api =
            Fcqrs.actor (VerifySerialization.configuration().Build()) NullLoggerFactory.Instance
                (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite (sqliteString journalPath))) ("HeldOrder" + suffix)
        try
            let tallies = tallyAggregate api "HeldOrderTally"
            Fcqrs.wireSagaStarters api []
            let options = TransactionalProjectionOptions("held-order", sqliteStore journalPath projectionPath)
            // Leave both events to the explicit catch-up below, so they share one snapshot.
            options.PollInterval <- TimeSpan.FromMinutes 10.0
            use projection = Fcqrs.transactionalProjection api options (fun _ _ _ -> Task.CompletedTask)
            wait (projection.CatchUpAsync())
            let seen = Collections.Concurrent.ConcurrentQueue<int>()
            use _all =
                (projection :> FCQRS.Query.ISubscribe<IMessageWithCID>).Subscribe(fun (event: IMessageWithCID) ->
                    match event with
                    | :? Event<TallyEvent> as tally -> let (Tallied amount) = tally.EventDetails in seen.Enqueue amount
                    | _ -> ())
            // The first event has a correlation waiter; the second does not.
            let waiting =
                Fcqrs.sendAwaiting (projection :> FCQRS.Query.ISubscribe<IMessageWithCID>)
                    tallies (Fcqrs.newCid ()) (Fcqrs.aggregateId "tally") (Tally 1) (fun _ -> true)
                |> Async.StartAsTask
            let journaled () = sqliteScalar journalPath "SELECT COUNT(*) FROM journal WHERE persistence_id LIKE 'HeldOrderTally/%'"
            let deadline = DateTime.UtcNow.AddSeconds 20.0
            while journaled () < 1L && DateTime.UtcNow < deadline do
                Thread.Sleep 50
            Async.RunSynchronously(tallies.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "tally") (Tally 2) (fun _ -> true), 20000)
            |> ignore
            wait (projection.CatchUpAsync())
            waiting.WaitAsync(TimeSpan.FromSeconds 20.0).GetAwaiter().GetResult() |> ignore
            let deadline = DateTime.UtcNow.AddSeconds 5.0
            while seen.Count < 2 && DateTime.UtcNow < deadline do
                Thread.Sleep 50
            Expect.sequenceEqual (seen.ToArray()) [ 1; 2 ] "a subscriber without a correlation ID sees journal order"
        finally
            wait (api.Stop())
            deleteDatabases [ journalPath; projectionPath ]

let private observedTimeout =
    testCase "catch-up: a sendAwaiting timeout leaves no unobserved task exception"
    <| fun _ ->
        let suffix, journalPath, projectionPath = sqlitePaths ()
        let configuration =
            VerifySerialization.configuration()
                .AddInMemoryCollection(
                    [ Collections.Generic.KeyValuePair<string, string | null>("config:akka:fcqrs:command-timeout", "3") ])
                .Build()
        let api =
            Fcqrs.actor configuration NullLoggerFactory.Instance
                (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite (sqliteString journalPath))) ("ObservedTimeout" + suffix)
        let unobserved = ref 0
        let count =
            EventHandler<UnobservedTaskExceptionEventArgs>(fun _ args ->
                if args.Exception.InnerExceptions |> Seq.exists (fun error -> error :? TimeoutException) then
                    Interlocked.Increment(&unobserved.contents) |> ignore)
        TaskScheduler.UnobservedTaskException.AddHandler count
        try
            let tallies = tallyAggregate api "ObservedTimeoutTally"
            Fcqrs.wireSagaStarters api []
            let options = TransactionalProjectionOptions("observed-timeout", sqliteStore journalPath projectionPath)
            // The handler holds the event until the wait has timed out, so the notification never arrives.
            let held = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            use projection = Fcqrs.transactionalProjection api options (fun _ _ _ -> held.Task)
            wait (projection.CatchUpAsync())
            let outcome =
                try
                    try
                        Fcqrs.sendAwaiting (projection :> FCQRS.Query.ISubscribe<IMessageWithCID>)
                            tallies (Fcqrs.newCid ()) (Fcqrs.aggregateId "tally") (Tally 1) (fun _ -> true)
                        |> fun work -> Async.RunSynchronously(work, 20000)
                        |> ignore
                        None
                    with error -> Some(error.GetType())
                finally
                    held.TrySetResult() |> ignore
            // Finalizing a faulted task that nothing observed raises UnobservedTaskException.
            for _ in 1..5 do
                GC.Collect()
                GC.WaitForPendingFinalizers()
                Thread.Sleep 100
            Expect.equal outcome (Some typeof<TimeoutException>) "the wait timed out"
            Expect.equal unobserved.Value 0 "the timed-out wait was observed"
        finally
            TaskScheduler.UnobservedTaskException.RemoveHandler count
            wait (api.Stop())
            deleteDatabases [ journalPath; projectionPath ]

let private nullCorrelationId =
    testCase "catch-up: a notification without a correlation ID is published"
    <| fun _ ->
        // Custom notifications from Projection.multi or the C# list handler can carry a null CID.
        let hubType =
            typeof<FCQRS.Query.ISubscribe<IMessageWithCID>>.Assembly
                .GetType("FCQRS.Query+Internal+NotificationHub`1", true)
            |> Unchecked.nonNull
            |> fun definition -> definition.MakeGenericType [| typeof<IMessageWithCID> |]
        let hub =
            Activator.CreateInstance(hubType, [| box 8; box NullLogger.Instance; box (TimeSpan.FromSeconds 5.0) |])
            |> Unchecked.nonNull
        let subscriptions = hub :?> FCQRS.Query.ISubscribe<IMessageWithCID>
        let received = ref 0
        use _all = subscriptions.Subscribe(fun _ -> Interlocked.Increment(&received.contents) |> ignore)
        use _waiter = subscriptions.Subscribe(Fcqrs.newCid (), 1)
        let publish =
            hubType.GetMethod(
                "Publish",
                Reflection.BindingFlags.Instance ||| Reflection.BindingFlags.Public ||| Reflection.BindingFlags.NonPublic
            )
            |> Unchecked.nonNull
        let notification = { new IMessageWithCID with member _.CID = Unchecked.defaultof<CID> }
        let outcome =
            try
                publish.Invoke(hub, [| box notification |]) |> ignore
                None
            with :? Reflection.TargetInvocationException as error ->
                error.InnerException |> Option.ofObj |> Option.map (fun inner -> inner.GetType().Name)
        Expect.isNone outcome "publishing a notification without a correlation ID does not throw"
        let deadline = DateTime.UtcNow.AddSeconds 5.0
        while received.Value < 1 && DateTime.UtcNow < deadline do
            Thread.Sleep 20
        Expect.equal received.Value 1 "the subscriber without a correlation ID receives it"

let private concurrentInstances postgres =
    use fixture = new Fixture(postgres)
    for amount in 1..8 do fixture.Send("shared", amount)
    use first = Fcqrs.transactionalProjection fixture.Api (fixture.Options "shared-name") fixture.Apply
    use second = Fcqrs.transactionalProjection fixture.Api (fixture.Options "shared-name") fixture.Apply
    wait (Task.WhenAll(first.CatchUpAsync(), second.CatchUpAsync()))
    Expect.equal (fixture.Scalar "SELECT COUNT(*) FROM applied_events") 8L "same-name instances fence checkpoint advancement and apply each event once"
    Expect.equal (fixture.Scalar "SELECT SUM(amount) FROM applied_events") 36L "competing workers cannot duplicate read-model effects"

let private postgresCommitInversion connectionString =
    use fixture = new Fixture(Some connectionString)
    fixture.Send("template", 1)
    use projection = Fcqrs.transactionalProjection fixture.Api (fixture.Options "commit-inversion") fixture.Apply
    wait (projection.CatchUpAsync())
    use delayed = fixture.OpenJournal()
    use transaction = delayed.BeginTransaction()
    // Allocate an ordering number and leave that transaction open. The copied
    // real payload preserves the plugin's serializer/manifest representation.
    use columnsCommand = delayed.CreateCommand()
    columnsCommand.Transaction <- transaction
    columnsCommand.CommandText <- "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'journal' AND column_name <> 'ordering' ORDER BY ordinal_position"
    let columns = ResizeArray<string>()
    do
        use reader = columnsCommand.ExecuteReader()
        while reader.Read() do columns.Add(reader.GetString 0)
    let quoted = columns |> Seq.map (fun name -> "\"" + name + "\"") |> String.concat ", "
    let values =
        columns
        |> Seq.map (function "sequence_number" -> "2" | name -> "\"" + name + "\"")
        |> String.concat ", "
    execute delayed (Some transaction) $"INSERT INTO journal ({quoted}) SELECT {values} FROM journal LIMIT 1" []
    fixture.Send("commits-first", 2)
    wait (projection.CatchUpAsync())
    Expect.equal (fixture.Scalar "SELECT COUNT(*) FROM applied_events") 2L "an uncommitted lower ordering does not block the committed snapshot"
    transaction.Commit()
    wait (projection.CatchUpAsync())
    Expect.equal (fixture.Scalar "SELECT COUNT(*) FROM applied_events") 3L "the lower ordering that commits later is never skipped"

let private postgresAmbientSnapshot connectionString =
    use fixture = new Fixture(Some connectionString)
    fixture.Send("ambient", 1)
    let options = fixture.Options "ambient"
    options.PollInterval <- TimeSpan.FromHours 1.0
    use projection = Fcqrs.transactionalProjection fixture.Api options fixture.Apply
    wait (projection.CatchUpAsync())
    wait (fixture.WaitForCaptures 2)
    do
        let mutable transactionOptions = System.Transactions.TransactionOptions()
        transactionOptions.IsolationLevel <- System.Transactions.IsolationLevel.RepeatableRead
        transactionOptions.Timeout <- TimeSpan.FromSeconds 20.0
        use scope =
            new System.Transactions.TransactionScope(
                System.Transactions.TransactionScopeOption.RequiresNew, transactionOptions,
                System.Transactions.TransactionScopeAsyncFlowOption.Enabled)
        use oldJournal = fixture.OpenJournal()
        let journalCount () =
            use command = oldJournal.CreateCommand()
            command.CommandText <- "SELECT COUNT(*) FROM journal"
            Convert.ToInt64(command.ExecuteScalar())
        Expect.equal (journalCount ()) 1L "the caller's ambient transaction establishes an old snapshot"
        let writeOutsideScope =
            use _flow = ExecutionContext.SuppressFlow()
            Task.Run(Action(fun () -> fixture.Send("ambient", 2)))
        wait writeOutsideScope
        Expect.equal (journalCount ()) 1L "repeatable read remains stale after another actor write commits"
        wait (projection.CatchUpAsync())
        // Roll back the caller's scope. Catch-up must have committed its own
        // read-model transaction independently, while background polling is parked.
        ()
    Expect.equal (fixture.Scalar "SELECT COUNT(*) FROM applied_events") 2L "catch-up captures fresh committed history independently of the caller's ambient snapshot"


// A projection started with Fcqrs.projection tracks each aggregate's sequence numbers, so an
// event that commits after later-numbered events still reaches its handler.
let private postgresLateCommitProjection connectionString =
    use fixture = new Fixture(Some connectionString)
    let seen = Collections.Concurrent.ConcurrentQueue<string * int64>()
    use projection =
        Fcqrs.projection fixture.Api
            (Projection.single FromStart (fun message ->
                match message with
                | :? Event<CatchUpEvent> as event -> seen.Enqueue(string event.Sender, ValueLens.Value event.Version)
                | _ -> ()))
    fixture.Send("template", 1)
    wait (projection.CatchUpAsync())
    use delayed = fixture.OpenJournal()
    use transaction = delayed.BeginTransaction()
    use columnsCommand = delayed.CreateCommand()
    columnsCommand.Transaction <- transaction
    columnsCommand.CommandText <- "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'journal' AND column_name <> 'ordering' ORDER BY ordinal_position"
    let columns = ResizeArray<string>()
    do
        use reader = columnsCommand.ExecuteReader()
        while reader.Read() do columns.Add(reader.GetString 0)
    let quoted = columns |> Seq.map (fun name -> "\"" + name + "\"") |> String.concat ", "
    let values = columns |> Seq.map (function "sequence_number" -> "2" | name -> "\"" + name + "\"") |> String.concat ", "
    // Takes a journal number and holds its commit while a later-numbered event commits.
    execute delayed (Some transaction) $"INSERT INTO journal ({quoted}) SELECT {values} FROM journal LIMIT 1" []
    fixture.Send("commits-first", 2)
    wait (projection.CatchUpAsync())
    // Longer than Akka's global-offset reader waits for a missing number before skipping it.
    Thread.Sleep 3000
    Expect.equal seen.Count 2 "the uncommitted event is not delivered yet"
    transaction.Commit()
    wait (projection.CatchUpAsync())
    Expect.equal seen.Count 3 "the event that committed late is delivered"

let private namedProgress =
    testCase "projection: named progress resumes after a restart, and FromStart reads everything again"
    <| fun _ ->
        let suffix, journalPath, projectionPath = sqlitePaths ()
        let run (events: int list) (progress: ProjectionProgress) =
            let api =
                Fcqrs.actor (VerifySerialization.configuration().Build()) NullLoggerFactory.Instance
                    (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite (sqliteString journalPath))) ("NamedProgress" + suffix)
            try
                let tallies = tallyAggregate api "NamedProgressTally"
                Fcqrs.wireSagaStarters api []
                let seen = Collections.Concurrent.ConcurrentQueue<int>()
                use projection =
                    Fcqrs.projection api
                        (Projection.single progress (fun message ->
                            match message with
                            | :? Event<TallyEvent> as event -> let (Tallied amount) = event.EventDetails in seen.Enqueue amount
                            | _ -> ()))
                for amount in events do
                    tallies.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "tally") (Tally amount) (fun _ -> true)
                    |> fun work -> Async.RunSynchronously(work, 20000)
                    |> ignore
                wait (projection.CatchUpAsync())
                List.ofSeq seen
            finally
                wait (api.Stop())
        try
            Expect.equal (run [ 1; 2 ] (Named "tallies")) [ 1; 2 ] "the first run handles both events"
            Expect.equal (run [ 3 ] (Named "tallies")) [ 3 ] "a named projection resumes after the events it handled"
            Expect.equal (run [] FromStart) [ 1; 2; 3 ] "FromStart reads the whole journal again"
            Expect.equal (run [] (Named "another")) [ 1; 2; 3 ] "a new name reads the whole journal once"
        finally
            deleteDatabases [ journalPath; projectionPath ]


let private transientJournalFailure =
    testCase "projection: a journal that cannot be read for a while delays the projection without stopping it"
    <| fun _ ->
        let suffix, journalPath, projectionPath = sqlitePaths ()
        let api =
            Fcqrs.actor (VerifySerialization.configuration().Build()) NullLoggerFactory.Instance
                (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite (sqliteString journalPath))) ("TransientJournal" + suffix)
        try
            let tallies = tallyAggregate api "TransientJournalTally"
            Fcqrs.wireSagaStarters api []
            let seen = Collections.Concurrent.ConcurrentQueue<int>()
            use projection =
                Fcqrs.projection api
                    (Projection.single FromStart (fun message ->
                        match message with
                        | :? Event<TallyEvent> as event -> let (Tallied amount) = event.EventDetails in seen.Enqueue amount
                        | _ -> ()))
            let tally amount =
                tallies.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "tally") (Tally amount) (fun _ -> true)
                |> fun work -> Async.RunSynchronously(work, 20000)
                |> ignore
            tally 1
            wait (projection.CatchUpAsync())
            let rename (from: string) (target: string) =
                use connection = new SqliteConnection(sqliteString journalPath)
                connection.Open()
                execute connection None $"ALTER TABLE {from} RENAME TO {target}" []
            // The journal cannot be read: background polls and this caller's wait fail.
            rename "journal" "journal_away"
            Expect.throws (fun () -> wait (projection.CatchUpAsync())) "a wait during the outage fails"
            Thread.Sleep 2500
            Expect.isFalse projection.Completion.IsCompleted "the projection keeps running through the outage"
            rename "journal_away" "journal"
            tally 2
            wait (projection.CatchUpAsync())
            Expect.equal (List.ofSeq seen) [ 1; 2 ] "the projection resumes after the outage"
        finally
            wait (api.Stop())
            deleteDatabases [ journalPath; projectionPath ]

let tests =
    let postgres =
        match Environment.GetEnvironmentVariable "FCQRS_TEST_POSTGRES" with
        | null | "" -> None
        | value -> Some value
    testSequenced (
        testList "transactional projection catch-up"
            [ testCase "SQLite: catch-up spans all aggregate histories" (fun _ -> allAggregates None)
              rollbackAndRecovery
              cancellationAndDispose
              deletedHistory
              fixedBoundary
              wrongJournalSource
              timeoutIsolation
              actorShutdown
              hostedProjectionInjection
              shardingBookkeeping
              causalNotification
              heldWaiterOrder
              observedTimeout
              nullCorrelationId
              namedProgress
              transientJournalFailure
              testCase "SQLite: competing projection instances do not double-apply" (fun _ -> concurrentInstances None)
              match postgres with
              | Some connection ->
                  testCase "PostgreSQL: catch-up spans all aggregate histories" (fun _ -> allAggregates postgres)
                  testCase "PostgreSQL: competing projection instances do not double-apply" (fun _ -> concurrentInstances postgres)
                  testCase "PostgreSQL: a late commit below the previous global offset is processed" (fun _ -> postgresCommitInversion connection)
                  testCase "PostgreSQL: a projection receives an event that commits after later-numbered ones" (fun _ -> postgresLateCommitProjection connection)
                  testCase "PostgreSQL: an older ambient snapshot cannot weaken catch-up" (fun _ -> postgresAmbientSnapshot connection)
              | None ->
                  ptestCase "PostgreSQL integration: set FCQRS_TEST_POSTGRES to run" ignore ])
