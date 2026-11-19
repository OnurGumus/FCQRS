open System
open System.IO
// snippet: 1
// snippet: 2 module
    // snippet: 3
// include: samples/accounts/6-add-a-memo/fsharp/Transfer.fs
module Statement =
    open System.Data.Common
    open System.Threading.Tasks
    open Akka.Persistence.Query
    open Dapper
    open FCQRS.Common
    open Account
    // snippet: 4
    // snippet: 5
open System.Data.Common
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Actor
open FCQRS.Common
open FCQRS.FSharp
open FCQRS.Model.Data
open FCQRS.ProjectionStorage
open FCQRS.Projections
open FCQRS.Workflows
open Account
open Transfer
let connectionString = $"Data Source={database}"

// Startup, the accounts, and the transfer workflow are the same as in step 5.
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
let transfers = Workflows.register api (Transfer.definition accounts)
// snippet: 6

// snippet: 7
