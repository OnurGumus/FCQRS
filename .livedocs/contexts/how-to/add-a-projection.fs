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
// snippet: 1
let register (api: IActor) (connString: string) =
    // snippet: 2
    subscriptions
