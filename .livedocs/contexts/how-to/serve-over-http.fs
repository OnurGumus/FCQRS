// include: samples/accounts/4-show-a-statement/fsharp/Account.fs
// include: samples/accounts/4-show-a-statement/fsharp/Statement.fs
open System
open System.Threading
open System.Threading.Tasks
open Dapper
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Data.Sqlite
open FCQRS.Common
open FCQRS.FSharp
open FCQRS.Projections
open Account
let sender (accounts: AggregateHandle<AccountCommand, AccountEvent>)
           (statement: IProjection) (connectionString: string) =
    // snippet: 1
    send
// snippet: 2
let valid (value: string) =
    not (String.IsNullOrWhiteSpace value) && value.Length <= 255
let invalid = Results.BadRequest({| error = "Invalid request." |})
let endpoints (app: WebApplication) (connectionString: string)
              (send: string -> AccountCommand -> CancellationToken -> Task<IResult>) =
    // snippet: 3
    // snippet: 4
