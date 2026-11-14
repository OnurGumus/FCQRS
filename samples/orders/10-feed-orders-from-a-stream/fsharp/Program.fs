// docs:setup
open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Tasks
open Akka.Streams.Dsl
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
// the first reservation. It prints nothing, and a lock keeps it correct
// when two orders call it at once.
type Warehouse() =
    let calls = Dictionary<string, int>()
    let reservations = Dictionary<string, string>()
    interface IWarehouse with
        member _.Reserve(key, item, _) =
            lock calls (fun () ->
                if item = "gizmo" then
                    invalidOp "Out of stock"
                calls[key] <- (match calls.TryGetValue key with
                               | true, count -> count + 1
                               | _ -> 1)
                if calls[key] <= 2 then
                    invalidOp "The warehouse is busy"
                if not (reservations.ContainsKey key) then
                    reservations[key] <- $"r-{reservations.Count + 1}"
                Task.FromResult reservations[key])
        member _.Release(_, reservation) = Task.FromResult reservation
// docs:end

// docs:carrier
// A stand-in for the carrier. It does not take crates, and a call takes two
// seconds. With `stop`, the first call waits for a second one, and the
// second stops the process while both orders are inside `ship`.
type Carrier(stop: bool) =
    let shipments = Dictionary<string, string>()
    let mutable calls = 0
    interface ICarrier with
        member _.Ship(key, _, item) =
            task {
                if item = "crate" then
                    invalidOp "The carrier does not take crates"
                if stop then
                    if Interlocked.Increment &calls = 1 then
                        do! Task.Delay Timeout.Infinite
                    printfn
                        "  the process stops while two orders are being shipped"
                    exit 1
                do! Task.Delay 2000
                return
                    lock shipments (fun () ->
                        if not (shipments.ContainsKey key) then
                            shipments[key] <- $"s-{shipments.Count + 1}"
                        shipments[key])
            }
// docs:end

// docs:fraud
// A stand-in for the fraud check. It passes every order and prints nothing.
type FraudCheck() =
    interface IFraudCheck with
        member _.Check(_, _, _) = Task.FromResult true
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

// Put money in a customer's account.
let deposit customer amount =
    accounts.Send
        (Fcqrs.newCid ()) (Fcqrs.aggregateId customer) (Deposit amount)
        (fun _ -> true)
    |> Async.RunSynchronously
    |> ignore
    printfn $"Deposited {amount} to {customer}"

// Ada pays for the orders. The deposit comes before the workflow is
// registered, so the program reads the status of the resumed orders right
// after registering, while they are still shipping.
deposit "ada" 500m

// Run the program with --stop to stop it while two orders are shipping.
let stop = Environment.GetCommandLineArgs() |> Array.contains "--stop"

// Registering the workflow also resumes the orders that were running when
// the process last stopped.
let orders =
    Workflows.register
        api (definition (Warehouse()) (Carrier stop) accounts (FraudCheck()))
// docs:end

// docs:batch
// Five orders from ada, one per item.
let batch =
    [ "widget"; "gadget"; "gizmo"; "crate"; "sprocket" ]
    |> List.map (fun item -> {
        Customer = "ada"
        Item = item
        Quantity = 2
        UnitPrice = 40m
    })

// The order ID comes from the element, so an element that arrives again
// reaches the order it started before.
let idOf (request: OrderRequest) = $"o-{request.Item}"
// docs:end

// docs:start
// Orders that registering resumed are running before the stream starts.
for id in [ "o-widget"; "o-gadget" ] do
    match orders.Status id |> Async.RunSynchronously with
    | WorkflowStatus.Running pending ->
        let steps = String.Join(", ", pending)
        printfn $"{id} is running: {steps}"
    | _ -> ()

// Prints an outcome as the flow emits it.
let print (struct (id: string, outcome: WorkflowOutcome<OrderResult>)) =
    match outcome with
    | WorkflowOutcome.Finished result ->
        printfn $"{id} finished: {result.Status}"
    | WorkflowOutcome.Faulted error -> printfn $"{id} faulted: {error.Message}"
    | WorkflowOutcome.Cancelled -> printfn $"{id} was cancelled"

// Counts the orders between entering the flow and leaving it. This
// stream has no async boundary, so its stages run one at a time and
// plain counters are enough.
let mutable inFlow = 0
let mutable most = 0

// Start an order per element, at most two at once, and emit each outcome
// in the batch's order. An order that takes longer than 30 seconds fails
// the stream.
Source
    .From(batch)
    .Select(fun request ->
        inFlow <- inFlow + 1
        most <- max most inFlow
        request)
    .Via(WorkflowFlow.run orders idOf 2 (TimeSpan.FromSeconds 30.0))
    .Select(fun outcome ->
        inFlow <- inFlow - 1
        outcome)
    .RunForeach(print, api.Materializer)
    .Wait()

printfn $"At most {most} orders were in the stream at once"
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

printJournal "o-widget"
api.Stop().Wait()
// docs:end
