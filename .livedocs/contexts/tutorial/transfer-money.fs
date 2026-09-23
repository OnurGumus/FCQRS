// snippet: 1 module
    // snippet: 2
// include: samples/accounts/5-transfer-money/fsharp/Statement.fs
module Transfer =
    open System
    open FCQRS.Common
    open FCQRS.FSharp
    open Account
    // snippet: 3
    // snippet: 4
    // snippet: 5
    // snippet: 6
open System
open System.Data.Common
open System.IO
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
// snippet: 7

// The statement from step 4, with rows for transfers.
do
    use connection = new SqliteConnection(connectionString)
    Statement.createTable connection

let store =
    SqlProjectionStore(
        ProjectionSqlDialect.Sqlite,
        Func<DbConnection>(fun () -> new SqliteConnection(connectionString)))
let options = TransactionalProjectionOptions("Statement", store)
let statement = Fcqrs.transactionalProjection api options Statement.handle

let describe event =
    match event with
    | Opened owner -> $"Opened for {owner}"
    | Deposited amount -> $"Deposited {amount}"
    | Withdrawn amount -> $"Withdrew {amount}"
    | TransferSent(id, target, amount) -> $"Sent {amount} to {target} ({id})"
    | TransferReceived(id, source, amount) -> $"Received {amount} from {source} ({id})"
    | TransferRefunded(id, _, amount) -> $"Refunded {amount} ({id})"
    | Rejected reason -> $"Rejected: {reason}"

let show (reply: Event<AccountEvent>) =
    let stored = if reply.Journaled = Some true then "stored" else "not stored"
    let description = describe reply.EventDetails
    printfn $"{description} (version {reply.Version}, {stored})"

let alice = Fcqrs.aggregateId "alice"
let bob = Fcqrs.aggregateId "bob"

// Send a command and wait until the statement includes the event it stored.
let send account command =
    Fcqrs.sendAwaiting statement accounts (Fcqrs.newCid ()) account command (fun _ -> true)
    |> Async.RunSynchronously
    |> show
// snippet: 8
// snippet: 9
