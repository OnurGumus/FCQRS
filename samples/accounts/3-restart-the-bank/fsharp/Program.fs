open System
open System.IO
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Actor
open FCQRS.Common
open FCQRS.FSharp
open Account

let database = Path.Combine(AppContext.BaseDirectory, "accounts.db")

// FCQRS logs through Microsoft.Extensions.Logging; this sample stays quiet.
let logging = LoggerFactory.Create(fun _ -> ())
// FCQRS reads optional settings from IConfiguration; this sample sets none.
let configuration = ConfigurationBuilder().Build()
// Events go to a SQLite file next to the program.
let connection = Fcqrs.connect DBType.Sqlite $"Data Source={database};"
let api = Fcqrs.actor configuration logging (Some connection) "accounts"

// docs:register
// Register the account rules and save a snapshot every 100 events.
let accounts =
    Fcqrs.aggregate api
        { Name = "Account"
          Initial = initial
          Decide = decide
          Fold = fold
          Snapshots = Every 100
          Passivation = PassivationPolicy.Default }
// docs:end

// Finish startup. This program has no sagas yet.
Fcqrs.wireSagaStarters api []

let describe event =
    match event with
    | Opened owner -> $"Opened for {owner}"
    | Deposited amount -> $"Deposited {amount}"
    | Withdrawn amount -> $"Withdrew {amount}"
    | Rejected reason -> $"Rejected: {reason}"

// docs:send
let alice = Fcqrs.aggregateId "alice"

// Send a command and wait for the account's reply.
let request command =
    accounts.Send (Fcqrs.newCid ()) alice command (fun _ -> true)
    |> Async.RunSynchronously

let show (reply: Event<AccountEvent>) =
    let description = describe reply.EventDetails
    printfn $"{description} (version {reply.Version})"

show (request (Open "Alice"))

// Deposit 10, 250 times, and print the last reply.
let replies = [ for _ in 1 .. 250 -> request (Deposit 10m) ]
show (List.last replies)
// docs:end

// Applications do not read these tables. This program reads them only to show
// what FCQRS stored.
let printTables () =
    use connection = new SqliteConnection($"Data Source={database}")
    connection.Open()
    let query (sql: string) =
        let command = connection.CreateCommand()
        command.CommandText <- sql
        command
    // FCQRS saves a snapshot in the background after it replies. Wait up to ten
    // seconds until the newest snapshot that the 100-event cadence calls for is stored.
    use caughtUp =
        query
            "SELECT (SELECT COALESCE(MAX(sequence_number), 0) FROM snapshot
                     WHERE persistence_id = 'Account/default-shard/alice')
                 >= (SELECT MAX(sequence_number) FROM journal
                     WHERE persistence_id = 'Account/default-shard/alice') / 100 * 100"
    let deadline = DateTime.UtcNow.AddSeconds 10.
    while (caughtUp.ExecuteScalar() :?> int64) = 0L && DateTime.UtcNow < deadline do
        Threading.Thread.Sleep 50
    use count =
        query "SELECT COUNT(*) FROM journal WHERE persistence_id = 'Account/default-shard/alice'"
    printfn $"\nJournal: {count.ExecuteScalar()} events for Account/default-shard/alice"
    use snapshots =
        query
            "SELECT sequence_number, json_extract(CAST(snapshot AS TEXT), '$.State') FROM snapshot
             WHERE persistence_id = 'Account/default-shard/alice' ORDER BY sequence_number"
    use rows = snapshots.ExecuteReader()
    printfn "Snapshots:"
    while rows.Read() do
        printfn "  version %d  %s" (rows.GetInt64 0) (rows.GetString 1)

printTables ()
api.Stop().Wait()
