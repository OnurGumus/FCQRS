// docs:setup
open System
open System.Collections.Generic
open System.IO
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Actor
open FCQRS.FSharp
open FCQRS.Workflows
open Order

let database = Path.Combine(AppContext.BaseDirectory, "orders.db")

// FCQRS logs through Microsoft.Extensions.Logging; this program stays quiet.
let logging = LoggerFactory.Create(fun _ -> ())
// FCQRS reads optional settings from IConfiguration; this program sets none.
let configuration = ConfigurationBuilder().Build()
// The journal is a SQLite file next to the program.
let connection = Fcqrs.connect DBType.Sqlite $"Data Source={database};"
let api = Fcqrs.actor configuration logging (Some connection) "orders"
// docs:end

// docs:warehouse
// A stand-in for the warehouse. It remembers keys, so a repeated call
// returns the first reservation instead of reserving again.
type Warehouse() =
    let reservations = Dictionary<string, string>()
    interface IWarehouse with
        member _.Reserve(key, item, quantity) =
            task {
                if not (reservations.ContainsKey key) then
                    reservations[key] <- $"r-{reservations.Count + 1}"
                    printfn
                        $"  warehouse reserved {quantity} {item} (key {key})"
                return reservations[key]
            }
// docs:end

// docs:register
// Registering the workflow also resumes the orders that were running when
// the process last stopped.
let orders = Workflows.register api (definition (Warehouse()))
// docs:end

// docs:start
let request = {
    Customer = "ada"
    Item = "widget"
    Quantity = 2
    UnitPrice = 40m
}

// Start an order. A start with an ID that is already in the journal stores
// nothing and returns the order's status.
let start id =
    match orders.Start id request |> Async.RunSynchronously with
    | StartResult.Started -> printfn $"Started {id}"
    | StartResult.AlreadyStarted status ->
        printfn $"{id} is already started ({status})"

start "o-1"

// Wait for the order to end. The workflow does not depend on this wait.
let outcome =
    orders.Result "o-1" (TimeSpan.FromSeconds 30.0) |> Async.RunSynchronously

match outcome with
| WorkflowOutcome.Finished result ->
    printfn $"o-1 finished: reservation {result.Reservation}"
| other -> printfn $"o-1 ended: {other}"

start "o-1"
// docs:end

// docs:journal
// Applications do not read this table. This program reads it only to show
// what FCQRS stored for o-1.
let printJournal () =
    use connection = new SqliteConnection($"Data Source={database}")
    connection.Open()
    let command = connection.CreateCommand()
    command.CommandText <-
        "SELECT sequence_number, manifest FROM journal
         WHERE persistence_id = 'order/default-shard/o-1'
         ORDER BY sequence_number"
    use rows = command.ExecuteReader()
    printfn "Journal of order/default-shard/o-1:"
    while rows.Read() do
        // The manifest names the stored type, for example
        // "FCQRS.Workflows.WorkflowRecord+Started, FCQRS.Workflows".
        let manifest = rows.GetString 1
        let record = manifest.Split(',').[0].Split('+').[1]
        let number = rows.GetInt64 0
        printfn $"  {number} {record}"

printJournal ()
api.Stop().Wait()
// docs:end
