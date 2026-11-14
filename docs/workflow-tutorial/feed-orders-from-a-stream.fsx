(**
---
title: 10. Feed orders from a stream
category: Workflow tutorial
categoryindex: 3
index: 10
---
*)
(*** hide ***)
#r "nuget: FCQRS.Workflows, 7.5.0"
#load "../../samples/orders/10-feed-orders-from-a-stream/fsharp/Account.fs"
#load "../../samples/orders/10-feed-orders-from-a-stream/fsharp/Order.fs"

(**
# Step 10: Feed orders from a stream

Until now the program started each order with a call of its own. Orders can also arrive as a
stream: messages from a queue, or rows that another system writes. This step puts five orders
through an Akka.Streams flow that starts one workflow per element, two at a time. Then it stops the
process while two orders are shipping and runs the batch again, to show what resumes and what does
not.

This page changes the program from [step 9](guard-the-change.html). The workflow does not change:
`Order.fs` (`Order.cs`) and `Account.fs` (`Account.cs`) stay as they are. The journal starts empty.

## A batch of orders

In `Program.fs` (`Program.cs`), add the batch after the registration:

<!-- sample: orders/10-feed-orders-from-a-stream/fsharp Program.fs batch -->
```fsharp
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
```

<div class="cs-alt"></div>

<!-- sample: orders/10-feed-orders-from-a-stream/csharp Program.cs batch -->
```csharp
// Five orders from ada, one per item.
var batch = new[] { "widget", "gadget", "gizmo", "crate", "sprocket" }
    .Select(item => new OrderRequest("ada", item, 2, 40m))
    .ToList();

// The order ID comes from the element, so an element that arrives again
// reaches the order it started before.
string IdOf(OrderRequest request) => $"o-{request.Item}";
```

The five orders are ada's, one per item, and each costs 80. Under the stand-ins, the warehouse has
no `gizmo` and the carrier does not take a `crate`, so `o-gizmo` ends as "out of stock" and
`o-crate` faults. The other three ship.

`idOf` (`IdOf` in C#) computes the order ID from the element. A real element, such as a message
from a queue, carries an order number of its own; here the item stands in for it. The ID must come
from the element, not from a counter or a new GUID, so that an element that arrives again names the
order it started before. Its start then stores nothing.

## Run the batch through a flow

The setup opens `Akka.Streams.Dsl`, which defines `Source`. The F# program also opens
`System.Threading.Tasks` for the stand-ins below:

<!-- sample: orders/10-feed-orders-from-a-stream/fsharp Program.fs setup -->
```fsharp
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
```

<div class="cs-alt"></div>

<!-- sample: orders/10-feed-orders-from-a-stream/csharp Program.cs setup -->
```csharp
using Akka.Streams.Dsl;
using FCQRS.Workflows;
using FCQRS.Workflows.CSharp;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using static FCQRS.CSharp;

var database = Path.Combine(AppContext.BaseDirectory, "orders.db");

// FCQRS logs through Microsoft.Extensions.Logging; this program stays quiet.
var logging = LoggerFactory.Create(_ => { });
// FCQRS reads optional settings from IConfiguration; this program sets none.
var configuration = new ConfigurationBuilder().Build();
// The journal is a SQLite file next to the program.
var actor = ActorApi.Create(
    configuration, logging, $"Data Source={database};", "orders");
```

After the batch, replace the functions and the lines that placed orders with the stream. The
stream starts the orders and waits for their outcomes, so `start` and `place` (`Start` and `Place`)
go. `deposit` moves to the registration, shown below:

<!-- sample: orders/10-feed-orders-from-a-stream/fsharp Program.fs start -->
```fsharp
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

// Counts the orders between entering the flow and leaving it. The stages
// of one stream run one at a time, so plain counters are enough.
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
```

<div class="cs-alt"></div>

<!-- sample: orders/10-feed-orders-from-a-stream/csharp Program.cs start -->
```csharp
// Orders that registering resumed are running before the stream starts.
foreach (var id in new[] { "o-widget", "o-gadget" })
{
    if (await orders.StatusAsync(id) is WorkflowStatus.Running running)
    {
        var steps = string.Join(", ", running.pending);
        Console.WriteLine($"{id} is running: {steps}");
    }
}

// Prints an outcome as the flow emits it.
void Print(WorkflowEnded<OrderResult> ended)
{
    if (ended.IsFinished)
        Console.WriteLine($"{ended.Id} finished: {ended.Result.Status}");
    else if (ended.IsFaulted)
        Console.WriteLine($"{ended.Id} faulted: {ended.Error!.Message}");
    else
        Console.WriteLine($"{ended.Id} was cancelled");
}

// Counts the orders between entering the flow and leaving it. The stages
// of one stream run one at a time, so plain counters are enough.
var inFlow = 0;
var most = 0;

// Start an order per element, at most two at once, and emit each outcome
// in the batch's order. An order that takes longer than 30 seconds fails
// the stream.
await Source
    .From(batch)
    .Select(request =>
    {
        inFlow++;
        most = Math.Max(most, inFlow);
        return request;
    })
    .Via(WorkflowFlow.Run(orders, IdOf, 2, TimeSpan.FromSeconds(30)))
    .Select(ended =>
    {
        inFlow--;
        return ended;
    })
    .RunForeach(Print, actor.Materializer);

Console.WriteLine($"At most {most} orders were in the stream at once");
```

The program first prints the status of `o-widget` and `o-gadget` if they are running. On the first
run nothing has started yet, so it prints nothing.

`WorkflowFlow.run orders idOf 2 timeout` (`WorkflowFlow.Run(orders, IdOf, 2, timeout)` in C#) is a
flow from `OrderRequest` to an outcome. For each element it computes the ID, starts the order, and
waits until the order ends:

- At most two elements are between their start and their outcome, so at most two orders run at
  once. The third element enters when an earlier one leaves.
- Outcomes leave in the batch's order, even when a later order ends first.
- A faulted order is an element like a finished one: `WorkflowOutcome.Faulted` in F#, `IsFaulted`
  in C#. It does not fail the stream.
- An order that does not end within 30 seconds fails the stream with a `TimeoutException`. The
  order itself carries on.

The `Select` before the flow counts each element that enters it, and the one after counts each
outcome that leaves. Akka.Streams runs the stages of this stream one at a time, so the counters need
no lock. `RunForeach` runs the stream with the actor system's materializer and calls `print`
(`Print`) for each outcome; the program waits until the stream completes.

At the end, the program prints the journal of `o-widget` instead of `o-7`:

<!-- sample: orders/10-feed-orders-from-a-stream/fsharp Program.fs journal -->
```fsharp
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
```

<div class="cs-alt"></div>

<!-- sample: orders/10-feed-orders-from-a-stream/csharp Program.cs journal -->
```csharp
// Applications do not read this table. This program reads it only to show
// what FCQRS stored for an order.
void PrintJournal(string id)
{
    using var connection = new SqliteConnection($"Data Source={database}");
    connection.Open();
    var command = connection.CreateCommand();
    command.CommandText = """
        SELECT sequence_number, manifest FROM journal
        WHERE persistence_id = $id
        ORDER BY sequence_number
        """;
    command.Parameters.AddWithValue("$id", $"order/default-shard/{id}");
    using var rows = command.ExecuteReader();
    Console.WriteLine($"Journal of order/default-shard/{id}:");
    while (rows.Read())
    {
        // The manifest names the stored type, for example
        // "FCQRS.Workflows.WorkflowRecord+Started, FCQRS.Workflows".
        var record = rows.GetString(1).Split(',')[0].Split('+')[1];
        Console.WriteLine($"  {rows.GetInt64(0)} {record}");
    }
}

PrintJournal("o-widget");

await actor.Stop();
```

## Make the stand-ins quiet

Two orders now call the stand-ins at the same time. The stand-ins stop printing, because the lines
of two orders would mix in a different order on every run. They also lock their dictionaries,
because two calls can change them at once:

<!-- sample: orders/10-feed-orders-from-a-stream/fsharp Program.fs warehouse -->
```fsharp
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
```

<div class="cs-alt"></div>

<!-- sample: orders/10-feed-orders-from-a-stream/csharp Program.cs warehouse -->
```csharp
// A stand-in for the warehouse. It has no gizmos, and it is busy for the
// first two calls with a key. It remembers keys, so a repeated call returns
// the first reservation. It prints nothing, and a lock keeps it correct
// when two orders call it at once.
public sealed class Warehouse : IWarehouse
{
    private readonly Dictionary<string, int> calls = new();
    private readonly Dictionary<string, string> reservations = new();

    public Task<string> Reserve(string key, string item, int quantity)
    {
        lock (calls)
        {
            if (item == "gizmo")
                throw new InvalidOperationException("Out of stock");
            calls[key] = calls.GetValueOrDefault(key) + 1;
            if (calls[key] <= 2)
                throw new InvalidOperationException("The warehouse is busy");
            if (!reservations.ContainsKey(key))
                reservations[key] = $"r-{reservations.Count + 1}";
            return Task.FromResult(reservations[key]);
        }
    }

    public Task<string> Release(string key, string reservation) =>
        Task.FromResult(reservation);
}
```

The carrier's `crash` becomes `stop`. With `stop`, the first `ship` call waits and the second ends
the process. At that moment `o-widget` and `o-gadget` have each stored that `ship` started, and
neither has a result. Because the carrier waits for the second call, this is the state on every
machine. Without `stop`, a call takes two seconds; `Task.Delay` replaces `Thread.Sleep`, so two
calls that wait do not hold two threads:

<!-- sample: orders/10-feed-orders-from-a-stream/fsharp Program.fs carrier -->
```fsharp
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
```

<div class="cs-alt"></div>

<!-- sample: orders/10-feed-orders-from-a-stream/csharp Program.cs carrier -->
```csharp
// A stand-in for the carrier. It does not take crates, and a call takes two
// seconds. With `stop`, the first call waits for a second one, and the
// second stops the process while both orders are inside `ship`.
public sealed class Carrier(bool stop) : ICarrier
{
    private readonly Dictionary<string, string> shipments = new();
    private int calls;

    public async Task<string> Ship(string key, string reservation, string item)
    {
        if (item == "crate")
            throw new InvalidOperationException(
                "The carrier does not take crates");
        if (stop)
        {
            if (Interlocked.Increment(ref calls) == 1)
                await Task.Delay(Timeout.Infinite);
            Console.WriteLine(
                "  the process stops while two orders are being shipped");
            Environment.Exit(1);
        }
        await Task.Delay(2000);
        lock (shipments)
        {
            if (!shipments.ContainsKey(key))
                shipments[key] = $"s-{shipments.Count + 1}";
            return shipments[key];
        }
    }
}
```

The fraud check passes every order, as before, and prints nothing:

<!-- sample: orders/10-feed-orders-from-a-stream/fsharp Program.fs fraud -->
```fsharp
// A stand-in for the fraud check. It passes every order and prints nothing.
type FraudCheck() =
    interface IFraudCheck with
        member _.Check(_, _, _) = Task.FromResult true
```

<div class="cs-alt"></div>

<!-- sample: orders/10-feed-orders-from-a-stream/csharp Program.cs fraud -->
```csharp
// A stand-in for the fraud check. It passes every order and prints nothing.
public sealed class FraudCheck : IFraudCheck
{
    public Task<bool> Check(string key, string customer, decimal total) =>
        Task.FromResult(true);
}
```

Replace the registration. It reads `--stop`, and it deposits 500 for ada before it registers the
workflow:

<!-- sample: orders/10-feed-orders-from-a-stream/fsharp Program.fs register -->
```fsharp
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
```

<div class="cs-alt"></div>

<!-- sample: orders/10-feed-orders-from-a-stream/csharp Program.cs register -->
```csharp
// Register the account rules; the workflow sends commands to them.
var accounts = new Account().Init(actor);

// Finish the aggregate startup. This program has no sagas.
ActorWiring.InitSagaStarterEmpty(actor);

// Put money in a customer's account.
async Task Deposit(string customer, decimal amount)
{
    await accounts.Handler(_ => true, Values.NewCID(),
        Values.CreateAggregateId(customer), new Deposit(amount));
    Console.WriteLine($"Deposited {amount} to {customer}");
}

// Ada pays for the orders. The deposit comes before the workflow is
// registered, so the program reads the status of the resumed orders right
// after registering, while they are still shipping.
await Deposit("ada", 500m);

// Run the program with --stop to stop it while two orders are shipping.
var stop = args.Contains("--stop");

// Registering the workflow also resumes the orders that were running when
// the process last stopped.
var orders = WorkflowHost.Register(actor, new OrderWorkflow(
    new Warehouse(), new Carrier(stop), accounts.Factory, new FraudCheck()));
```

The deposit comes before the workflow's registration because of the second run. Registering
resumes the orders that were running, and their `ship` call takes two seconds. The program asks
for their status right after registering, so it finds them still in `ship`. A deposit between the
two would delay the question.

## Run it with --stop

Delete `bin/Debug/net11.0/orders.db`, then run the program with `--stop`:

```text
dotnet run -- --stop
```

```text
Deposited 500 to ada
  the process stops while two orders are being shipped
```

The stream took `widget` and `gadget`, its limit of two. Both orders reserved stock, paid, waited
for packing, and called the carrier, and the second call stopped the process. The program printed
no outcome, because no order had ended.

## Run it again

Keep `orders.db` and run the program without the flag:

```text
dotnet run
```

```text
Deposited 500 to ada
o-widget is running: 6 ship
o-gadget is running: 6 ship
o-widget finished: shipped
o-gadget finished: shipped
o-gizmo finished: out of stock
o-crate faulted: The carrier does not take crates
o-sprocket finished: shipped
At most 2 orders were in the stream at once
Journal of order/default-shard/o-widget:
  1 Started
  2 StepScheduled
  3 StepCompleted
  4 StepCompleted
  5 StepCompleted
  6 StepCompleted
  7 StepScheduled
  8 StepCompleted
  9 StepScheduled
  10 StepCompleted
  11 StepScheduled
  12 StepCompleted
  13 StepScheduled
  14 StepCompleted
  15 Finished
```

Registering the workflow resumed `o-widget` and `o-gadget`. Both were running at `ship`, position
6, before the stream handed over any element.

The stream did not resume. Nothing stored which elements it had taken, so the program fed the whole
batch again. For `widget` and `gadget`, the start found an order with that ID and stored nothing,
and the flow waited for the resumed order to end. The other three elements started new orders.

The outcomes came in the batch's order. `o-crate` faulted at the carrier and was emitted like the
others, and the stream went on to `o-sprocket`. At most two orders were in the stream at once.

The journal of `o-widget` has one `Started` record: the element fed again stored nothing. Record 6
is the `version` step from step 9, which gave the new order 2. Record 13 is `ship`'s start from the
first run, and record 14 its result from the second.

## What the stream does not do

The stream is not durable. When the process stops, the orders resume and the stream does not. This
program could feed the batch again because the batch is in its code. A real source must replay from
an offset of its own: a queue delivers again the messages that were not acknowledged, and a reader
of a file stores how far it got. Acknowledge an element, or store the offset, only after the flow
emits its outcome.

An element that arrives again reaches its order through the ID. If the order is still running, the
flow waits for it; if it has ended, the flow emits the stored outcome. Either way the start stores
nothing.

When the source needs only the start stored, not the outcome, `WorkflowFlow.start`
(`WorkflowFlow.Start` in C#) emits each element's ID as soon as its start is stored.

## What you built

The order workflow now reserves stock with retries, takes its time and confirmation number from
steps, waits for an approval signal, charges an aggregate, compensates when it cannot ship, catches
the failure it has an answer for, and survives a process that stops in the middle of a step and a
change to its own code. A stream feeds it orders, two at a time, and an element that arrives again
reaches the order it started.

The [workflow concept page](../concepts/workflows.html) covers what the tutorial left out: running
steps together, repeating steps, child workflows, cancellation, and the C# API in full.
[Write a workflow](../how-to/write-a-workflow.html) is the same material as a task guide, and
[Observability](../how-to/observability.html) shows the traces, metrics, and logs that each order
produces.

*)

(*** hide ***)
printfn "Feed orders from a stream example checked."
