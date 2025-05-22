module FCQRS.Model.Query

open FCQRS.Model.Data
open System.Threading
open System

type DataEvent<'TDataEventType> =
    { Type: 'TDataEventType
      CID: CID }

    interface IMessageWithCID with
        member this.CID = this.CID

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
