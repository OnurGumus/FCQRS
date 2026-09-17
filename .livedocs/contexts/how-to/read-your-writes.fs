open System
open FCQRS.Model.Data
open FCQRS.Common
open FCQRS.FSharp
open FCQRS.Model.Data
module Document =
    type Document = { Id: Guid; Title: ShortString; Content: LongString }
    type Command = CreateOrUpdate of Document
    type Event = Updated of Document | Rejected of string
open Document
open System.Threading
open FCQRS.Query
let save (api: IActor) (handle: int64 -> obj -> unit)
         (documents: AggregateHandle<Document.Command, Document.Event>) cid id doc = async {
    // snippet: 1
    return ack
}
let saveManually (subscriptions: ISubscribe)
                 (documents: AggregateHandle<Document.Command, Document.Event>)
                 (cid: CID) documentId command isExpectedReply (cancellationToken: CancellationToken) = async {
    // snippet: 2
}
