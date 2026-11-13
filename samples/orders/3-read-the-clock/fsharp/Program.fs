// docs:setup
open System
open System.Collections.Generic
open System.IO
open System.Threading
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
// A stand-in for the warehouse. It is busy for the first two calls with a
// key. It remembers keys, so a repeated call returns the first reservation.
type Warehouse() =
    let calls = Dictionary<string, int>()
    let reservations = Dictionary<string, string>()
    interface IWarehouse with
        member _.Reserve(key, item, quantity) =
            task {
                calls[key] <- (match calls.TryGetValue key with
                               | true, count -> count + 1
                               | _ -> 1)
                if calls[key] <= 2 then
                    printfn $"  warehouse busy (key {key})"
                    invalidOp "The warehouse is busy"
                if not (reservations.ContainsKey key) then
                    reservations[key] <- $"r-{reservations.Count + 1}"
                    printfn
                        $"  warehouse reserved {quantity} {item} (key {key})"
                return reservations[key]
            }
// docs:end

// docs:carrier
// A stand-in for the carrier.
type Carrier() =
    let shipments = Dictionary<string, string>()
    interface ICarrier with
        member _.Ship(key, reservation) =
            task {
                if not (shipments.ContainsKey key) then
                    shipments[key] <- $"s-{shipments.Count + 1}"
                    printfn $"  carrier shipped {reservation} (key {key})"
                return shipments[key]
            }
// docs:end

// docs:register
// Registering the workflow also resumes the orders that were running when
// the process last stopped.
let orders =
    Workflows.register api (definition (Warehouse()) (Carrier()))
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

// Read the order's status while it waits for its timer.
let rec waitForTimer () =
    match orders.Status "o-1" |> Async.RunSynchronously with
    | WorkflowStatus.Running steps as status
        when steps |> List.exists (fun step -> step.EndsWith " sleep") ->
        printfn $"o-1 status: {status}"
    | _ ->
        Thread.Sleep 50
        waitForTimer ()

waitForTimer ()

// Wait for the order to end. The workflow does not depend on this wait.
let outcome =
    orders.Result "o-1" (TimeSpan.FromSeconds 30.0) |> Async.RunSynchronously

match outcome with
| WorkflowOutcome.Finished result ->
    let text = $"reservation {result.Reservation}, shipment {result.Shipment}"
    printfn $"o-1 finished: {text}"
    let placed = result.PlacedAt.ToString "o"
    printfn $"  confirmation {result.Confirmation}, placed {placed}"
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
