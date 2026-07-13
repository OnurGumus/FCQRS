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

type IAwaitable =
    abstract member Task: Task

type IAwaitableDisposable =
    inherit IDisposable
    inherit IAwaitable

open FCQRS.Model.Data

[<Interface>]
type ISubscribe<'TDataEvent when 'TDataEvent :> IMessageWithCID> =
    /// <summary>
    /// Subscribes to all events and invokes the specified callback for each event.
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
            inner.Subscribe(cid, filter, take, ?callback = callback, ?cancellationToken = cancellationToken) }

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

    let subscribeToStream source mat (sink: Sink<'TDataEvent, _>) =
        source
        |> Source.viaMat KillSwitch.single Keep.right
        |> Source.toMat sink Keep.both
        |> Graph.run mat

    let subscribeCmd<'TDataEvent> (source: Source<'TDataEvent, unit>) (actorApi: IActor) =
        fun (cb: 'TDataEvent -> unit) ->
            let sink = Sink.forEach (fun event -> cb event)
            let ks, _ = subscribeToStream source actorApi.Materializer sink
            ks :> IKillSwitch

    let subscribeCmdWithFilter<'TDataEvent> (source: Source<'TDataEvent, unit>) (actorApi: IActor) =
        fun filter take cb ->
            // cb is now a required parameter (it will be provided as default if needed)
            let subscribeToStream source filter take mat (sink: Sink<'TDataEvent, _>) =
                source
                |> Source.viaMat KillSwitch.single Keep.right
                |> Source.filter filter
                |> Source.take take
                |> Source.toMat sink Keep.both
                |> Graph.run mat

            let sink = Sink.forEach (fun event -> cb event)
            let ks, d = subscribeToStream source filter take actorApi.Materializer sink
            let d = d |> Async.Ignore
            ks :> IKillSwitch, d


let private activitySource = new ActivitySource(Telemetry.QueryActivitySourceName)

let init<'TDataEvent, 'TPredicate, 't when 'TDataEvent :> IMessageWithCID> (actorApi: IActor) offsetCount handler =
    let logger = actorApi.LoggerFactory.CreateLogger "Query"
    logger.LogInformation "Query started"

    // Read-your-writes notifications are ephemeral: if nobody is subscribed,
    // dropping the oldest is correct. OverflowStrategy.Fail here used to fault
    // the offer once the buffer filled with no consumers attached - which the
    // handler's catch then escalated to a process kill. Buffer size:
    // config:akka:fcqrs:notification-buffer (default 1024).
    let bufferSize =
        let s: string | null = actorApi.Configuration["config:akka:fcqrs:notification-buffer"]

        match System.Int32.TryParse s with
        | true, v when v > 0 -> v
        | _ -> 1024

    // The queue accepts any positive size — make it as large as you like.
    // The BroadcastHub does NOT: its buffer must be a power of two (and is a
    // per-consumer smoothing window, not the shedding point), so it gets the
    // configured value rounded down to a power of two, clamped to [8, 4096].
    let hubBufferSize =
        let clamped = max 8 (min bufferSize 4096)
        let mutable p = 8
        while p * 2 <= clamped do p <- p * 2
        p

    let subQueue = Source.queue OverflowStrategy.DropHead bufferSize
    let subSink = Sink.broadcastHub hubBufferSize

    let runnableGraph = subQueue |> Source.toMat subSink Keep.both

    let queue, subRunnable = runnableGraph |> Graph.run actorApi.Materializer

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
            res |> List.iter (fun x -> queue.OfferAsync(x).Wait())
            lastProcessedOffset <- offsetValue
        with ex ->
            logger.LogCritical(ex, "Error in query handler")
            // FailFast, not Exit: Exit runs ProcessExit handlers (which can hang);
            // a broken projection must kill the process immediately and loudly.
            // (The projection span was already disposed by `use` during unwind,
            // so it sits in the exporter queue — the flush below gets it out.)
            fatalFailFast null "Process terminated due to query projection error" ex)
    |> Async.Start

    let subscribeCmd = subscribeCmd subRunnable actorApi
    let subscribeCmdWithFilter = subscribeCmdWithFilter subRunnable actorApi

    { new ISubscribe<'TDataEvent> with
        override _.Subscribe(callback, ?cancellationToken) =
            let token = defaultArg cancellationToken CancellationToken.None
            let ks = subscribeCmd callback
            let reg = token.Register(fun _ -> ks.Shutdown())

            { new IDisposable with
                member __.Dispose() =
                    reg.Dispose()

                    if not token.IsCancellationRequested then
                        ks.Shutdown() }

        override _.Subscribe(filter: 'TDataEvent -> bool, take: int, ?callback, ?cancellationToken) =
            let token = defaultArg cancellationToken CancellationToken.None
            let cb = defaultArg callback ignore
            let ks, res = subscribeCmdWithFilter filter take cb
            let reg = token.Register(fun _ -> ks.Shutdown())
            let task = Async.StartImmediateAsTask(res, token) :> Task

            { new IAwaitableDisposable with
                member __.Task = task

                member __.Dispose() =
                    reg.Dispose()

                    if not token.IsCancellationRequested then
                        ks.Shutdown() }

        override this.Subscribe(cid: CID, take: int, ?callback, ?cancellationToken) =
            this.Subscribe((fun e -> e.CID = cid), take, ?callback = callback, ?cancellationToken = cancellationToken)

        override this.Subscribe(cid: CID, filter: 'TDataEvent -> bool, take: int, ?callback, ?cancellationToken) =
            this.Subscribe((fun e -> e.CID = cid && filter e), take, ?callback = callback, ?cancellationToken = cancellationToken)

    }
