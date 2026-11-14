(**
---
title: 2. Retry the warehouse
category: Workflow tutorial
categoryindex: 3
index: 2
---
*)
(*** hide ***)
#r "nuget: FCQRS.Workflows, 7.5.0"
#load "../../samples/orders/2-retry-the-warehouse/fsharp/Order.fs"

(**
# Step 2: Retry the warehouse

A real warehouse is sometimes busy. In this step the stand-in warehouse fails the first two calls
for each key, and the `reserve-stock` step retries until a call succeeds.

This page changes the program from [step 1](place-an-order.html). Each block below names the file
it goes in and the block it replaces.

## Give the step a retry policy

In `Order.fs`, the top of the file opens two more namespaces, `System` for `TimeSpan` and
`FCQRS.Common` for `FixedInterval`. In `Order.cs`, `using static FCQRS.CSharp` brings in
`RetrySchedules`:

<!-- sample: orders/2-retry-the-warehouse/fsharp Order.fs messages -->
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
type OrderResult = { Reservation: string }
```

<div class="cs-alt"></div>

<!-- sample: orders/2-retry-the-warehouse/csharp Order.cs messages -->
```csharp
using FCQRS.Workflows;
using FCQRS.Workflows.CSharp;
using static FCQRS.CSharp;

// What an order workflow is started with. FCQRS stores it in the journal.
public sealed record OrderRequest(
    string Customer, string Item, int Quantity, decimal UnitPrice);

// What the workflow returns. FCQRS stores it too.
public sealed record OrderResult(string Reservation);
```

In F#, add the policy before `definition`, and replace `definition`. In C#, replace the workflow
class, which now holds the policy:

<!-- sample: orders/2-retry-the-warehouse/fsharp Order.fs retry -->
```fsharp
// Up to five attempts, 200 ms apart.
let reserveRetry = {
    Retry.times 5 with
        Schedule = FixedInterval(TimeSpan.FromMilliseconds 200.0)
}
```

<!-- sample: orders/2-retry-the-warehouse/fsharp Order.fs workflow -->
```fsharp
// "order" is stored with every instance. It must not change.
let definition (warehouse: IWarehouse) =
    Workflow.define "order" (fun (request: OrderRequest) ->
        workflow {
            // A failed attempt is not stored. FCQRS tries again until an
            // attempt succeeds or the policy gives up.
            let! reservation =
                activity "reserve-stock" reserveRetry (fun attempt ->
                    warehouse.Reserve(
                        attempt.IdempotencyKey, request.Item, request.Quantity))
            return { Reservation = reservation }
        })
```

<div class="cs-alt"></div>

<!-- sample: orders/2-retry-the-warehouse/csharp Order.cs workflow -->
```csharp
// "order" is stored with every instance. It must not change.
public sealed class OrderWorkflow(IWarehouse warehouse)
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
        return new OrderResult(reservation);
    }
}
```

A **retry policy** belongs to one step. `Retry.times 5` allows five attempts. Its default schedule
waits longer after each failure, from one second up to one minute; this program waits a fixed
200 ms so that it finishes quickly. A policy can also set a deadline for the whole step, which is
measured from the step's stored start, so a restart does not extend it.

## Make the warehouse busy

In `Program.fs` (`Program.cs`), replace the stand-in warehouse:

<!-- sample: orders/2-retry-the-warehouse/fsharp Program.fs warehouse -->
```fsharp
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
```

<div class="cs-alt"></div>

<!-- sample: orders/2-retry-the-warehouse/csharp Program.cs warehouse -->
```csharp
// A stand-in for the warehouse. It is busy for the first two calls with a
// key. It remembers keys, so a repeated call returns the first reservation.
public sealed class Warehouse : IWarehouse
{
    private readonly Dictionary<string, int> calls = new();
    private readonly Dictionary<string, string> reservations = new();

    public Task<string> Reserve(string key, string item, int quantity)
    {
        calls[key] = calls.GetValueOrDefault(key) + 1;
        if (calls[key] <= 2)
        {
            Console.WriteLine($"  warehouse busy (key {key})");
            throw new InvalidOperationException("The warehouse is busy");
        }
        if (!reservations.ContainsKey(key))
        {
            reservations[key] = $"r-{reservations.Count + 1}";
            Console.WriteLine(
                $"  warehouse reserved {quantity} {item} (key {key})");
        }
        return Task.FromResult(reservations[key]);
    }
}
```

With step 1's `Retry.none`, the first failure would end the step. The policy above retries it.
[Step 7](when-a-step-fails.html) shows what a workflow does when a step fails for good.

## Run it

Delete `bin/Debug/net11.0/orders.db` if step 1 left one, then run `dotnet run`:

```text
Started o-1
  warehouse busy (key order/o-1/0)
  warehouse busy (key order/o-1/0)
  warehouse reserved 2 widget (key order/o-1/0)
o-1 finished: reservation r-1
o-1 is already started (Completed)
Journal of order/default-shard/o-1:
  1 Started
  2 StepScheduled
  3 StepCompleted
  4 Finished
```

The step made three attempts, all with the same key, `order/o-1/0`. The key belongs to the step,
not to the attempt, so a warehouse that remembers keys sees one request, made three times.

The journal is the same as in step 1. FCQRS does not store failed attempts, only the result of the
one that succeeded. So the number of attempts is not durable either: if the process stops after the
second failure, the step starts again with a fresh count of five.

## What the warehouse must do

The step's key makes a repeated call recognisable, but only the warehouse can act on it. A real
warehouse must store the key with the reservation and return the stored reservation when the same
key arrives again. Without that, an attempt that reserved stock but failed to answer, for example
because the connection dropped, would be followed by a second reservation.

## Next

[Step 3: Read the clock](read-the-clock.html) adds a confirmation number, an order time, and a shipping step.

*)

(*** hide ***)
if Order.reserveRetry.MaxAttempts <> Some 5 then failwith "the reserve policy allows five attempts"
printfn "Retry the warehouse example checked."
