// include: samples/accounts/5-transfer-money/fsharp/Account.fs
module Transfer =
    open System
    open FCQRS.Common
    open FCQRS.FSharp
    open Account
    // snippet: 1
    // snippet: 2
    // snippet: 3
    // snippet: 4
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Actor
open FCQRS.Common
open FCQRS.FSharp
open Account
let connectionString = "Data Source=accounts.db"
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
// snippet: 5
