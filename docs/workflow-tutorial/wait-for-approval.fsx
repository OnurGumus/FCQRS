(**
---
title: 4. Wait for approval
category: Workflow tutorial
categoryindex: 3
index: 4
---
*)
(*** hide ***)
#r "nuget: FCQRS.Workflows, 7.2.1"
#load "../../samples/orders/4-wait-for-approval/fsharp/Order.fs"

(**
# Step 4: Wait for approval

An order over 1000 needs a person to approve it before it ships. The person answers at some later
time, from outside the workflow. This step makes the workflow wait for that answer, for a limited
time.

This page changes the program from [step 3](read-the-clock.html).

## Wait for a signal

A **signal** is a named message sent to one workflow instance from outside. FCQRS stores it before
it acknowledges it, and the body reads it as the result of a step that waits for that name.

In `Order.fs` (`Order.cs`), the result gets a status, `shipped` or `not approved`:

<!-- sample: orders/4-wait-for-approval/fsharp Order.fs messages -->
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
    Status: string
    Reservation: string
    Shipment: string
    Confirmation: Guid
    PlacedAt: DateTime
}
```

<div class="cs-alt"></div>

<!-- sample: orders/4-wait-for-approval/csharp Order.cs messages -->
```csharp
using FCQRS.Workflows;
using FCQRS.Workflows.CSharp;
using static FCQRS.CSharp;

// What an order workflow is started with. FCQRS stores it in the journal.
public sealed record OrderRequest(
    string Customer, string Item, int Quantity, decimal UnitPrice);

// What the workflow returns. FCQRS stores it too.
public sealed record OrderResult(
    string Status, string Reservation, string Shipment, Guid Confirmation,
    DateTime PlacedAt);
```

Replace the workflow. After the confirmation number, an order over 1000 waits for the signal
`approval`:

<!-- sample: orders/4-wait-for-approval/fsharp Order.fs workflow -->
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
            // An order over 1000 waits for a person to approve it, for at
            // most three seconds in this program. With no answer by then,
            // the wait returns None.
            let total = request.UnitPrice * decimal request.Quantity
            let approvalWait = TimeSpan.FromSeconds 3.0
            let! approved =
                workflow {
                    if total <= 1000m then
                        return true
                    else
                        let! answer =
                            waitSignalFor<bool> "approval" approvalWait
                        return defaultArg answer false
                }
            if not approved then
                return {
                    Status = "not approved"
                    Reservation = reservation
                    Shipment = ""
                    Confirmation = confirmation
                    PlacedAt = placedAt
                }
            else
                // Packing takes two seconds. The timer's due time is
                // stored, so a restart does not start the wait again.
                do! sleep (TimeSpan.FromSeconds 2.0)
                let! shipment =
                    activity "ship" (Retry.times 3) (fun attempt ->
                        carrier.Ship(attempt.IdempotencyKey, reservation))
                return {
                    Status = "shipped"
                    Reservation = reservation
                    Shipment = shipment
                    Confirmation = confirmation
                    PlacedAt = placedAt
                }
            })
```

<div class="cs-alt"></div>

<!-- sample: orders/4-wait-for-approval/csharp Order.cs workflow -->
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
        // An order over 1000 waits for a person to approve it, for at
        // most three seconds in this program. With no answer by then,
        // Received is false.
        var total = request.UnitPrice * request.Quantity;
        if (total > 1000m)
        {
            var answer = await ctx.WaitSignal<bool>(
                "approval", TimeSpan.FromSeconds(3));
            if (!(answer.Received && answer.Value))
                return new OrderResult(
                    "not approved", reservation, "", confirmation, placedAt);
        }
        // Packing takes two seconds. The timer's due time is stored, so
        // a restart does not start the wait again.
        await ctx.Sleep(TimeSpan.FromSeconds(2));
        var shipment = await ctx.Activity("ship", Retry.times(3),
            attempt => carrier.Ship(attempt.IdempotencyKey, reservation));
        return new OrderResult(
            "shipped", reservation, shipment, confirmation, placedAt);
    }
}
```

`waitSignalFor<bool> "approval"` (`ctx.WaitSignal<bool>` in C#) is a step at position 3. It ends when
a signal named `approval` with a `bool` payload arrives, or when its timeout passes. The timeout's
due time is stored when the body reaches the step, as a timer's is, so a restart does not extend
it. A real approval would wait days; this program waits three seconds.

An order of 1000 or less does not reach the wait, so its `sleep` and `ship` steps are at positions
3 and 4, and a large order's are at 4 and 5. The positions belong to each order's own run of the
body. Both kinds of order replay correctly, because each replays its own journal with the same
input.

## Send the signal

In `Program.fs` (`Program.cs`), replace the code that starts the order. The program now places
three orders, one after another:

<!-- sample: orders/4-wait-for-approval/fsharp Program.fs start -->
```fsharp
// Start an order. A start with an ID that is already in the journal stores
// nothing and returns the order's status.
let start id request =
    match orders.Start id request |> Async.RunSynchronously with
    | StartResult.Started -> printfn $"Started {id}"
    | StartResult.AlreadyStarted status ->
        printfn $"{id} is already started ({status})"

// Start an order, send it an approval if given one, and wait for its end.
let place id quantity approval =
    let request = {
        Customer = "ada"
        Item = "widget"
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
    | other -> printfn $"{id} ended: {other}"

place "o-1" 2 None
// 30 widgets at 40 cost 1200. The approval is sent before the order waits
// for it, and the order keeps it until then.
place "o-2" 30 (Some true)
// 50 widgets cost 2000. Nobody approves this order.
place "o-3" 50 None
```

<div class="cs-alt"></div>

<!-- sample: orders/4-wait-for-approval/csharp Program.cs start -->
```csharp
// Start an order. A start with an ID that is already in the journal stores
// nothing and returns false.
async Task Start(string id, OrderRequest request)
{
    if (await orders.StartAsync(id, request))
        Console.WriteLine($"Started {id}");
    else
        Console.WriteLine(
            $"{id} is already started ({await orders.StatusAsync(id)})");
}

// Start an order, send it an approval if given one, and wait for its end.
async Task Place(string id, int quantity, bool? approval)
{
    await Start(id, new OrderRequest("ada", "widget", quantity, 40m));
    if (approval is bool approve)
        await orders.SignalAsync(id, "approval", approve);
    var result = await orders.ResultAsync(id, TimeSpan.FromSeconds(30));
    Console.WriteLine(result.Status == "shipped"
        ? $"{id} finished: shipped as {result.Shipment}"
        : $"{id} finished: {result.Status}");
}

await Place("o-1", 2, null);
// 30 widgets at 40 cost 1200. The approval is sent before the order waits
// for it, and the order keeps it until then.
await Place("o-2", 30, true);
// 50 widgets cost 2000. Nobody approves this order.
await Place("o-3", 50, null);
```

`Workflows.signal orders id "approval" true` (`orders.SignalAsync` in C#) returns once FCQRS has
stored the signal. The program sends `o-2`'s approval right after the start, while the order is
still reserving stock.

## Run it

Delete `bin/Debug/net11.0/orders.db`, then run `dotnet run`. It takes about ten seconds:

```text
Started o-1
  warehouse busy (key order/o-1/0)
  warehouse busy (key order/o-1/0)
  warehouse reserved 2 widget (key order/o-1/0)
  carrier shipped r-1 (key order/o-1/4)
o-1 finished: shipped as s-1
Started o-2
  warehouse busy (key order/o-2/0)
  warehouse busy (key order/o-2/0)
  warehouse reserved 30 widget (key order/o-2/0)
  carrier shipped r-2 (key order/o-2/5)
o-2 finished: shipped as s-2
Started o-3
  warehouse busy (key order/o-3/0)
  warehouse busy (key order/o-3/0)
  warehouse reserved 50 widget (key order/o-3/0)
o-3 finished: not approved
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

- `o-1` costs 80, so it skips the wait. Its journal is the same as in step 3.
- `o-2` costs 1200. Its approval arrived before the body asked for it. FCQRS stored the signal
  when it arrived, and the wait at position 3 found it, so the order shipped without waiting. A
  signal sent early is not lost. The carrier's key ends in 5, because the wait took position 3.
- `o-3` costs 2000 and gets no approval. After three seconds its wait returned no answer, and the
  order finished as `not approved`.

`o-3` still holds a reservation at the warehouse: nothing releases it. Step 6 adds a step that
releases the stock of an order that does not ship.

## Next

Step 5: Restart during shipping stops the process in the middle of an order and starts it again.

*)

(*** hide ***)
printfn "Wait for approval example checked."
