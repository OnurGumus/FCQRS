
module FCQRS.Model.Query
open FCQRS.Data
open System.Threading

type DataEvent<'TDataEventType> = { Type: 'TDataEventType; CID: CID }

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

    abstract Subscribe<'TDataEventType>: callback:(DataEvent<'TDataEventType> -> unit) * CancellationToken -> unit
    abstract Subscribe: filter:(DataEvent<'TDataEventType> -> bool) * numberOfEvents:int * callback:(DataEvent<'TDataEventType> -> unit) * CancellationToken -> Async<unit>

