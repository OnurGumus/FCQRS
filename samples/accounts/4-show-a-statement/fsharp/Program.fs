open System
open System.Data.Common
open System.IO
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Actor
open FCQRS.Common
open FCQRS.FSharp
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

// Finish startup. This program has no sagas yet.
Fcqrs.wireSagaStarters api []

// docs:register
// The statement table lives in the same SQLite file as the journal.
do
    use connection = new SqliteConnection(connectionString)
    Statement.createTable connection

// FCQRS passes each new journal event to Statement.handle, and commits the
// handler's rows and the projection's progress in one transaction.
let store =
    SqlProjectionStore(
        ProjectionSqlDialect.Sqlite,
        Func<DbConnection>(fun () -> new SqliteConnection(connectionString)))
let options = TransactionalProjectionOptions("Statement", store)
let statement = Fcqrs.transactionalProjection api options Statement.handle
// docs:end

let describe event =
    match event with
    | Opened owner -> $"Opened for {owner}"
    | Deposited amount -> $"Deposited {amount}"
    | Withdrawn amount -> $"Withdrew {amount}"
    | Rejected reason -> $"Rejected: {reason}"

// docs:send
let alice = Fcqrs.aggregateId "alice"

// Send a command and wait until the statement includes the event it stored.
let send command =
    let reply =
        Fcqrs.sendAwaiting
            statement accounts (Fcqrs.newCid ()) alice command (fun _ -> true)
        |> Async.RunSynchronously
    let description = describe reply.EventDetails
    printfn $"{description} (version {reply.Version})"

send (Open "Alice")
send (Deposit 100m)
send (Withdraw 30m)
send (Deposit 50m)
send (Withdraw 500m)
// docs:end

// docs:query
// The statement is an ordinary table: read it with SQL.
let printStatement () =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    use query = connection.CreateCommand()
    query.CommandText <-
        "SELECT version, entry, amount, balance FROM statement
         WHERE account = 'alice' ORDER BY version"
    use rows = query.ExecuteReader()
    printfn "\nStatement for alice:"
    printfn "  version  entry              amount  balance"
    while rows.Read() do
        let version, entry = rows.GetInt64 0, rows.GetString 1
        let amount, balance = rows.GetDecimal 2, rows.GetDecimal 3
        printfn $"  {version,7}  {entry,-18}{amount,7}{balance,9}"

printStatement ()
// docs:end

statement.Dispose()
api.Stop().Wait()
