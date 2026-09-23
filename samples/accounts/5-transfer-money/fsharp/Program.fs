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

// FCQRS logs through Microsoft.Extensions.Logging; this sample stays quiet.
let logging = LoggerFactory.Create(fun _ -> ())
// FCQRS reads optional settings from IConfiguration; this sample sets none.
let configuration = ConfigurationBuilder().Build()
// Events go to a SQLite file next to the program.
let connection = Fcqrs.connect DBType.Sqlite connectionString
let api = Fcqrs.actor configuration logging (Some connection) "accounts"

// Register the account rules; `accounts` sends commands to them.
let accounts =
    Fcqrs.aggregate api
        { Name = "Account"
          Initial = initial
          Decide = decide
          Fold = fold
          Snapshots = Default
          Passivation = PassivationPolicy.Default }

// docs:register
// Register the saga with the accounts it sends commands to, then install its
// start rule. From now on, each stored TransferSent starts one transfer.
let transfers = Fcqrs.saga api (Transfer.definition accounts.Factory)
Fcqrs.wireSagaStarters api [ transfers ]
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
// A transfer ends when the target stores the money or the source gets it back.
let finished (message: IMessageWithCID) =
    match message with
    | :? Event<AccountEvent> as event ->
        match event.EventDetails with
        | TransferReceived _ | TransferRefunded _ -> true
        | _ -> false
    | _ -> false

// Ask Alice's account to send money, and wait until the saga has finished.
let transfer id target amount =
    let cid = Fcqrs.newCid ()
    // Subscribe first: the saga can finish before the reply arrives.
    use outcome = statement.Subscribe(cid, finished, 1)
    let command = SendTransfer(id, target, amount)
    let reply =
        accounts.Send cid alice command (fun _ -> true)
        |> Async.RunSynchronously
    show reply
    if reply.Journaled = Some true then
        outcome.Task.WaitAsync(TimeSpan.FromSeconds 30.).Wait()

transfer "t1" "bob" 30m
// Carol has no account, so this transfer comes back.
transfer "t2" "carol" 20m
// docs:end

// docs:repeat
// After a restart, a saga sends its last command again. Do the same by hand:
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

printStatement "alice"
printStatement "bob"

statement.Dispose()
api.Stop().Wait()
