module FCQRS.Query

open Akka.Persistence.Query
open Akkling.Streams
open Akka.Streams
open Akka.Streams.Dsl
open Microsoft.Extensions.Logging
open Common
open System.Diagnostics
open System.Threading
open System
open System.Threading.Tasks
open System.Threading.Channels
open System.Collections.Generic

type IAwaitable =
    abstract member Task: Task

/// A subscription whose Task succeeds after the requested callbacks complete. Cancellation, disposal, or projection shutdown before that count
/// cancels the Task; a filter or callback exception faults it.
type IAwaitableDisposable =
    inherit IDisposable
    inherit IAwaitable

open FCQRS.Model.Data

[<Interface>]
type ISubscribe<'TDataEvent when 'TDataEvent :> IMessageWithCID> =
    /// <summary>
    /// Subscribes to all events and invokes the specified callback for each event.
    /// Registration is complete when this method returns. Callbacks run in order
    /// on a separate worker; a full subscriber queue drops its oldest notification.
    /// </summary>
    /// <param name="callback">Function invoked for each event, e.g. printing or processing the event.</param>
    /// <param name="cancellationToken">An optional cancellation token to cancel the subscription.</param>
    /// <example>
    /// <code lang="fsharp">
    /// // Example usage: subscribe to all events and write them to the console.
    /// let subscription =
    ///     query.Subscribe((fun event -> printfn "Received event: %A" event))
    ///
    /// // Later, to cancel the subscription:
    /// subscription.Dispose()
    /// </code>
    /// </example>
    abstract Subscribe: callback: ('TDataEvent -> unit) * ?cancellationToken: CancellationToken -> IDisposable

    /// <summary>
    /// Subscribes to events using a filter. Only events for which the predicate returns true
    /// are processed, and the callback is invoked for each matching event up to a specified count.
    /// Registration is complete when this method returns. The Task succeeds only
    /// after that count is reached; cancellation or disposal before then cancels it.
    /// </summary>
    /// <param name="filter">
    /// Predicate function to determine if an event should be processed, e.g.
    /// <c>fun event -> event.CorrelationId = targetId</c>.
    /// </param>
    /// <param name="take">Maximum number of events to process.</param>
    /// <param name="callback">
    /// Optional callback function to handle the event (defaults to ignoring the event if not provided).
    /// </param>
    /// <param name="cancellationToken">An optional cancellation token to cancel the subscription.</param>
    /// <example>
    /// <code lang="fsharp">
    /// // Typical usage: subscribe for a filtered event by matching on CorrelationId,
    /// // process only one event, and omit the callback and cancellation token.
    /// async {
    ///     let targetId = some-correlation-id
    ///     // Here, take is set to 1 and no callback or cancellation token is provided.
    ///     let! subscription = query.Subscribe((fun event -> event.CorrelationId = targetId), 1)
    ///     // Use the asynchronous subscription as needed.
    /// } |> Async.Start
    /// </code>
    /// </example>
    abstract Subscribe:
        filter: ('TDataEvent -> bool) *
        take: int *
        ?callback: ('TDataEvent -> unit) *
        ?cancellationToken: CancellationToken ->
            IAwaitableDisposable

    /// <summary>
    /// Subscribes to events matching a specific correlation ID.
    /// </summary>
    /// <param name="cid">The correlation ID to match.</param>
    /// <param name="take">Maximum number of events to process.</param>
    /// <param name="callback">Optional callback function to handle the event.</param>
    /// <param name="cancellationToken">An optional cancellation token to cancel the subscription.</param>
    abstract Subscribe:
        cid: CID *
        take: int *
        ?callback: ('TDataEvent -> unit) *
        ?cancellationToken: CancellationToken ->
            IAwaitableDisposable

    /// <summary>
    /// Subscribes to events matching a specific correlation ID and an additional filter.
    /// </summary>
    /// <param name="cid">The correlation ID to match.</param>
    /// <param name="filter">Additional predicate to filter events after CID matching.</param>
    /// <param name="take">Maximum number of events to process.</param>
    /// <param name="callback">Optional callback function to handle the event.</param>
    /// <param name="cancellationToken">An optional cancellation token to cancel the subscription.</param>
    abstract Subscribe:
        cid: CID *
        filter: ('TDataEvent -> bool) *
        take: int *
        ?callback: ('TDataEvent -> unit) *
        ?cancellationToken: CancellationToken ->
            IAwaitableDisposable

/// The canonical subscription stream: a non-generic shorthand for
/// ISubscribe&lt;IMessageWithCID&gt; — the type every FCQRS projection /
/// read-your-writes subscription actually uses (cf. IEnumerable vs
/// IEnumerable&lt;T&gt;). Lets consumers write ISubscribe instead of the closed
/// generic, and inject it by that name.
type ISubscribe =
    inherit ISubscribe<IMessageWithCID>

/// Internal: lets the facade discover the system-configured bound for a
/// read-your-writes projection wait (akka.fcqrs.command-timeout) without
/// widening the public ISubscribe surface.
type internal IHasNotificationTimeout =
    abstract Timeout: TimeSpan

/// Adapt a generic ISubscribe&lt;IMessageWithCID&gt; to the non-generic ISubscribe
/// (forwards every Subscribe overload to the inner subscription).
let asDefaultSubscribe (inner: ISubscribe<IMessageWithCID>) : ISubscribe =
    { new ISubscribe with
        member _.Subscribe(callback: IMessageWithCID -> unit, ?cancellationToken: CancellationToken) : IDisposable =
            inner.Subscribe(callback, ?cancellationToken = cancellationToken)
        member _.Subscribe(filter: IMessageWithCID -> bool, take: int, ?callback: IMessageWithCID -> unit, ?cancellationToken: CancellationToken) : IAwaitableDisposable =
            inner.Subscribe(filter, take, ?callback = callback, ?cancellationToken = cancellationToken)
        member _.Subscribe(cid: CID, take: int, ?callback: IMessageWithCID -> unit, ?cancellationToken: CancellationToken) : IAwaitableDisposable =
            inner.Subscribe(cid, take, ?callback = callback, ?cancellationToken = cancellationToken)
        member _.Subscribe(cid: CID, filter: IMessageWithCID -> bool, take: int, ?callback: IMessageWithCID -> unit, ?cancellationToken: CancellationToken) : IAwaitableDisposable =
            inner.Subscribe(cid, filter, take, ?callback = callback, ?cancellationToken = cancellationToken)

      interface IHasNotificationTimeout with
          member _.Timeout =
              match box inner with
              | :? IHasNotificationTimeout as t -> t.Timeout
              | _ -> TimeSpan.FromSeconds 30.0 }

/// Adapt a "single-event" projection handler — one that just updates the read
/// model and returns unit — to the canonical list-returning shape: after the
/// handler runs, the journal event itself is published to subscribers whenever
/// it is an IMessageWithCID. Aggregate Event&lt;'T&gt;s are; saga internals are
/// not, so subscribers only ever see aggregate events. This is the common
/// projection (notify with each event as-is); write a list-returning handler
/// when notifications must be filtered or transformed — e.g. suppressing
/// intermediate events so read-your-writes only wakes on the final one.
let autoPublish (handle: int64 -> obj -> unit) : int64 -> obj -> IMessageWithCID list =
    fun offset evt ->
        handle offset evt

        match evt with
        | :? IMessageWithCID as m -> [ m ]
        | _ -> []

/// Adapt a "filtered" projection handler — one that updates the read model and
/// returns Publish/Suppress — to the canonical list-returning shape. Like
/// autoPublish, but the handler gets a per-event say over whether subscribers
/// wake: on Publish the journal event itself is notified (when it is an
/// IMessageWithCID), on Suppress nothing is. The middle ground between autoPublish
/// (always notify) and a hand-written list handler (notify anything).
let filterPublish (handle: int64 -> obj -> Notify) : int64 -> obj -> IMessageWithCID list =
    fun offset evt ->
        match handle offset evt with
        | Publish ->
            match evt with
            | :? IMessageWithCID as m -> [ m ]
            | _ -> []
        | Suppress -> []

[<AutoOpen>]
module internal Internal =
    open Akka.Persistence.Sql.Query
    let readJournal system =
        PersistenceQuery
            .Get(system)
            .ReadJournalFor<SqlReadJournal>
            SqlReadJournal.Identifier

    /// Each subscriber owns a bounded queue and one callback worker. Registration
    /// and publication share a lock, but user code never runs under that lock.
    /// A slow callback therefore sheds only its own queued notifications.
    type private NotificationSubscription<'T>
        (bufferSize: int, filter: 'T -> bool, take: int option, callback: 'T -> unit,
         token: CancellationToken, unregister: unit -> unit, logger: ILogger) =
        let options = BoundedChannelOptions(bufferSize)
        do
            options.FullMode <- BoundedChannelFullMode.DropOldest
            options.SingleReader <- true
            options.AllowSynchronousContinuations <- false

        let channel = Channel.CreateBounded<'T>(options)
        let completion = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let mutable stopped = 0
        let registrationGate = obj ()
        let mutable registration: CancellationTokenRegistration option = None

        let finish complete =
            if Interlocked.Exchange(&stopped, 1) = 0 then
                unregister ()
                channel.Writer.TryComplete() |> ignore
                lock registrationGate (fun () ->
                    registration |> Option.iter (fun r -> r.Unregister() |> ignore)
                    registration <- None)
                complete ()

        member _.Publish(evt: 'T) =
            channel.Writer.TryWrite evt |> ignore

        member _.Cancel() =
            finish (fun () -> completion.TrySetCanceled(token) |> ignore)

        member this.Start() =
            let reg = token.Register(fun () -> this.Cancel())
            // Cancellation can run synchronously inside Register, and the worker
            // can finish concurrently. Neither path may leave a registration behind.
            lock registrationGate (fun () ->
                if Volatile.Read(&stopped) = 0 then registration <- Some reg
                else reg.Unregister() |> ignore)

            // Task.Run also keeps callbacks off the subscribing/publishing thread
            // when a notification was queued before this worker starts.
            Task.Run(Func<Task>(fun () ->
                task {
                    try
                        let mutable remaining = take
                        if remaining = Some 0 then
                            finish (fun () -> completion.TrySetResult() |> ignore)

                        while Volatile.Read(&stopped) = 0 do
                            let! available = channel.Reader.WaitToReadAsync().AsTask()
                            if available then
                                let mutable evt = Unchecked.defaultof<'T>
                                while Volatile.Read(&stopped) = 0 && channel.Reader.TryRead(&evt) do
                                    if filter evt then
                                        callback evt
                                        match remaining with
                                        | Some 1 -> finish (fun () -> completion.TrySetResult() |> ignore)
                                        | Some count -> remaining <- Some(count - 1)
                                        | None -> ()
                    with ex ->
                        finish (fun () ->
                            completion.TrySetException ex |> ignore
                            if take.IsNone then
                                completion.Task.Exception |> ignore
                                logger.LogError(ex, "Notification subscriber failed; this subscription is dead and will receive no further events"))
                } :> Task))
            |> ignore

        interface IAwaitableDisposable with
            member _.Task = completion.Task
            member this.Dispose() = this.Cancel()

    /// Synchronous registration closes the subscribe-before-send race: publishing
    /// after Subscribe returns always sees the new subscriber. Queue workers may
    /// start later without losing the notifications already addressed to them.
    type NotificationHub<'TDataEvent when 'TDataEvent :> IMessageWithCID>
        (bufferSize: int, logger: ILogger, notificationTimeout: TimeSpan) =
        let gate = obj ()
        let subscribers = Dictionary<int64, NotificationSubscription<'TDataEvent>>()
        let mutable nextId = 0L
        let mutable stopped = false

        member private _.Subscribe(filter, take, callback, token) =
            take |> Option.iter (fun count -> if count < 0 then invalidArg "take" "The event count must not be negative.")
            let subscriber =
                lock gate (fun () ->
                    if stopped then invalidOp "The projection notification subscription has stopped."
                    nextId <- nextId + 1L
                    let id = nextId
                    let remove () = lock gate (fun () -> subscribers.Remove id |> ignore)
                    let subscriber = new NotificationSubscription<'TDataEvent>(bufferSize, filter, take, callback, token, remove, logger)
                    subscribers.Add(id, subscriber)
                    subscriber)
            subscriber.Start()
            subscriber :> IAwaitableDisposable

        member _.Publish(evt: 'TDataEvent) =
            lock gate (fun () ->
                for subscriber in subscribers.Values do
                    subscriber.Publish evt)

        member _.Stop() =
            let active =
                lock gate (fun () ->
                    stopped <- true
                    let active = subscribers.Values |> Seq.toArray
                    subscribers.Clear()
                    active)
            for subscriber in active do subscriber.Cancel()

        interface ISubscribe<'TDataEvent> with
            member this.Subscribe(callback, ?cancellationToken) =
                this.Subscribe((fun _ -> true), None, callback, defaultArg cancellationToken CancellationToken.None) :> IDisposable

            member this.Subscribe(filter: 'TDataEvent -> bool, take: int, ?callback, ?cancellationToken) =
                this.Subscribe(filter, Some take, defaultArg callback ignore, defaultArg cancellationToken CancellationToken.None)

            member this.Subscribe(cid: CID, take: int, ?callback, ?cancellationToken) =
                (this :> ISubscribe<'TDataEvent>).Subscribe((fun e -> e.CID = cid), take, ?callback = callback, ?cancellationToken = cancellationToken)

            member this.Subscribe(cid: CID, filter: 'TDataEvent -> bool, take: int, ?callback, ?cancellationToken) =
                (this :> ISubscribe<'TDataEvent>).Subscribe((fun e -> e.CID = cid && filter e), take, ?callback = callback, ?cancellationToken = cancellationToken)

        interface IHasNotificationTimeout with
            member _.Timeout = notificationTimeout


let private activitySource = new ActivitySource(Telemetry.QueryActivitySourceName)

let init<'TDataEvent, 'TPredicate, 't when 'TDataEvent :> IMessageWithCID> (actorApi: IActor) offsetCount handler =
    let logger = actorApi.LoggerFactory.CreateLogger "Query"
    logger.LogInformation "Query started"

    // Each active subscriber has its own bounded, ephemeral notification queue.
    // Publications without subscribers are discarded; a full subscriber queue
    // drops its oldest item without blocking projection or other subscribers.
    let bufferSize =
        let s: string | null = actorApi.Configuration["config:akka:fcqrs:notification-buffer"]

        match System.Int32.TryParse s with
        | true, v when v > 0 -> v
        | _ -> 1024

    let notificationTimeout =
        CommandHandler.Internal.resolveCommandTimeout actorApi.System.Settings.Config
    let notifications = NotificationHub<'TDataEvent>(bufferSize, logger, notificationTimeout)
    actorApi.System.RegisterOnTermination(Action(fun () -> notifications.Stop()))

    // A journal-read error must never silently complete the projection stream
    // (frozen read models in a healthy-looking process). Restart the source
    // with backoff instead — resuming from the last offset the handler actually
    // processed, so a restart never replays already-projected events.
    let mutable lastProcessedOffset = offsetCount

    let restartSettings =
        RestartSettings.Create(TimeSpan.FromSeconds 1.0, TimeSpan.FromSeconds 30.0, 0.2)

    let source =
        RestartSource.WithBackoff(
            (fun () -> (readJournal actorApi.System).AllEvents(Offset.Sequence lastProcessedOffset)),
            restartSettings)

    source
    |> Source.runForEach actorApi.Materializer (fun envelop ->
        try
            let offsetValue = (envelop.Offset :?> Sequence).Value
            logger.LogTrace("data event : {@dataevent}", envelop.Event)

            // Projection span: closes the trace end-to-end (command -> event ->
            // projection). Parent comes from the event's metadata traceparent.
            use activity =
                if activitySource.HasListeners() then
                    match envelop.Event with
                    | :? FCQRS.Model.Data.IMessage as msg ->
                        let cidStr = msg.CID |> ValueLens.Value |> ValueLens.Value

                        let payloadName =
                            match envelop.Event with
                            | :? IEnvelope as env -> env.Payload.GetType().Name
                            | other -> other.GetType().Name

                        let act =
                            match tryTraceContext msg.Metadata cidStr with
                            | Some p ->
                                activitySource.StartActivity($"Projection:{payloadName}", ActivityKind.Internal, p)
                            | None -> activitySource.StartActivity($"Projection:{payloadName}", ActivityKind.Internal)

                        match act with
                        | null -> ()
                        | act ->
                            act.SetTag("cid", cidStr) |> ignore
                            act.SetTag("offset", offsetValue) |> ignore

                        act
                    | _ -> null
                else
                    null

            let res = handler offsetValue envelop.Event

            res |> List.iter notifications.Publish

            lastProcessedOffset <- offsetValue
        with ex ->
            logger.LogCritical(ex, "Error in query handler")
            // FailFast, not Exit: Exit runs ProcessExit handlers (which can hang);
            // a broken projection must kill the process immediately and loudly.
            // (The projection span was already disposed by `use` during unwind,
            // so it sits in the exporter queue — the flush below gets it out.)
            fatalFailFast null "Process terminated due to query projection error" ex)
    |> Async.Start

    notifications :> ISubscribe<'TDataEvent>
