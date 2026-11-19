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

// docs:continue
// This step is the bank's next release. On its first run, it copies the
// database step 5 wrote, so its journal starts with events without a memo.
let database = Path.Combine(AppContext.BaseDirectory, "accounts.db")
if not (File.Exists database) then
    // Step 5 builds into the same folder layout next to this step.
    let previous =
        AppContext.BaseDirectory.Replace("6-add-a-memo", "5-transfer-money")
    let step5 = Path.Combine(previous, "accounts.db")
    if not (File.Exists step5) then
        eprintfn "Run step 5 first: this step continues from its database."
        exit 1
    File.Copy(step5, database)
// docs:end

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

// Finish the aggregate startup, and register the transfer workflow.
Fcqrs.wireSagaStarters api []
let transfers = Workflows.register api (Transfer.definition accounts)

// docs:register
// The new statement has its own table and its own projection name. FCQRS has no
// progress for that name yet, so the projection starts from the first event.
do
    use connection = new SqliteConnection(connectionString)
    Statement.createTable connection

let store =
    SqlProjectionStore(
        ProjectionSqlDialect.Sqlite,
        Func<DbConnection>(fun () -> new SqliteConnection(connectionString)))
let options = TransactionalProjectionOptions("StatementV2", store)
let statement = Fcqrs.transactionalProjection api options Statement.handle
// docs:end

// docs:transfer
let describeOutcome outcome =
    match outcome with
    | Completed -> "completed"
    | Refunded -> "refunded"
    | Refused reason -> $"refused: {reason}"

// Start a transfer from Alice with a memo, and wait for it to end.
let transfer id target amount memo =
    let request = {
        Id = id
        Source = "alice"
        Target = target
        Amount = amount
        Memo = memo
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

transfer "t3" "bob" 25m (Some "rent")
// docs:end

// Applications do not read the journal. This program reads it only to show how
// Alice's transfers are stored: without a memo before this step, with one after.
let printTransfers () =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    use query = connection.CreateCommand()
    query.CommandText <-
        "SELECT sequence_number,
                json_remove(json_extract(CAST(message AS TEXT), '$.EventDetails'), '$.Case')
         FROM journal
         WHERE persistence_id = 'Account/default-shard/alice'
           AND json_extract(CAST(message AS TEXT), '$.EventDetails.Case') = 'TransferSent'
         ORDER BY sequence_number"
    use rows = query.ExecuteReader()
    printfn "\nTransfers stored for alice:"
    while rows.Read() do
        printfn "  %d  %s" (rows.GetInt64 0) (rows.GetString 1)

// Read one account's statement with SQL.
let printStatement (account: string) =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    use query = connection.CreateCommand()
    query.CommandText <-
        "SELECT version, entry, memo, amount, balance FROM statement_v2
         WHERE account = $account ORDER BY version"
    query.Parameters.AddWithValue("$account", account) |> ignore
    use rows = query.ExecuteReader()
    printfn $"\nStatement for {account}:"
    printfn "  version  entry                    memo   amount  balance"
    while rows.Read() do
        let version, entry = rows.GetInt64 0, rows.GetString 1
        let memo = if rows.IsDBNull 2 then "" else rows.GetString 2
        let amount, balance = rows.GetDecimal 3, rows.GetDecimal 4
        printfn $"  {version,7}  {entry,-24} {memo,-6}{amount,7}{balance,9}"

printTransfers ()
// The statement follows the journal on its own; wait until it has read
// everything, including the events step 5 stored.
statement.CatchUpAsync().Wait()
printStatement "alice"
printStatement "bob"

statement.Dispose()
api.Stop().Wait()
