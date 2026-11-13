// docs:setup
open System
open System.Collections.Generic
open System.IO
open System.Threading
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Actor
open FCQRS.Common
open FCQRS.FSharp
open FCQRS.Workflows
open Account
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
// A stand-in for the warehouse. It has no gizmos, and it is busy for the
// first two calls with a key. It remembers keys, so a repeated call returns
// the first reservation.
type Warehouse() =
    let calls = Dictionary<string, int>()
    let reservations = Dictionary<string, string>()
    let releases = HashSet<string>()
    interface IWarehouse with
        member _.Reserve(key, item, quantity) =
            task {
                if item = "gizmo" then
                    printfn $"  warehouse has no gizmo (key {key})"
                    invalidOp "Out of stock"
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
        member _.Release(key, reservation) =
            task {
                if releases.Add key then
                    printfn $"  warehouse released {reservation} (key {key})"
                return reservation
            }
// docs:end

// docs:carrier
// A stand-in for the carrier. It does not take crates. A call takes two
// seconds. With `crash`, the process stops after the carrier ships and
// before FCQRS stores the result.
type Carrier(crash: bool) =
    let shipments = Dictionary<string, string>()
    interface ICarrier with
        member _.Ship(key, reservation, item) =
            task {
                if item = "crate" then
                    printfn $"  carrier does not take crates (key {key})"
                    invalidOp "The carrier does not take crates"
                Thread.Sleep 2000
                if not (shipments.ContainsKey key) then
                    shipments[key] <- $"s-{shipments.Count + 1}"
                    printfn $"  carrier shipped {reservation} (key {key})"
                if crash then
                    printfn "  the process stops before this step is stored"
                    exit 1
                return shipments[key]
            }
// docs:end

// docs:register
// Register the account rules; the workflow sends commands to them.
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

// Run the program with --crash to stop it in the middle of shipping.
let crash = Environment.GetCommandLineArgs() |> Array.contains "--crash"

// Registering the workflow also resumes the orders that were running when
// the process last stopped.
let orders =
    Workflows.register api (definition (Warehouse()) (Carrier crash) accounts)
// docs:end

// docs:start
// Put money in a customer's account.
let deposit customer amount =
    accounts.Send
        (Fcqrs.newCid ()) (Fcqrs.aggregateId customer) (Deposit amount)
        (fun _ -> true)
    |> Async.RunSynchronously
    |> ignore
    printfn $"Deposited {amount} to {customer}"

// Start an order. A start with an ID that is already in the journal stores
// nothing and returns the order's status.
let start id request =
    match orders.Start id request |> Async.RunSynchronously with
    | StartResult.Started -> printfn $"Started {id}"
    | StartResult.AlreadyStarted status ->
        printfn $"{id} is already started ({status})"

// Start an order, send it an approval if given one, and wait for its end.
let place id customer item quantity approval =
    let request = {
        Customer = customer
        Item = item
        Quantity = quantity
        UnitPrice = 40m
    }
    start id request
    match approval with
    | Some approve ->
        Workflows.signal orders id "approval" approve |> Async.RunSynchronously
    | None -> ()
    let outcome =
        orders.Result id (TimeSpan.FromSeconds 30.0) |> Async.RunSynchronously
    match outcome with
    | WorkflowOutcome.Finished result when result.Status = "shipped" ->
        printfn $"{id} finished: shipped as {result.Shipment}"
    | WorkflowOutcome.Finished result ->
        printfn $"{id} finished: {result.Status}"
    | WorkflowOutcome.Faulted error -> printfn $"{id} faulted: {error.Message}"
    | other -> printfn $"{id} ended: {other}"

deposit "ada" 1500m
deposit "bob" 50m
// The warehouse has no gizmos.
place "o-5" "ada" "gizmo" 1 None
// The carrier does not take crates.
place "o-6" "ada" "crate" 1 None

// o-7 costs 1200 and waits for an approval that this program never sends.
// Step 8 finds it still waiting.
let big = { Customer = "ada"; Item = "widget"; Quantity = 30; UnitPrice = 40m }
start "o-7" big

let rec waitForApproval () =
    match orders.Status "o-7" |> Async.RunSynchronously with
    | WorkflowStatus.Running steps as status
        when steps |> List.exists (fun step -> step.EndsWith " approval") ->
        printfn $"o-7 status: {status}"
    | _ ->
        Thread.Sleep 50
        waitForApproval ()

waitForApproval ()
// docs:end

// docs:journal
// Applications do not read this table. This program reads it only to show
// what FCQRS stored for an order.
let printJournal id =
    use connection = new SqliteConnection($"Data Source={database}")
    connection.Open()
    let command = connection.CreateCommand()
    command.CommandText <-
        "SELECT sequence_number, manifest FROM journal
         WHERE persistence_id = $id
         ORDER BY sequence_number"
    let persistenceId = $"order/default-shard/{id}"
    command.Parameters.AddWithValue("$id", persistenceId) |> ignore
    use rows = command.ExecuteReader()
    printfn $"Journal of order/default-shard/{id}:"
    while rows.Read() do
        // The manifest names the stored type, for example
        // "FCQRS.Workflows.WorkflowRecord+Started, FCQRS.Workflows".
        let manifest = rows.GetString 1
        let record = manifest.Split(',').[0].Split('+').[1]
        let number = rows.GetInt64 0
        printfn $"  {number} {record}"

printJournal "o-6"
api.Stop().Wait()
// docs:end
