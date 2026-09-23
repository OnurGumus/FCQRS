// include: samples/accounts/3-restart-the-bank/fsharp/Account.fs
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
// snippet: 1

Fcqrs.wireSagaStarters api []

let describe event =
    match event with
    | Opened owner -> $"Opened for {owner}"
    | Deposited amount -> $"Deposited {amount}"
    | Withdrawn amount -> $"Withdrew {amount}"
    | Rejected reason -> $"Rejected: {reason}"
// snippet: 2
