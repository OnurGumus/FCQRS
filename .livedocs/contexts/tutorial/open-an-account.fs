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
// snippet: 3
let describe event =
    match event with
    | Opened owner -> $"Opened for {owner}"
    | Deposited amount -> $"Deposited {amount}"
// snippet: 4
