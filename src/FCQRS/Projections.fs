/// Transactional projections with a journal-wide, durable catch-up boundary.
module FCQRS.Projections

open System
open System.Data.Common
open System.Threading
open System.Threading.Tasks
open Akka.Persistence.Query
open Akka.Streams
open Akka.Streams.Dsl
open Microsoft.Extensions.Logging
open FCQRS.Common
open FCQRS.Model.Data
open FCQRS.ProjectionStorage

/// A transactional projection and its request-scoped notification subscriptions.
/// Catch-up covers every persistence ID in one committed journal snapshot. It does
/// not wait for other projections, later writes, or external effects.
type IProjection =
    inherit FCQRS.Query.ISubscribe
    inherit IDisposable
    /// Captures a fixed journal snapshot and waits for this projection to commit
    /// every event through it. Uses the configured CatchUpTimeout.
    abstract CatchUpAsync: unit -> Task
    /// Captures a fixed journal snapshot and waits for this projection to commit
    /// every event through it. Cancellation/timeout stops the caller's wait, not a
    /// transaction already being processed. Call after the aggregate persistence acknowledgement.
    /// The caller's ambient TransactionScope is suppressed; projection commits are independent.
    abstract CatchUpAsync: cancellationToken: CancellationToken -> Task
    /// Completes on disposal or actor-system shutdown; faults on a projection error.
    /// After an error, restart the projection after correcting its cause.
    abstract Completion: Task

/// Options for a named transactional projection. Use a distinct name for each read model.
/// Reusing a name resumes its stored progress; do not reuse it for an empty or different model.
[<Sealed>]
type TransactionalProjectionOptions(name: string, store: SqlProjectionStore) =
    do
        if String.IsNullOrWhiteSpace name then invalidArg (nameof name) "A projection name is required."
        if isNull (box store) then nullArg (nameof store)
    /// Stable identity of the read model and its durable progress.
    member _.Name = name
    /// Authoritative journal connection and transactional read-model store.
    member _.Store = store
    /// Delay between background journal-head queries, after the previous batch finishes.
    /// CatchUpAsync captures its own snapshot immediately. Default: one second.
    member val PollInterval = TimeSpan.FromSeconds 1.0 with get, set
    /// Maximum number of events fetched for one persistence ID per query. Default: 500.
    member val BatchSize = 500 with get, set
    /// Bound for the entire catch-up call, including snapshot capture. Default: 30 seconds.
    member val CatchUpTimeout = TimeSpan.FromSeconds 30.0 with get, set

/// Creates an independently running projection. The handler must write exclusively
/// through the supplied connection and transaction, and await all its database work.
/// FCQRS commits the handler's updates and contiguous journal position together.
/// Events are processed in sequence order within each persistence ID; there is no
/// cross-persistence-ID order. Retain journal history until it has been processed.
/// Configured Akka event adapters are currently unsupported by this projection reader.
/// Configure the SQL journal through HOCON; DataOptionsSetup overrides are rejected
/// because capture and replay must be validated against the same database and mapping.
let start
    (actor: IActor)
    (options: TransactionalProjectionOptions)
    (handler: DbConnection -> DbTransaction -> EventEnvelope -> Task)
    : IProjection =

    if isNull (box options) then nullArg (nameof options)
    if isNull (box handler) then nullArg (nameof handler)
    let validateDuration name (value: TimeSpan) =
        if value <= TimeSpan.Zero || value.TotalMilliseconds > float (UInt32.MaxValue - 1u) then
            invalidArg name "The duration must be positive and less than 49.7 days."
    validateDuration "PollInterval" options.PollInterval
    validateDuration "CatchUpTimeout" options.CatchUpTimeout
    if options.BatchSize < 1 then invalidArg "BatchSize" "BatchSize must be positive."
    FCQRS.EventUpcasting.Internal.freeze actor.System

    let name, store = options.Name, options.Store
    let interval, batchSize, timeout = options.PollInterval, int64 options.BatchSize, options.CatchUpTimeout
    let logger = actor.LoggerFactory.CreateLogger "TransactionalProjection"
    if actor.System.Settings.Setup.Get<Akka.Persistence.Sql.Config.DataOptionsSetup>().HasValue
       || actor.System.Settings.Setup.Get<Akka.Persistence.Sql.Config.MultiDataOptionsSetup>().HasValue then
        invalidArg "options" "Transactional projections require SQL journal settings in HOCON; DataOptionsSetup overrides cannot be validated."
    let config = actor.System.Settings.Config.WithFallback(Akka.Persistence.Sql.SqlPersistence.Get(actor.System).DefaultConfig)
    let notificationTimeout = CommandHandler.Internal.resolveCommandTimeout config
    let writePlugin = config.GetString("akka.persistence.journal.plugin")
    if writePlugin <> "akka.persistence.journal.sql" then
        invalidArg "options" "Transactional projections require the Akka.Persistence.Sql write journal."
    let adapters = config.GetConfig(writePlugin + ".event-adapters")
    if not (isNull adapters) && not adapters.IsEmpty then
        invalidArg "options" "Transactional projections currently require an identity journal reader (no Akka event-adapters)."

    let readConfig = config.GetConfig(Akka.Persistence.Sql.Query.SqlReadJournal.Identifier)
    let readerWritePlugin = readConfig.GetString("write-plugin", "")
    if not (String.IsNullOrEmpty readerWritePlugin) && readerWritePlugin <> writePlugin then
        invalidArg "options" "The SQL query journal must use the active write journal and its event adapters."
    let mapping = readConfig.GetString("table-mapping", "default")
    let schema = readConfig.GetString(mapping + ".schema-name", null) |> Option.ofObj
    let provider = readConfig.GetString("provider-name", "")
    if (store.Dialect = ProjectionSqlDialect.Sqlite && not (provider.StartsWith("SQLite", StringComparison.OrdinalIgnoreCase)))
       || (store.Dialect = ProjectionSqlDialect.PostgreSql && not (provider.StartsWith("PostgreSQL", StringComparison.OrdinalIgnoreCase))) then
        invalidArg "options" "The projection store dialect must match the actor's SQL journal provider."
    store.ValidateJournal(
        readConfig.GetString("connection-string", ""),
        readConfig.GetString(mapping + ".journal.table-name", "journal"), schema,
        readConfig.GetString(mapping + ".journal.columns.persistence-id", "persistence_id"),
        readConfig.GetString(mapping + ".journal.columns.sequence-number", "sequence_number"))

    let writeConfig = config.GetConfig(writePlugin)
    let writeProvider = writeConfig.GetString("provider-name", "")
    if not (String.Equals(provider, writeProvider, StringComparison.OrdinalIgnoreCase)) then
        invalidArg "options" "The SQL read and write journal providers must match."
    let writeMapping = writeConfig.GetString("table-mapping", "default")
    store.ValidateJournal(
        writeConfig.GetString("connection-string", ""),
        writeConfig.GetString(writeMapping + ".journal.table-name", "journal"),
        writeConfig.GetString(writeMapping + ".schema-name", null) |> Option.ofObj,
        writeConfig.GetString(writeMapping + ".journal.columns.persistence-id", "persistence_id"),
        writeConfig.GetString(writeMapping + ".journal.columns.sequence-number", "sequence_number"))

    let journal = FCQRS.Query.Internal.readJournal actor.System
    let gate = new SemaphoreSlim(1, 1)
    let lifetime = new CancellationTokenSource()
    let completion = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let errorGate = obj ()
    let mutable failure: exn option = None
    let mutable stopped = 0
    let notifications =
        FCQRS.Query.Internal.NotificationHub<IMessageWithCID>(
            max 1 (config.GetInt("akka.fcqrs.notification-buffer", 1024)), logger, notificationTimeout)
    let subscriptions = FCQRS.Query.asDefaultSubscribe (notifications :> FCQRS.Query.ISubscribe<IMessageWithCID>)

    let fail error =
        lock errorGate (fun () ->
            if failure.IsNone then
                failure <- Some error
                logger.LogError(error, "Transactional projection {Projection} stopped", name)
                completion.TrySetException(error) |> ignore)
        lifetime.Cancel()
        notifications.Stop()

    let checkRunning () =
        match lock errorGate (fun () -> failure) with
        | Some error -> raise (InvalidOperationException($"Projection '{name}' has stopped after an error.", error))
        | None -> lifetime.Token.ThrowIfCancellationRequested()

    let apply (envelope: EventEnvelope) = task {
        use! connection = store.OpenProjectionAsync(lifetime.Token)
        use! transaction = connection.BeginTransactionAsync(lifetime.Token)
        do! store.LockProjectionAsync(connection, transaction, name, lifetime.Token)
        let! position = store.ReadPositionAsync(connection, transaction, name, envelope.PersistenceId, lifetime.Token)
        if envelope.SequenceNr > position then
            if position = Int64.MaxValue || envelope.SequenceNr <> position + 1L then
                invalidOp $"Projection '{name}' found a journal gap for '{envelope.PersistenceId}' after sequence {position}. Retain or restore its journal history."
            let event = FCQRS.EventUpcasting.Internal.upcastEvent actor.System envelope.Event
            let envelope =
                if obj.ReferenceEquals(event, envelope.Event) then envelope
                else EventEnvelope(envelope.Offset, envelope.PersistenceId, envelope.SequenceNr, event, envelope.Timestamp, envelope.Tags)
            do! handler connection transaction envelope
            do! store.WritePositionAsync(connection, transaction, name, envelope.PersistenceId, position, envelope.SequenceNr, lifetime.Token)
            do! transaction.CommitAsync(lifetime.Token)
            match envelope.Event with
            | :? IMessageWithCID as event -> notifications.Publish event
            | _ -> ()
            return envelope.SequenceNr
        else
            return position
    }

    let readBatch persistenceId first last = task {
        let source = journal.CurrentEventsByPersistenceId(persistenceId, first, last)
        let running =
            source
                .ViaMaterialized(KillSwitches.Single<EventEnvelope>(), Func<_, _, _>(fun _ kill -> kill))
                .ToMaterialized(Sink.Seq<EventEnvelope>(), Func<_, _, _>(fun kill result -> kill, result))
                .Run(actor.Materializer)
        let killSwitch, result = running
        use registration = lifetime.Token.Register(fun () -> killSwitch.Abort(OperationCanceledException(lifetime.Token)))
        return! result
    }

    let processTargets (targets: Map<string, int64>) = task {
        let! positions = store.ReadPositionsAsync(name, lifetime.Token)
        for KeyValue(persistenceId, target) in targets do
            let mutable position = positions |> Map.tryFind persistenceId |> Option.defaultValue 0L
            while position < target do
                lifetime.Token.ThrowIfCancellationRequested()
                let last = position + min batchSize (target - position)
                let! events = readBatch persistenceId (position + 1L) last
                let mutable lastRead = position
                for envelope in events do
                    if envelope.PersistenceId <> persistenceId || lastRead = Int64.MaxValue || envelope.SequenceNr <> lastRead + 1L then
                        invalidOp $"Projection '{name}' found a noncontiguous journal event after sequence {lastRead} for '{persistenceId}'. Missing history and expanding event adapters cannot be checkpointed."
                    let! committed = apply envelope
                    position <- max position committed
                    lastRead <- envelope.SequenceNr
                if lastRead < last then
                    invalidOp $"Projection '{name}' could not read journal sequence {lastRead + 1L} for '{persistenceId}'. Retain or restore its journal history."
    }

    let suppressAmbient () =
        new System.Transactions.TransactionScope(
            System.Transactions.TransactionScopeOption.Suppress,
            System.Transactions.TransactionScopeAsyncFlowOption.Enabled)

    let initialize = Task.Run(Func<Task>(fun () -> task {
        use ambient = suppressAmbient ()
        do! store.InitializeAsync(lifetime.Token)
    }))

    let processSerialized targets (waitToken: CancellationToken) = task {
        do! gate.WaitAsync(waitToken)
        try
            checkRunning ()
            do! processTargets targets
        finally
            gate.Release() |> ignore
    }

    let background = Task.Run(Func<Task>(fun () -> task {
        use ambient = suppressAmbient ()
        try
            do! initialize
            while not lifetime.IsCancellationRequested do
                let! targets = store.CaptureAsync(lifetime.Token)
                do! processSerialized targets lifetime.Token
                do! Task.Delay(interval, lifetime.Token)
        with
        | :? OperationCanceledException when lifetime.IsCancellationRequested -> ()
        | error -> fail error
        do! gate.WaitAsync()
        gate.Release() |> ignore
        if (lock errorGate (fun () -> failure.IsNone)) then completion.TrySetResult(()) |> ignore
    }))

    let dispose () =
        if Interlocked.Exchange(&stopped, 1) = 0 then
            lifetime.Cancel()
            notifications.Stop()
    actor.System.RegisterOnTermination(Action dispose)
    Akka.Actor.CoordinatedShutdown.Get(actor.System).AddTask(
        Akka.Actor.CoordinatedShutdown.PhaseBeforeActorSystemTerminate,
        "fcqrs-projection-" + Guid.NewGuid().ToString("N"),
        Func<Task<Akka.Done>>(fun () ->
            dispose ()
            Task.FromResult(Akka.Done.Instance)))

    let catchUp (ct: CancellationToken) : Task =
        task {
            // A caller's ambient snapshot may predate the aggregate acknowledgement.
            // Capture and processing must use fresh, independently committed transactions.
            use ambient = suppressAmbient ()
            ct.ThrowIfCancellationRequested()
            checkRunning ()
            use bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token)
            bounded.CancelAfter timeout
            let waitToken = bounded.Token
            try
                do! initialize.WaitAsync(waitToken)
                // Microsoft.Data.Sqlite executes its async ADO.NET calls synchronously.
                // Keep snapshot acquisition off the calling thread and bound the wait.
                let capture = Task.Run<Map<string, int64>>(Func<Task<Map<string, int64>> | null>(fun () -> store.CaptureAsync(waitToken)), waitToken)
                let! targets = capture.WaitAsync(waitToken)
                let work = task {
                    try
                        do! processSerialized targets waitToken
                    with
                    | :? OperationCanceledException as error when waitToken.IsCancellationRequested -> return raise error
                    | error ->
                        fail error
                        return raise error
                }
                work.ContinueWith((fun (t: Task) -> if t.IsFaulted then t.Exception |> ignore), TaskScheduler.Default) |> ignore
                do! work.WaitAsync(waitToken)
                checkRunning ()
            with
            | :? OperationCanceledException as error ->
                match lock errorGate (fun () -> failure) with
                | Some cause -> return raise (InvalidOperationException($"Projection '{name}' has stopped after an error.", cause))
                | None when not ct.IsCancellationRequested && not lifetime.IsCancellationRequested ->
                    return raise (TimeoutException($"Projection '{name}' did not catch up within {timeout}.", error :> exn | null))
                | None -> return raise error
        } :> Task

    background.ContinueWith((fun (t: Task) ->
        match t.Exception with
        | null -> ()
        | error -> fail error), TaskScheduler.Default) |> ignore
    completion.Task.ContinueWith((fun (t: Task) -> if t.IsFaulted then t.Exception |> ignore), TaskScheduler.Default) |> ignore

    { new IProjection with
        member _.CatchUpAsync() = catchUp CancellationToken.None
        member _.CatchUpAsync(ct) = catchUp ct
        member _.Completion = completion.Task
        member _.Dispose() = dispose ()
        member _.Subscribe(callback, ?cancellationToken) = subscriptions.Subscribe(callback, ?cancellationToken = cancellationToken)
        member _.Subscribe(filter: IMessageWithCID -> bool, take, ?callback, ?cancellationToken) = subscriptions.Subscribe(filter, take, ?callback = callback, ?cancellationToken = cancellationToken)
        member _.Subscribe(cid: CID, take, ?callback, ?cancellationToken) = subscriptions.Subscribe(cid, take, ?callback = callback, ?cancellationToken = cancellationToken)
        member _.Subscribe(cid: CID, filter: IMessageWithCID -> bool, take, ?callback, ?cancellationToken) = subscriptions.Subscribe(cid, filter, take, ?callback = callback, ?cancellationToken = cancellationToken)

      interface FCQRS.Query.IHasNotificationTimeout with
        member _.Timeout = notificationTimeout }
