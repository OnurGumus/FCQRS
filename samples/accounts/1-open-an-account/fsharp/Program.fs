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

// docs:startup
// FCQRS logs through Microsoft.Extensions.Logging; this sample stays quiet.
let logging = LoggerFactory.Create(fun _ -> ())
// FCQRS reads optional settings from IConfiguration; this sample sets none.
let configuration = ConfigurationBuilder().Build()
// Events go to a SQLite file next to the program.
let connection = Fcqrs.connect DBType.Sqlite $"Data Source={database};"
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
// docs:end

let describe event =
    match event with
    | Opened owner -> $"Opened for {owner}"
    | Deposited amount -> $"Deposited {amount}"

// docs:send
// Each account ID gets its own aggregate instance.
let alice = Fcqrs.aggregateId "alice"

// Send a command, wait for the stored event, and print it.
let send command =
    let reply =
        accounts.Send (Fcqrs.newCid ()) alice command (fun _ -> true)
        |> Async.RunSynchronously
    let description = describe reply.EventDetails
    printfn $"{description} (version {reply.Version})"

send (Open "Alice")
send (Deposit 100m)
send (Deposit 50m)
// docs:end

// Applications read stored events through projections (step 4). This reads the
// journal table directly only to show what FCQRS stored.
let printJournal () =
    use connection = new SqliteConnection($"Data Source={database}")
    connection.Open()
    use query = connection.CreateCommand()
    query.CommandText <-
        "SELECT sequence_number, json_extract(CAST(message AS TEXT), '$.EventDetails') FROM journal
         WHERE persistence_id = 'Account/default-shard/alice' ORDER BY sequence_number"
    use rows = query.ExecuteReader()
    printfn "\nJournal rows for Account/default-shard/alice:"
    while rows.Read() do
        printfn "  %d  %s" (rows.GetInt64 0) (rows.GetString 1)

printJournal ()
api.Stop().Wait()
