// snippet: 1 module
    // snippet: 2
open System
open System.IO
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Actor
open FCQRS.Common
open FCQRS.FSharp
open Account
let database = Path.Combine(AppContext.BaseDirectory, "accounts.db")

// Startup is the same as in step 1.
let logging = LoggerFactory.Create(fun _ -> ())
let configuration = ConfigurationBuilder().Build()
let connection = Fcqrs.connect DBType.Sqlite $"Data Source={database};"
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

let describe event =
    match event with
    | Opened owner -> $"Opened for {owner}"
    | Deposited amount -> $"Deposited {amount}"
    | Withdrawn amount -> $"Withdrew {amount}"
    | Rejected reason -> $"Rejected: {reason}"
// snippet: 3
// snippet: 4
