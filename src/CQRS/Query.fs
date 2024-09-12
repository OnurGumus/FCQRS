module CQRS.Query
open Akka.Persistence.Query
open Akka.Persistence.Query.Sql
open Akkling.Streams
open CQRS.Actor
open Akka.Streams
open Akka.Streams.Dsl


[<Interface>]
type IAPI<'TDataEvent,'TPredicate> =
    abstract Query:
        ty:System.Type *
        ?filter: 'TPredicate *
        ?orderby: string *
        ?orderbydesc: string *
        ?thenby: string *
        ?thenbydesc: string *
        ?take: int *
        ?skip: int *
        ?cacheKey: string ->
            seq<obj> Async

    abstract Subscribe: ('TDataEvent -> unit) -> IKillSwitch
    abstract Subscribe: ('TDataEvent -> bool) * int * ('TDataEvent -> unit) -> IKillSwitch * Async<unit>


let readJournal system =
    PersistenceQuery
        .Get(system)
        .ReadJournalFor<Akka.Persistence.Sql.Query.SqlReadJournal>(SqlReadJournal.Identifier)

let subscribeToStream source mat (sink: Sink<'TDataEvent, _>) =
            source
            |> Source.viaMat KillSwitch.single Keep.right
            |> Source.toMat (sink) Keep.both
            |> Graph.run mat

let subscribeCmd<'TDataEvent> (source:Source<'TDataEvent,unit>) (actorApi :IActor) =
    (fun (cb: 'TDataEvent -> unit) ->
        let sink = Sink.forEach (fun event -> cb (event))
        let ks, _ = subscribeToStream source actorApi.Materializer sink
        ks :> IKillSwitch)


let subscribeCmdWithFilter<'TDataEvent> (source:Source<'TDataEvent,unit>)  (actorApi:IActor) =
        (fun filter take (cb: 'TDataEvent -> unit) ->
            let subscribeToStream source filter take mat (sink: Sink<'TDataEvent, _>) =
                source
                |> Source.viaMat KillSwitch.single Keep.right
                |> Source.filter filter
                |> Source.take take
                |> Source.toMat (sink) Keep.both
                |> Graph.run mat

            let sink = Sink.forEach (fun event -> cb (event))
            let ks, d = subscribeToStream source filter take actorApi.Materializer sink
            let d = d |> Async.Ignore
            ks :> IKillSwitch, d)
    
let init<'TDataEvent,'TPredicate,'t> (actorApi: IActor) offsetCount  handler (query: _ -> Async<seq<obj>>)=
    let source = (readJournal actorApi.System).AllEvents(Offset.Sequence(offsetCount))

    let subQueue = Source.queue OverflowStrategy.Fail 1024
    let subSink = (Sink.broadcastHub 1024)

    let runnableGraph = subQueue |> Source.toMat subSink Keep.both

    let queue, subRunnable = runnableGraph |> Graph.run (actorApi.Materializer)

    source
    |> Source.recover (fun ex ->
        None)
    |> Source.runForEach actorApi.Materializer (handler actorApi queue)
    |> Async.StartAsTask
    |> ignore

    System.Threading.Thread.Sleep(1000)
    subscribeToStream
        source
        actorApi.Materializer
        (Sink.ForEach(fun x -> Serilog.Log.Verbose("data event : {@dataevent}", x)))|> ignore

    let subscribeCmd = subscribeCmd subRunnable actorApi
    let subscribeCmdWithFilter = subscribeCmdWithFilter subRunnable actorApi



    { new IAPI<'TDataEvent,'TPredicate> with
        override this.Subscribe(cb) = subscribeCmd (cb)
        override this.Subscribe(filter, take, cb) = subscribeCmdWithFilter filter take cb

        override this.Query(ty:System.Type, ?filter, ?orderby, ?orderbydesc, ?thenby,? thenbydesc, ?take, ?skip,  ?cacheKey) : Async<obj seq> =
            query (ty, filter, orderby, orderbydesc, thenby, thenbydesc, take, skip, cacheKey)
    }

