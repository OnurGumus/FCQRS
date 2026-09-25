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

/// A running projection and its request-scoped notification subscriptions.
/// Catch-up covers every application persistence ID in one committed journal snapshot.
/// Akka's own cluster-sharding records (IDs starting with "/sharding/") are excluded.
/// It does not wait for other projections, later writes, or external effects.
/// A correlation-ID subscription (`Subscribe(cid, ...)`, which `sendAwaiting` uses) receives
/// its notifications after the whole snapshot containing them commits, so it can wait longer
/// than the commit of its own event. If the projection fails before then, the subscription is
/// cancelled. Other subscriptions receive each notification when its event commits.
type IProjection =
    inherit FCQRS.Query.ISubscribe
    inherit IDisposable
    /// Captures a fixed journal snapshot and waits for this projection to commit
    /// every event through it. Uses the configured CatchUpTimeout. A write that committed more
    /// than LateWriteWindow after taking its journal number can be handled after this returns,
    /// by the next full scan.
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
    /// Delay between background journal-head queries, after the previous batch finishes. An
    /// aggregate on this node that stores an event starts the next query at once, and
    /// CatchUpAsync captures its own snapshot immediately. Default: one second.
    member val PollInterval = TimeSpan.FromSeconds 1.0 with get, set
    /// Maximum number of events fetched for one persistence ID per query. Default: 500.
    member val BatchSize = 500 with get, set
    /// Bound for the entire catch-up call, including snapshot capture. Default: 30 seconds.
    member val CatchUpTimeout = TimeSpan.FromSeconds 30.0 with get, set
    /// How long after taking its journal number a write may commit and still be found by the next
    /// query, which reads only recent writes. A write that commits later is found by the next full
    /// scan. Default: 30 seconds.
    member val LateWriteWindow = TimeSpan.FromSeconds 30.0 with get, set
    /// How often a query reads every persistence ID's position instead of only recent writes. The
    /// full scan also finds a write that committed after LateWriteWindow. Default: five minutes.
    member val FullScanInterval = TimeSpan.FromMinutes 5.0 with get, set

/// Journal history a projection needs is missing or out of order, for example after journal rows
/// were deleted. Reading again cannot repair it: restore the history or rebuild the read model.
type JournalHistoryException(message: string) =
    inherit InvalidOperationException(message)

// The journal settings both projection kinds read and validate.
type private JournalSettings =
    { Config: Akka.Configuration.Config
      Provider: string
      ConnectionString: string
      Table: string
      Schema: string option
      PersistenceIdColumn: string
      SequenceNumberColumn: string
      OrderingColumn: string
      WriteConfig: Akka.Configuration.Config
      WriteMapping: string }

let private journalSettings (actor: IActor) (allowAdapters: bool) =
    if actor.System.Settings.Setup.Get<Akka.Persistence.Sql.Config.DataOptionsSetup>().HasValue
       || actor.System.Settings.Setup.Get<Akka.Persistence.Sql.Config.MultiDataOptionsSetup>().HasValue then
        invalidArg "options" "Projections require SQL journal settings in HOCON; DataOptionsSetup overrides cannot be validated."
    let config = actor.System.Settings.Config.WithFallback(Akka.Persistence.Sql.SqlPersistence.Get(actor.System).DefaultConfig)
    let writePlugin = config.GetString("akka.persistence.journal.plugin")
    if writePlugin <> "akka.persistence.journal.sql" then
        invalidArg "options" "Projections require the Akka.Persistence.Sql write journal."
    let adapters = config.GetConfig(writePlugin + ".event-adapters")
    if not allowAdapters && not (isNull adapters) && not adapters.IsEmpty then
        invalidArg "options" "Transactional projections currently require an identity journal reader (no Akka event-adapters)."
    let readConfig = config.GetConfig(Akka.Persistence.Sql.Query.SqlReadJournal.Identifier)
    let readerWritePlugin = readConfig.GetString("write-plugin", "")
    if not (String.IsNullOrEmpty readerWritePlugin) && readerWritePlugin <> writePlugin then
        invalidArg "options" "The SQL query journal must use the active write journal and its event adapters."
    let mapping = readConfig.GetString("table-mapping", "default")
    let provider = readConfig.GetString("provider-name", "")
    let writeConfig = config.GetConfig(writePlugin)
    if not (String.Equals(provider, writeConfig.GetString("provider-name", ""), StringComparison.OrdinalIgnoreCase)) then
        invalidArg "options" "The SQL read and write journal providers must match."
    { Config = config
      Provider = provider
      ConnectionString = readConfig.GetString("connection-string", "")
      Table = readConfig.GetString(mapping + ".journal.table-name", "journal")
      Schema = readConfig.GetString(mapping + ".schema-name", null) |> Option.ofObj
      PersistenceIdColumn = readConfig.GetString(mapping + ".journal.columns.persistence-id", "persistence_id")
      SequenceNumberColumn = readConfig.GetString(mapping + ".journal.columns.sequence-number", "sequence_number")
      OrderingColumn = readConfig.GetString(mapping + ".journal.columns.ordering", "ordering")
      WriteConfig = writeConfig
      WriteMapping = writeConfig.GetString("table-mapping", "default") }

// What distinguishes the two projection kinds: where positions live, and what applying an event means.
type private Tracking =
    { /// Names the projection in logs and errors.
      Name: string
      /// Retries a failed journal read or progress write with backoff instead of stopping, and
      /// terminates the process on missing journal history. Otherwise any error stops the projection.
      Resilient: bool
      Initialize: CancellationToken -> Task
      /// Each persistence ID's last sequence number in one committed journal snapshot, and the
      /// highest global journal number read.
      CaptureAll: CancellationToken -> Task<Map<string, int64> * int64>
      /// The same for persistence IDs with events numbered above the given global number.
      CaptureSince: int64 -> CancellationToken -> Task<Map<string, int64> * int64>
      /// Positions of every persistence ID, or of the ones given.
      ReadPositions: string list option -> CancellationToken -> Task<Map<string, int64>>
      /// Applies an event that is next for its persistence ID, publishes its notifications
      /// through the function given, and returns the persistence ID's position afterwards.
      Apply: (IMessageWithCID -> unit) -> EventEnvelope -> CancellationToken -> Task<int64> }

// One query of the journal: the persistence IDs it found with their last sequence numbers, whether
// it read every persistence ID, the highest global journal number it read, and when it ran.
type private Pass =
    { Targets: Map<string, int64>
      Full: bool
      Highest: int64
      Read: DateTime }

// Reads the journal per persistence ID, from each one's position to the captured snapshot, so a
// write that commits after later-numbered writes is read on the next pass instead of skipped.
let private run (actor: IActor) (logger: ILogger) (settings: JournalSettings)
                (options: TransactionalProjectionOptions) (tracking: Tracking) : IProjection =
    let name = tracking.Name
    let interval, batchSize, timeout = options.PollInterval, int64 options.BatchSize, options.CatchUpTimeout
    let notificationTimeout = CommandHandler.Internal.resolveCommandTimeout settings.Config
    let journal = FCQRS.Query.Internal.readJournal actor.System
    let gate = new SemaphoreSlim(1, 1)
    let lifetime = new CancellationTokenSource()
    let completion = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let errorGate = obj ()
    let mutable failure: exn option = None
    let mutable stopped = 0
    let notifications =
        FCQRS.Query.Internal.NotificationHub<IMessageWithCID>(
            max 1 (settings.Config.GetInt("akka.fcqrs.notification-buffer", 1024)), logger, notificationTimeout)
    let subscriptions = FCQRS.Query.asDefaultSubscribe (notifications :> FCQRS.Query.ISubscribe<IMessageWithCID>)
    // An aggregate on this node stored an event: read the journal now instead of at the next poll.
    let wake = new SemaphoreSlim(0, 1)
    let stored =
        JournalActivity.listen actor.System (fun () ->
            try
                if wake.CurrentCount = 0 then wake.Release() |> ignore
            with
            | :? SemaphoreFullException
            | :? ObjectDisposedException -> ())

    let fail error =
        lock errorGate (fun () ->
            if failure.IsNone then
                failure <- Some error
                logger.LogError(error, "Projection {Projection} stopped", name)
                completion.TrySetException(error) |> ignore)
        lifetime.Cancel()
        notifications.Stop()

    let checkRunning () =
        match lock errorGate (fun () -> failure) with
        | Some error -> raise (InvalidOperationException($"Projection '{name}' has stopped after an error.", error))
        | None -> lifetime.Token.ThrowIfCancellationRequested()

    // Missing history cannot be read again; a resilient projection would otherwise retry forever.
    let fatal (error: exn) =
        logger.LogCritical(error, "Projection {Projection} cannot continue: its journal history is missing.", name)
        fatalFailFast null "Process terminated because a projection's journal history is missing" error

    let transient (error: exn) =
        tracking.Resilient && not (error :? JournalHistoryException) && not lifetime.IsCancellationRequested

    // 1 s, doubling to 30 s, plus up to 20 percent random delay.
    let retryAfter (failures: int) =
        let seconds = min 30.0 (Math.Pow(2.0, float (failures - 1)))
        TimeSpan.FromSeconds(seconds * (1.0 + Random.Shared.NextDouble() * 0.2))

    // Most queries read only writes numbered above the highest number handled LateWriteWindow
    // ago. Numbers are taken in time order, so a write that commits within the window is numbered
    // above it. A full scan runs until one succeeds, once more a window later for writes already
    // under way at start, and every FullScanInterval, which finds any write that committed later
    // than the window. A query's number counts only once its pass has handled every event it found.
    let captureGate = obj ()
    let mutable lastFullScan: DateTime option = None
    let mutable startFollowUp = true
    // (time of a query whose pass completed, highest number it read), oldest first.
    let handled = Collections.Generic.List<DateTime * int64>()

    let capture (token: CancellationToken) = task {
        let now = DateTime.UtcNow
        let after =
            lock captureGate (fun () ->
                match lastFullScan with
                | None -> None
                | Some full when now - full >= options.FullScanInterval -> None
                | Some full when startFollowUp && now - full >= options.LateWriteWindow -> None
                | Some _ ->
                    let cutoff = now - options.LateWriteWindow
                    match handled |> Seq.filter (fun (time, _) -> time <= cutoff) |> Seq.tryLast with
                    | Some(_, highest) -> Some highest
                    // Within a window of the first full scan: read everything above it.
                    | None -> Some(snd handled[0]))
        let! targets, highest =
            match after with
            | None -> tracking.CaptureAll token
            | Some after -> tracking.CaptureSince after token
        return { Targets = targets; Full = after.IsNone; Highest = highest; Read = DateTime.UtcNow }
    }

    let completed (pass: Pass) =
        lock captureGate (fun () ->
            if pass.Full then
                if lastFullScan.IsSome then startFollowUp <- false
                lastFullScan <- Some(max pass.Read (defaultArg lastFullScan pass.Read))
            let index = handled.FindLastIndex(fun (time, _) -> time <= pass.Read) + 1
            let before = if index > 0 then snd handled[index - 1] else 0L
            handled.Insert(index, (pass.Read, max pass.Highest before))
            for later in index + 1 .. handled.Count - 1 do
                let time, highest = handled[later]
                handled[later] <- (time, max highest pass.Highest)
            // Keep the newest entry at least a window old, and everything after it.
            let older = handled.FindLastIndex(fun (time, _) -> time <= DateTime.UtcNow - options.LateWriteWindow)
            if older > 0 then handled.RemoveRange(0, older))

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

    let processTargets (targets: Map<string, int64>) (full: bool) = task {
        let! positions =
            if full then tracking.ReadPositions None lifetime.Token
            elif targets.IsEmpty then Task.FromResult Map.empty
            else tracking.ReadPositions (Some [ for KeyValue(persistenceId, _) in targets -> persistenceId ]) lifetime.Token
        // Persistence IDs are processed in key order, not causal order, so a saga's follow-up
        // event can commit before the originator event that caused it. Both share a correlation
        // ID, and a snapshot holding the follow-up also holds its cause. Correlation-ID waiters
        // therefore receive their notifications only after the whole snapshot commits, so
        // whichever one wakes them, the events that caused it are already readable. Subscribers
        // without a correlation ID still receive every notification as its event commits.
        let held = ResizeArray<unit -> unit>()
        let publish message = notifications.PublishExceptWaiters message |> Option.iter held.Add
        for KeyValue(persistenceId, target) in targets do
            let mutable position = positions |> Map.tryFind persistenceId |> Option.defaultValue 0L
            while position < target do
                lifetime.Token.ThrowIfCancellationRequested()
                let last = position + min batchSize (target - position)
                let! events = readBatch persistenceId (position + 1L) last
                let mutable lastRead = position
                for envelope in events do
                    if envelope.PersistenceId <> persistenceId || lastRead = Int64.MaxValue || envelope.SequenceNr <> lastRead + 1L then
                        raise (JournalHistoryException $"Projection '{name}' found a noncontiguous journal event after sequence {lastRead} for '{persistenceId}'. Missing history and expanding event adapters cannot be checkpointed.")
                    let! committed = tracking.Apply publish envelope lifetime.Token
                    position <- max position committed
                    lastRead <- envelope.SequenceNr
                if lastRead < last then
                    raise (JournalHistoryException $"Projection '{name}' could not read journal sequence {lastRead + 1L} for '{persistenceId}'. Retain or restore its journal history, and make every event adapter return exactly one event.")
        for deliver in held do
            deliver ()
    }

    let suppressAmbient () =
        new System.Transactions.TransactionScope(
            System.Transactions.TransactionScopeOption.Suppress,
            System.Transactions.TransactionScopeAsyncFlowOption.Enabled)

    let initialize = Task.Run(Func<Task>(fun () -> task {
        use ambient = suppressAmbient ()
        let mutable failures = 0
        let mutable ready = false
        while not ready do
            let! failed = task {
                try
                    do! tracking.Initialize lifetime.Token
                    return None
                with error when transient error ->
                    return Some error
            }
            match failed with
            | None -> ready <- true
            | Some error ->
                failures <- failures + 1
                let delay = retryAfter failures
                logger.LogError(error, "Projection {Projection} could not prepare its progress store; retrying in {Delay}.", name, delay)
                do! Task.Delay(delay, lifetime.Token)
    }))

    let processSerialized (pass: Pass) (waitToken: CancellationToken) = task {
        do! gate.WaitAsync(waitToken)
        try
            checkRunning ()
            do! processTargets pass.Targets pass.Full
            completed pass
        finally
            gate.Release() |> ignore
    }

    let background = Task.Run(Func<Task>(fun () -> task {
        use ambient = suppressAmbient ()
        try
            do! initialize
            let mutable failures = 0
            while not lifetime.IsCancellationRequested do
                let! failed = task {
                    try
                        let! targets = capture lifetime.Token
                        do! processSerialized targets lifetime.Token
                        return None
                    with error when transient error ->
                        return Some error
                }
                match failed with
                | None ->
                    failures <- 0
                    let! _ = wake.WaitAsync(interval, lifetime.Token)
                    ()
                | Some error ->
                    failures <- failures + 1
                    let delay = retryAfter failures
                    logger.LogError(error, "Projection {Projection} could not read the journal or store its progress; retrying in {Delay}.", name, delay)
                    do! Task.Delay(delay, lifetime.Token)
        with
        | :? OperationCanceledException when lifetime.IsCancellationRequested -> ()
        | :? JournalHistoryException as error when tracking.Resilient -> fatal error
        | error -> fail error
        do! gate.WaitAsync()
        gate.Release() |> ignore
        if (lock errorGate (fun () -> failure.IsNone)) then completion.TrySetResult(()) |> ignore
    }))

    let dispose () =
        if Interlocked.Exchange(&stopped, 1) = 0 then
            stored.Dispose()
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
                let captured = Task.Run<Pass>(Func<Task<Pass> | null>(fun () -> capture waitToken), waitToken)
                let! targets = captured.WaitAsync(waitToken)
                let work = task {
                    try
                        do! processSerialized targets waitToken
                    with
                    | :? OperationCanceledException as error when waitToken.IsCancellationRequested -> return raise error
                    | :? JournalHistoryException as error when tracking.Resilient ->
                        fatal error
                        return raise error
                    // The caller's wait fails; the projection keeps running and retries.
                    | error when transient error -> return raise error
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

let private upcastEnvelope (actor: IActor) (envelope: EventEnvelope) =
    let event = FCQRS.EventUpcasting.Internal.upcastEvent actor.System envelope.Event
    if obj.ReferenceEquals(event, envelope.Event) then envelope
    else EventEnvelope(envelope.Offset, envelope.PersistenceId, envelope.SequenceNr, event, envelope.Timestamp, envelope.Tags)

let private gap name (envelope: EventEnvelope) (position: int64) =
    if position = Int64.MaxValue || envelope.SequenceNr <> position + 1L then
        raise (JournalHistoryException $"Projection '{name}' found a journal gap for '{envelope.PersistenceId}' after sequence {position}. Retain or restore its journal history, and make every event adapter return exactly one event.")

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
    validateDuration "LateWriteWindow" options.LateWriteWindow
    validateDuration "FullScanInterval" options.FullScanInterval
    if options.BatchSize < 1 then invalidArg "BatchSize" "BatchSize must be positive."
    FCQRS.EventUpcasting.Internal.freeze actor.System

    let name, store = options.Name, options.Store
    let logger = actor.LoggerFactory.CreateLogger "TransactionalProjection"
    let settings = journalSettings actor false
    if (store.Dialect = ProjectionSqlDialect.Sqlite && not (settings.Provider.StartsWith("SQLite", StringComparison.OrdinalIgnoreCase)))
       || (store.Dialect = ProjectionSqlDialect.PostgreSql && not (settings.Provider.StartsWith("PostgreSQL", StringComparison.OrdinalIgnoreCase))) then
        invalidArg "options" "The projection store dialect must match the actor's SQL journal provider."
    store.ValidateJournal(
        settings.ConnectionString, settings.Table, settings.Schema,
        settings.PersistenceIdColumn, settings.SequenceNumberColumn, settings.OrderingColumn)
    let writeConfig, writeMapping = settings.WriteConfig, settings.WriteMapping
    store.ValidateJournal(
        writeConfig.GetString("connection-string", ""),
        writeConfig.GetString(writeMapping + ".journal.table-name", "journal"),
        writeConfig.GetString(writeMapping + ".schema-name", null) |> Option.ofObj,
        writeConfig.GetString(writeMapping + ".journal.columns.persistence-id", "persistence_id"),
        writeConfig.GetString(writeMapping + ".journal.columns.sequence-number", "sequence_number"),
        writeConfig.GetString(writeMapping + ".journal.columns.ordering", "ordering"))

    let apply (publish: IMessageWithCID -> unit) (envelope: EventEnvelope) (token: CancellationToken) = task {
        use! connection = store.OpenProjectionAsync(token)
        use! transaction = connection.BeginTransactionAsync(token)
        do! store.LockProjectionAsync(connection, transaction, name, token)
        let! position = store.ReadPositionAsync(connection, transaction, name, envelope.PersistenceId, token)
        if envelope.SequenceNr > position then
            gap name envelope position
            let envelope = upcastEnvelope actor envelope
            do! handler connection transaction envelope
            do! store.WritePositionAsync(connection, transaction, name, envelope.PersistenceId, position, envelope.SequenceNr, token)
            do! transaction.CommitAsync(token)
            match envelope.Event with
            | :? IMessageWithCID as event -> publish event
            | _ -> ()
            return envelope.SequenceNr
        else
            return position
    }

    run actor logger settings options
        { Name = name
          Resilient = false
          Initialize = store.InitializeAsync
          CaptureAll = store.CaptureAllAsync
          CaptureSince = fun after token -> store.CaptureSinceAsync(after, token)
          ReadPositions = fun ids token -> store.ReadPositionsAsync(name, ids, token)
          Apply = apply }

/// Where a tracked projection keeps how far it has read.
type internal TrackedProgress =
    /// In memory: every start reads the whole journal again.
    | InMemory
    /// In the journal database under this name: a start resumes where the last one stopped.
    | Stored of string

/// Creates a projection that follows each persistence ID's own sequence numbers, so it never skips
/// an event that commits after later-numbered ones, and hands each event to `handler`. Events are
/// processed in sequence order within each persistence ID; there is no cross-persistence-ID order.
/// With stored progress, a handler can see an event again after a crash: its position is stored
/// after the handler returns. A handler that throws terminates the process. The journal must be
/// SQLite or PostgreSQL, configured through HOCON.
let internal startTracked (actor: IActor) (progress: TrackedProgress) (handler: obj -> IMessageWithCID list) : IProjection =
    if isNull (box handler) then nullArg (nameof handler)
    match progress with
    | Stored name when String.IsNullOrWhiteSpace name -> invalidArg (nameof progress) "A stored projection needs a name."
    | _ -> ()
    FCQRS.EventUpcasting.Internal.freeze actor.System
    let logger = actor.LoggerFactory.CreateLogger "Projection"
    // An Akka event adapter must turn each journal row into exactly one event; the sequence
    // checks stop the projection when one returns several events or none.
    let settings = journalSettings actor true
    let dialect =
        if settings.Provider.StartsWith("SQLite", StringComparison.OrdinalIgnoreCase) then ProjectionSqlDialect.Sqlite
        elif settings.Provider.StartsWith("PostgreSQL", StringComparison.OrdinalIgnoreCase) then ProjectionSqlDialect.PostgreSql
        else invalidArg (nameof actor) $"Projections require a SQLite or PostgreSQL journal; this one uses '{settings.Provider}'."
    // The connections Akka.Persistence.Sql makes: the same provider and connection string.
    let provider =
        match LinqToDB.Data.DataConnection.GetDataProvider(settings.Provider, settings.ConnectionString) with
        | null -> invalidArg (nameof actor) $"No data provider is available for the journal provider '{settings.Provider}'."
        | provider -> provider
    let connect = Func<DbConnection>(fun () -> provider.CreateConnection settings.ConnectionString)
    let store =
        SqlProjectionStore(
            dialect, connect, connect,
            journalTable = settings.Table,
            ?journalSchema = settings.Schema,
            ?progressSchema = settings.Schema,
            journalPersistenceIdColumn = settings.PersistenceIdColumn,
            journalSequenceNumberColumn = settings.SequenceNumberColumn,
            journalOrderingColumn = settings.OrderingColumn)

    let handle (envelope: EventEnvelope) =
        let envelope = upcastEnvelope actor envelope
        try
            use activity = FCQRS.Query.projectionActivity envelope.Event envelope.SequenceNr
            handler envelope.Event
        with error ->
            logger.LogCritical(error, "Error in query handler")
            // FailFast, not Exit: Exit runs ProcessExit handlers, which can hang.
            fatalFailFast null "Process terminated due to query projection error" error
            failwith "unreachable"

    let tracking =
        match progress with
        | InMemory ->
            let positions = System.Collections.Concurrent.ConcurrentDictionary<string, int64>()
            { Name = "in-memory"
              Resilient = true
              Initialize = fun _ -> Task.CompletedTask
              CaptureAll = store.CaptureAllAsync
              CaptureSince = fun after token -> store.CaptureSinceAsync(after, token)
              ReadPositions =
                fun ids _ ->
                    match ids with
                    | None -> positions |> Seq.map (fun pair -> pair.Key, pair.Value) |> Map.ofSeq
                    | Some ids ->
                        ids
                        |> List.choose (fun id -> match positions.TryGetValue id with | true, position -> Some(id, position) | _ -> None)
                        |> Map.ofList
                    |> Task.FromResult
              Apply =
                fun publish envelope _ ->
                    let position =
                        match positions.TryGetValue envelope.PersistenceId with
                        | true, position -> position
                        | _ -> 0L
                    if envelope.SequenceNr > position then
                        gap "in-memory" envelope position
                        handle envelope |> List.iter publish
                        positions[envelope.PersistenceId] <- envelope.SequenceNr
                        Task.FromResult envelope.SequenceNr
                    else
                        Task.FromResult position }
        | Stored name ->
            { Name = name
              Resilient = true
              Initialize = store.InitializeAsync
              CaptureAll = store.CaptureAllAsync
              CaptureSince = fun after token -> store.CaptureSinceAsync(after, token)
              ReadPositions = fun ids token -> store.ReadPositionsAsync(name, ids, token)
              Apply =
                fun publish envelope token -> task {
                    use! connection = store.OpenProjectionAsync(token)
                    use! transaction = connection.BeginTransactionAsync(token)
                    do! store.LockProjectionAsync(connection, transaction, name, token)
                    let! position = store.ReadPositionAsync(connection, transaction, name, envelope.PersistenceId, token)
                    if envelope.SequenceNr > position then
                        gap name envelope position
                        let notifications = handle envelope
                        do! store.WritePositionAsync(connection, transaction, name, envelope.PersistenceId, position, envelope.SequenceNr, token)
                        do! transaction.CommitAsync(token)
                        notifications |> List.iter publish
                        return envelope.SequenceNr
                    else
                        return position
                } }

    run actor logger settings (TransactionalProjectionOptions(tracking.Name, store)) tracking
