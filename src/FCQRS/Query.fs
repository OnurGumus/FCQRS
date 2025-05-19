module FCQRS.Query

open Akka.Persistence.Query
open Akka.Persistence.Query.Sql
open Akkling.Streams
open Akka.Streams
open Akka.Streams.Dsl
open Microsoft.Extensions.Logging
open Common
open System.Threading
open System
open System.Threading.Tasks

type IAwaitable =
    abstract member Task: Task

type IAwaitableDisposable =
    inherit IDisposable
    inherit IAwaitable

[<Interface>]
type ISubscribe<'TDataEvent> =
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

[<AutoOpen>]
module Internal =
    let readJournal system =
        PersistenceQuery
            .Get(system)
            .ReadJournalFor<Akka.Persistence.Sql.Query.SqlReadJournal>
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


let init<'TDataEvent, 'TPredicate, 't> (actorApi: IActor) offsetCount handler =
    let source = (readJournal actorApi.System).AllEvents(Offset.Sequence offsetCount)
    let logger = actorApi.LoggerFactory.CreateLogger "Query"
    logger.LogInformation "Query started"
    let subQueue = Source.queue OverflowStrategy.Fail 1024
    let subSink = Sink.broadcastHub 1024

    let runnableGraph = subQueue |> Source.toMat subSink Keep.both

    let queue, subRunnable = runnableGraph |> Graph.run actorApi.Materializer

    source
    |> Source.recover (fun ex ->
        logger.LogError(ex, "Error in query source")
        None)
    |> Source.runForEach actorApi.Materializer (fun envelop ->
        try
            let offsetValue = (envelop.Offset :?> Sequence).Value
            let res = handler offsetValue envelop.Event
            res |> List.iter (fun x -> queue.OfferAsync(x).Wait())
        with ex ->
            logger.LogCritical(ex, "Error in query handler")
            Environment.Exit -1)
    |> Async.Start

    System.Threading.Thread.Sleep 1000

    subscribeToStream
        source
        actorApi.Materializer
        (Sink.ForEach(fun x -> logger.LogTrace("data event : {@dataevent}", x)))
    |> ignore

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

        override _.Subscribe(filter, take, ?callback, ?cancellationToken) =
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

    }
