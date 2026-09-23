// include: samples/accounts/2-withdraw-money/fsharp/Account.fs
open System
open FCQRS.Model.Data
open FCQRS.Common
open FCQRS.FSharp
open Account
open System.Threading
open FCQRS.Projections
let depositAndCatchUp (accounts: AggregateHandle<AccountCommand, AccountEvent>)
                      (projection: IProjection) (cid: CID) (alice: AggregateId)
                      (cancellationToken: CancellationToken) = async {
    // snippet: 1
}
open System.Data.Common
open System.Threading.Tasks
open Akka.Persistence.Query
open Dapper
open Microsoft.Data.Sqlite
open FCQRS.ProjectionStorage
let connectionString = "Data Source=app.db;"
let configuration = Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
let loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(fun _ -> ())
module Sqlite =
    let api = Fcqrs.actor configuration loggerFactory
                  (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite connectionString)) "accounts"
    // snippet: 2
module PostgreSql =
    let handle = Sqlite.handle
    // snippet: 3
