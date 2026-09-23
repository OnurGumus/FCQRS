// include: samples/accounts/4-show-a-statement/fsharp/Account.fs
module Statement =
    open System.Data.Common
    open System.Threading.Tasks
    open Akka.Persistence.Query
    open Dapper
    open FCQRS.Common
    open Account
    // snippet: 1
    // snippet: 2
open System
open System.Data.Common
open System.IO
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Actor
open FCQRS.Common
open FCQRS.FSharp
open FCQRS.ProjectionStorage
open FCQRS.Projections
open Account
let database = Path.Combine(AppContext.BaseDirectory, "accounts.db")
let connectionString = $"Data Source={database}"

// Startup and the account registration are the same as in step 2.
let logging = LoggerFactory.Create(fun _ -> ())
let configuration = ConfigurationBuilder().Build()
let connection = Fcqrs.connect DBType.Sqlite connectionString
let api = Fcqrs.actor configuration logging (Some connection) "accounts"

let accounts =
    Fcqrs.aggregate api
        { Name = "Account"
          Initial = initial
          Decide = decide
          Fold = fold
          Snapshots = Default
          Passivation = PassivationPolicy.Default }

Fcqrs.wireSagaStarters api []
// snippet: 3

let describe event =
    match event with
    | Opened owner -> $"Opened for {owner}"
    | Deposited amount -> $"Deposited {amount}"
    | Withdrawn amount -> $"Withdrew {amount}"
    | Rejected reason -> $"Rejected: {reason}"
// snippet: 4
// snippet: 5
