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
open FCQRS.Workflows
open Account
open Transfer

let database = Path.Combine(AppContext.BaseDirectory, "accounts.db")
let connectionString = $"Data Source={database}"

// FCQRS logs through Microsoft.Extensions.Logging; this sample stays quiet.
let logging = LoggerFactory.Create(fun _ -> ())
// FCQRS reads optional settings from IConfiguration; this sample sets none.
let configuration = ConfigurationBuilder().Build()
// Events go to a SQLite file next to the program.
let connection = Fcqrs.connect DBType.Sqlite connectionString
let api = Fcqrs.actor configuration logging (Some connection) "accounts"

// Register the account rules; `accounts` sends commands to them.
let accounts =
    Fcqrs.aggregate api {
        Name = "Account"
        Initial = initial
        Decide = decide
        Fold = fold
        Snapshots = Default
        Passivation = PassivationPolicy.Default
    }

// Finish the aggregate startup. This program has no sagas.
Fcqrs.wireSagaStarters api []

// docs:register
// Register the workflow with the accounts it sends commands to. Registering
// it also resumes the transfers that were running when the process stopped.
let transfers = Workflows.register api (Transfer.definition accounts)
// docs:end

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

// Print a reply. Journaled tells whether FCQRS stored the event.
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

send alice (Open "Alice")
send alice (Deposit 100m)
send bob (Open "Bob")

// docs:transfer
let describeOutcome outcome =
    match outcome with
    | Completed -> "completed"
    | Refunded -> "refunded"
    | Refused reason -> $"refused: {reason}"

// Start a transfer from Alice and wait for it to end. A start with an ID that
// is already in the journal stores nothing, so a repeated request moves no
// money: the transfer's ID is the workflow's ID.
let transfer id target amount =
    let request = {
        Id = id
        Source = "alice"
        Target = target
        Amount = amount
    }
    let start = transfers.Start id request |> Async.RunSynchronously
    let repeated =
        match start with
        | StartResult.Started -> ""
        | StartResult.AlreadyStarted _ -> " (already started)"
    let ended =
        transfers.Result id (TimeSpan.FromSeconds 30.)
        |> Async.RunSynchronously
    match ended with
    | WorkflowOutcome.Finished outcome ->
        printfn $"Transfer {id}: {describeOutcome outcome}{repeated}"
    | other -> printfn $"Transfer {id} ended without a result: %A{other}"

transfer "t1" "bob" 30m
// Carol has no account, so this transfer comes back.
transfer "t2" "carol" 20m
// docs:end

// docs:repeat
// After a restart, a workflow sends a command again when its reply was not
// stored. Do the same by hand:
send bob (ReceiveTransfer("t1", "alice", 30m))
// docs:end

// Read one account's statement with SQL.
let printStatement (account: string) =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    use query = connection.CreateCommand()
    query.CommandText <-
        "SELECT version, entry, amount, balance FROM statement
         WHERE account = $account ORDER BY version"
    query.Parameters.AddWithValue("$account", account) |> ignore
    use rows = query.ExecuteReader()
    printfn $"\nStatement for {account}:"
    printfn "  version  entry                    amount  balance"
    while rows.Read() do
        let version, entry = rows.GetInt64 0, rows.GetString 1
        let amount, balance = rows.GetDecimal 2, rows.GetDecimal 3
        printfn $"  {version,7}  {entry,-24}{amount,7}{balance,9}"

// The statement follows the journal on its own; wait until it has read
// everything the transfers stored.
statement.CatchUpAsync().Wait()
printStatement "alice"
printStatement "bob"

statement.Dispose()
api.Stop().Wait()
