module FCQRS.Query
open Akka.Persistence.Query
open Akka.Persistence.Query.Sql
open Akkling.Streams
open Akka.Streams
open Akka.Streams.Dsl
open Microsoft.Extensions.Logging
open Common

[<Interface>]
type ISubscribe<'TDataEvent,'TPredicate> =
    abstract Subscribe: ('TDataEvent -> unit) -> IKillSwitch
    abstract Subscribe: ('TDataEvent -> bool) * int * ('TDataEvent -> unit) -> IKillSwitch * Async<unit>

let readJournal system =
    PersistenceQuery
        .Get(system)
        .ReadJournalFor<Akka.Persistence.Sql.Query.SqlReadJournal>SqlReadJournal.Identifier

let subscribeToStream source mat (sink: Sink<'TDataEvent, _>) =
    source
    |> Source.viaMat KillSwitch.single Keep.right
    |> Source.toMat sink Keep.both
    |> Graph.run mat

let subscribeCmd<'TDataEvent> (source:Source<'TDataEvent,unit>) (actorApi :IActor) =
    fun (cb: 'TDataEvent -> unit) ->
        let sink = Sink.forEach (fun event -> cb event)
        let ks, _ = subscribeToStream source actorApi.Materializer sink
        ks :> IKillSwitch

let subscribeCmdWithFilter<'TDataEvent> (source:Source<'TDataEvent,unit>)  (actorApi:IActor) =
        fun filter take (cb: 'TDataEvent -> unit) ->
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
    
let init<'TDataEvent,'TPredicate,'t> (actorApi: IActor) offsetCount  handler =
    let source = (readJournal actorApi.System).AllEvents(Offset.Sequence offsetCount)
    let logger = actorApi.LoggerFactory.CreateLogger"Query"
    let subQueue = Source.queue OverflowStrategy.Fail 1024
    let subSink = Sink.broadcastHub 1024

    let runnableGraph = subQueue |> Source.toMat subSink Keep.both

    let queue, subRunnable = runnableGraph |> Graph.run actorApi.Materializer

    source
    |> Source.recover (fun ex -> logger.LogError(ex, "Error in query source");None)
    |> Source.runForEach actorApi.Materializer (
        fun envelop -> 
        try
            let offsetValue = (envelop.Offset :?> Sequence).Value
            let res = handler  offsetValue envelop.Event
            res |> List.iter (fun x -> queue.OfferAsync(x).Wait())
         with
            | ex -> 
                logger.LogCritical(ex, "Error in query handler")
                System.Environment.Exit -1
        )
    |> Async.Start

    System.Threading.Thread.Sleep 1000
    subscribeToStream
        source
        actorApi.Materializer
        (Sink.ForEach(fun x -> logger.LogTrace("data event : {@dataevent}", x)))|> ignore

    let subscribeCmd = subscribeCmd subRunnable actorApi
    let subscribeCmdWithFilter = subscribeCmdWithFilter subRunnable actorApi

    { new ISubscribe<'TDataEvent,'TPredicate> with
        override _.Subscribe cb = subscribeCmd cb
        override _.Subscribe(filter, take, cb) = subscribeCmdWithFilter filter take cb
    }