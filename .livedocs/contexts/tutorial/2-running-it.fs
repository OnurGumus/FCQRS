// include: samples/registration-fsharp/Account.fs
open System
open FCQRS.Model.Data
open FCQRS.Common
open FCQRS.FSharp
open Account
open System.Collections.Concurrent
open System.Threading.Tasks
let accountId = "alice"
let id = Fcqrs.aggregateId accountId
// snippet: 1
let register (api: IActor) =
    // snippet: 2
    accounts
