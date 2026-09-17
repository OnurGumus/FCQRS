// include: samples/getting-started-fsharp/Document.fs
open System
open FCQRS.Model.Data
open FCQRS.Common
open FCQRS.FSharp
open Program
// snippet: 1
let tryEdit (api: IActor) (documents: AggregateHandle<DocumentCommand, DocumentEvent>) =
    // snippet: 2
