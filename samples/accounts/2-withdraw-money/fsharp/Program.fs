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

let describe event =
    match event with
    | Opened owner -> $"Opened for {owner}"
    | Deposited amount -> $"Deposited {amount}"
    | Withdrawn amount -> $"Withdrew {amount}"
    | Rejected reason -> $"Rejected: {reason}"

// docs:send
// Each account ID gets its own aggregate instance.
let alice = Fcqrs.aggregateId "alice"

// Send a command and return the account's reply.
let request command =
    accounts.Send (Fcqrs.newCid ()) alice command (fun _ -> true)

// Print a reply. Journaled tells whether FCQRS stored the event.
let show (reply: Event<AccountEvent>) =
    let stored = if reply.Journaled = Some true then "stored" else "not stored"
    let description = describe reply.EventDetails
    printfn $"{description} (version {reply.Version}, {stored})"

let send command = request command |> Async.RunSynchronously |> show

send (Open "Alice")
send (Deposit 100m)
send (Withdraw 30m)
send (Withdraw 500m)
send (Open "Alice")
// docs:end

// docs:together
// Two withdrawals of 60 arrive at the same moment.
let replies =
    [ request (Withdraw 60m); request (Withdraw 60m) ]
    |> Async.Parallel
    |> Async.RunSynchronously

// Print the stored one first.
replies
|> Array.sortBy (fun reply -> reply.Journaled <> Some true)
|> Array.iter show
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
