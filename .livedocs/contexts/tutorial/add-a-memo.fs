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
open Account
let connectionString = $"Data Source={database}"

// Startup, the accounts, and the transfer saga are the same as in step 5.
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

let transfers = Fcqrs.saga api (Transfer.definition accounts.Factory)
Fcqrs.wireSagaStarters api [ transfers ]
// snippet: 6

let describe event =
    let about memo = memo |> Option.map (sprintf ", \"%s\"") |> Option.defaultValue ""
    match event with
    | Opened owner -> $"Opened for {owner}"
    | Deposited amount -> $"Deposited {amount}"
    | Withdrawn amount -> $"Withdrew {amount}"
    | TransferSent(id, target, amount, memo) -> $"Sent {amount} to {target} ({id}{about memo})"
    | TransferReceived(id, source, amount, memo) ->
        $"Received {amount} from {source} ({id}{about memo})"
    | TransferRefunded(id, _, amount) -> $"Refunded {amount} ({id})"
    | Rejected reason -> $"Rejected: {reason}"

let show (reply: Event<AccountEvent>) =
    let stored = if reply.Journaled = Some true then "stored" else "not stored"
    let description = describe reply.EventDetails
    printfn $"{description} (version {reply.Version}, {stored})"

let alice = Fcqrs.aggregateId "alice"

let finished (message: IMessageWithCID) =
    match message with
    | :? Event<AccountEvent> as event ->
        match event.EventDetails with
        | TransferReceived _ | TransferRefunded _ -> true
        | _ -> false
    | _ -> false
// snippet: 7
