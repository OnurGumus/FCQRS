(**
---
title: 3. Read the clock
category: Workflow tutorial
categoryindex: 3
index: 3
---
*)
(*** hide ***)
#r "nuget: FCQRS.Workflows, 7.5.0"
#load "../../samples/orders/3-read-the-clock/fsharp/Order.fs"

(**
# Step 3: Read the clock

An order needs a time and a confirmation number, and after packing it ships. This step adds both,
and with them the rule that matters most when you write a workflow: the body must be
deterministic.

This page changes the program from [step 2](retry-the-warehouse.html).

## Why the body cannot read the clock

When FCQRS recovers an order, it runs the body again from the start and gives each finished step
its stored result. For that to work, the body must ask for the same steps in the same order every
time it runs. A body that reads `DateTime.UtcNow` gets a different time on every run, and a body
that branches on that time can ask for different steps on replay than it did the first time.

So the body reads the time, makes GUIDs, and draws random numbers only through steps. `now`,
`newGuid`, and `random` are steps whose value FCQRS stores the first time and replays after that.
The same holds for anything else that can change between runs, such as configuration or a database
read: get it in an activity.

FCQRS cannot detect a body that breaks this rule. The replay then runs with different values than
the original run.

## Add the shipping step

In `Order.fs` (`Order.cs`), the result gets three more fields:

<!-- sample: orders/3-read-the-clock/fsharp Order.fs messages -->
```fsharp
module Order

open System
open System.Threading.Tasks
open FCQRS.Common
open FCQRS.Workflows

// What an order workflow is started with. FCQRS stores it in the journal.
type OrderRequest = {
    Customer: string
    Item: string
    Quantity: int
    UnitPrice: decimal
}

// What the workflow returns. FCQRS stores it too.
type OrderResult = {
    Reservation: string
    Shipment: string
    Confirmation: Guid
    PlacedAt: DateTime
}
```

<div class="cs-alt"></div>

<!-- sample: orders/3-read-the-clock/csharp Order.cs messages -->
```csharp
using FCQRS.Workflows;
using FCQRS.Workflows.CSharp;
using static FCQRS.CSharp;

// What an order workflow is started with. FCQRS stores it in the journal.
public sealed record OrderRequest(
    string Customer, string Item, int Quantity, decimal UnitPrice);

// What the workflow returns. FCQRS stores it too.
public sealed record OrderResult(
    string Reservation, string Shipment, Guid Confirmation, DateTime PlacedAt);
```

Add the carrier after the warehouse:

<!-- sample: orders/3-read-the-clock/fsharp Order.fs services -->
```fsharp
// The warehouse is an external system. The key is the same for every
// attempt of one step, so the warehouse can recognise a repeated call.
type IWarehouse =
    abstract Reserve:
        key: string * item: string * quantity: int -> Task<string>

// The carrier is another external system. It returns a shipment ID.
type ICarrier =
    abstract Ship: key: string * reservation: string -> Task<string>
```

<div class="cs-alt"></div>

<!-- sample: orders/3-read-the-clock/csharp Order.cs services -->
```csharp
// The warehouse is an external system. The key is the same for every
// attempt of one step, so the warehouse can recognise a repeated call.
public interface IWarehouse
{
    Task<string> Reserve(string key, string item, int quantity);
}

// The carrier is another external system. It returns a shipment ID.
public interface ICarrier
{
    Task<string> Ship(string key, string reservation);
}
```

Replace the workflow:

<!-- sample: orders/3-read-the-clock/fsharp Order.fs workflow -->
```fsharp
// "order" is stored with every instance. It must not change.
let definition (warehouse: IWarehouse) (carrier: ICarrier) =
    Workflow.define "order" (fun (request: OrderRequest) ->
        workflow {
            // A failed attempt is not stored. FCQRS tries again until an
            // attempt succeeds or the policy gives up.
            let! reservation =
                activity "reserve-stock" reserveRetry (fun attempt ->
                    warehouse.Reserve(
                        attempt.IdempotencyKey, request.Item, request.Quantity))
            // The body must give the same answers on every replay, so it
            // reads the clock and makes GUIDs only through steps. FCQRS
            // stores their values and replays them.
            let! placedAt = now
            let! confirmation = newGuid
            // Packing takes two seconds. The timer's due time is stored, so
            // a restart does not start the wait again.
            do! sleep (TimeSpan.FromSeconds 2.0)
            let! shipment =
                activity "ship" (Retry.times 3) (fun attempt ->
                    carrier.Ship(attempt.IdempotencyKey, reservation))
            return {
                Reservation = reservation
                Shipment = shipment
                Confirmation = confirmation
                PlacedAt = placedAt
            }
        })
```

<div class="cs-alt"></div>

<!-- sample: orders/3-read-the-clock/csharp Order.cs workflow -->
```csharp
// "order" is stored with every instance. It must not change.
public sealed class OrderWorkflow(IWarehouse warehouse, ICarrier carrier)
    : Workflow<OrderRequest, OrderResult>("order")
{
    // Up to five attempts, 200 ms apart.
    private static readonly RetryPolicy ReserveRetry = Retry.times(5)
        .WithSchedule(RetrySchedules.Fixed(TimeSpan.FromMilliseconds(200)));

    public override async WorkflowTask<OrderResult> Run(
        WorkflowContext ctx, OrderRequest request)
    {
        // A failed attempt is not stored. FCQRS tries again until an
        // attempt succeeds or the policy gives up.
        var reservation = await ctx.Activity("reserve-stock", ReserveRetry,
            attempt => warehouse.Reserve(
                attempt.IdempotencyKey, request.Item, request.Quantity));
        // The body must give the same answers on every replay, so it
        // reads the clock and makes GUIDs only through steps. FCQRS
        // stores their values and replays them.
        var placedAt = await ctx.Now();
        var confirmation = await ctx.NewGuid();
        // Packing takes two seconds. The timer's due time is stored, so
        // a restart does not start the wait again.
        await ctx.Sleep(TimeSpan.FromSeconds(2));
        var shipment = await ctx.Activity("ship", Retry.times(3),
            attempt => carrier.Ship(attempt.IdempotencyKey, reservation));
        return new OrderResult(reservation, shipment, confirmation, placedAt);
    }
}
```

The body now has five steps, at positions 0 to 4: `reserve-stock`, `now`, `newGuid`, `sleep`, and
`ship`. `sleep` is a **timer**. FCQRS stores its due time when the body reaches it, so a process
that restarts during the wait finishes the remaining time, not a new two seconds. A timer holds no
thread while it waits.

## Run the shipping step

In `Program.fs` (`Program.cs`), the program's opening lines get `System.Threading` in F# and
`FCQRS.Workflows` in C#, for the loop that reads the status:

<!-- sample: orders/3-read-the-clock/fsharp Program.fs setup -->
```fsharp
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
```

<div class="cs-alt"></div>

<!-- sample: orders/3-read-the-clock/csharp Program.cs setup -->
```csharp
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

Add a stand-in carrier. In F#, put it after the warehouse. In C#, put it at the end of the file:

<!-- sample: orders/3-read-the-clock/fsharp Program.fs carrier -->
```fsharp
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
```

<div class="cs-alt"></div>

<!-- sample: orders/3-read-the-clock/csharp Program.cs carrier -->
```csharp
// A stand-in for the carrier.
public sealed class Carrier : ICarrier
{
    private readonly Dictionary<string, string> shipments = new();

    public Task<string> Ship(string key, string reservation)
    {
        if (!shipments.ContainsKey(key))
        {
            shipments[key] = $"s-{shipments.Count + 1}";
            Console.WriteLine($"  carrier shipped {reservation} (key {key})");
        }
        return Task.FromResult(shipments[key]);
    }
}
```

Give the workflow the carrier when you register it:

<!-- sample: orders/3-read-the-clock/fsharp Program.fs register -->
```fsharp
// Registering the workflow also resumes the orders that were running when
// the process last stopped.
let orders =
    Workflows.register api (definition (Warehouse()) (Carrier()))
```

<div class="cs-alt"></div>

<!-- sample: orders/3-read-the-clock/csharp Program.cs register -->
```csharp
// Registering the workflow also resumes the orders that were running when
// the process last stopped.
var orders = WorkflowHost.Register(
    actor, new OrderWorkflow(new Warehouse(), new Carrier()));
```

Replace the code that starts the order. It now reads the order's status while the timer runs, and
prints the new fields:

<!-- sample: orders/3-read-the-clock/fsharp Program.fs start -->
```fsharp
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
```

<div class="cs-alt"></div>

<!-- sample: orders/3-read-the-clock/csharp Program.cs start -->
```csharp
var request = new OrderRequest("ada", "widget", 2, 40m);

// Start an order. A start with an ID that is already in the journal stores
// nothing and returns false.
async Task Start(string id)
{
    if (await orders.StartAsync(id, request))
        Console.WriteLine($"Started {id}");
    else
        Console.WriteLine(
            $"{id} is already started ({await orders.StatusAsync(id)})");
}

await Start("o-1");

// Read the order's status while it waits for its timer.
while (true)
{
    var status = await orders.StatusAsync("o-1");
    if (status is WorkflowStatus.Running running
        && running.pending.Any(step => step.EndsWith(" sleep")))
    {
        Console.WriteLine($"o-1 status: {status}");
        break;
    }
    await Task.Delay(50);
}

// Wait for the order to end. The workflow does not depend on this wait.
var result = await orders.ResultAsync("o-1", TimeSpan.FromSeconds(30));
Console.WriteLine($"o-1 finished: reservation {result.Reservation}, "
    + $"shipment {result.Shipment}");
Console.WriteLine(
    $"  confirmation {result.Confirmation}, placed {result.PlacedAt:o}");

await Start("o-1");
```

## Run it

Delete `bin/Debug/net11.0/orders.db`, then run `dotnet run`:

```text
Started o-1
  warehouse busy (key order/o-1/0)
  warehouse busy (key order/o-1/0)
  warehouse reserved 2 widget (key order/o-1/0)
o-1 status: Running ["3 sleep"]
  carrier shipped r-1 (key order/o-1/4)
o-1 finished: reservation r-1, shipment s-1
  confirmation …, placed …
o-1 is already started (Completed)
Journal of order/default-shard/o-1:
  1 Started
  2 StepScheduled
  3 StepCompleted
  4 StepCompleted
  5 StepCompleted
  6 StepScheduled
  7 StepCompleted
  8 StepScheduled
  9 StepCompleted
  10 Finished
```

The confirmation and the time differ on every run; the page shows them as `…`.

While the timer ran, the status was `Running ["3 sleep"]`: the order waited on the step at position
3, named `sleep`. The carrier's key ends in 4, the position of `ship`.

The journal shows the kinds of steps:

- `reserve-stock` and `ship` are activities. Each has a `StepScheduled` record when it begins and a
  `StepCompleted` record with its result.
- `now` and `newGuid` (records 4 and 5) have only `StepCompleted`. Their value is computed and
  stored at once, so there is nothing to run again after a restart.
- `sleep` has a `StepScheduled` record with its due time (record 6) and a `StepCompleted` record
  when the time has passed (record 7).

## Next

[Step 4: Wait for approval](wait-for-approval.html) makes large orders wait for a person.

*)

(*** hide ***)
printfn "Read the clock example checked."
