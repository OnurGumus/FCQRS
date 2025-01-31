module FCQRS.Model.Query

open FCQRS.Model.Data
open System.Threading
open System

type DataEvent<'TDataEventType> = { Type: 'TDataEventType; CID: CID }

let emptyDisposable: IDisposable =
    { new IDisposable with
        member _.Dispose() = () }

[<Interface>]
type IQuery<'TDataEventType> =
    abstract Query<'t> :
        ?filter: Predicate *
        ?orderby: string *
        ?orderbydesc: string *
        ?thenby: string *
        ?thenbydesc: string *
        ?take: int *
        ?skip: int *
        ?cacheKey: string ->
            list<'t> Async

    abstract Subscribe<'TDataEventType> :
        callback: (DataEvent<'TDataEventType> -> unit) * CancellationToken -> IDisposable

    abstract Subscribe:
        filter: (DataEvent<'TDataEventType> -> bool) *
        numberOfEvents: int *
        callback: (DataEvent<'TDataEventType> -> unit) *
        CancellationToken ->
            Async<IDisposable>
