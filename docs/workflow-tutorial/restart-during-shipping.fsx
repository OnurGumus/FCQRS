(**
---
title: 5. Restart during shipping
category: Workflow tutorial
categoryindex: 3
index: 5
---
*)
(*** hide ***)
#r "nuget: FCQRS.Workflows, 7.2.1"
#load "../../samples/orders/5-restart-during-shipping/fsharp/Order.fs"

(**
# Step 5: Restart during shipping

The steps so far ran in one process from start to end. This step stops the process in the middle
of an order and starts it again, to show what FCQRS recovers and what it runs a second time. The
workflow does not change; only the program does.

This page changes the program from [step 4](wait-for-approval.html).

## Stop the process inside a step

In `Program.fs` (`Program.cs`), replace the stand-in carrier. A call now takes two seconds, and
with `crash` the carrier stops the whole process after it ships, before it returns:

<!-- sample: orders/5-restart-during-shipping/fsharp Program.fs carrier -->
```fsharp
// A stand-in for the carrier. A call takes two seconds. With `crash`, the
// process stops after the carrier ships and before FCQRS stores the result.
type Carrier(crash: bool) =
    let shipments = Dictionary<string, string>()
    interface ICarrier with
        member _.Ship(key, reservation) =
            task {
                Thread.Sleep 2000
                if not (shipments.ContainsKey key) then
                    shipments[key] <- $"s-{shipments.Count + 1}"
                    printfn $"  carrier shipped {reservation} (key {key})"
                if crash then
                    printfn "  the process stops before this step is stored"
                    exit 1
                return shipments[key]
            }
```

<div class="cs-alt"></div>

<!-- sample: orders/5-restart-during-shipping/csharp Program.cs carrier -->
```csharp
// A stand-in for the carrier. A call takes two seconds. With `crash`, the
// process stops after the carrier ships and before FCQRS stores the result.
public sealed class Carrier(bool crash) : ICarrier
{
    private readonly Dictionary<string, string> shipments = new();

    public Task<string> Ship(string key, string reservation)
    {
        Thread.Sleep(2000);
        if (!shipments.ContainsKey(key))
        {
            shipments[key] = $"s-{shipments.Count + 1}";
            Console.WriteLine($"  carrier shipped {reservation} (key {key})");
        }
        if (crash)
        {
            Console.WriteLine("  the process stops before this step is stored");
            Environment.Exit(1);
        }
        return Task.FromResult(shipments[key]);
    }
}
```

Replace the registration. The program reads `--crash` from its command line and gives it to the
carrier:

<!-- sample: orders/5-restart-during-shipping/fsharp Program.fs register -->
```fsharp
// Run the program with --crash to stop it in the middle of shipping.
let crash = Environment.GetCommandLineArgs() |> Array.contains "--crash"

// Registering the workflow also resumes the orders that were running when
// the process last stopped.
let orders =
    Workflows.register api (definition (Warehouse()) (Carrier crash))
```

<div class="cs-alt"></div>

<!-- sample: orders/5-restart-during-shipping/csharp Program.cs register -->
```csharp
// Run the program with --crash to stop it in the middle of shipping.
var crash = args.Contains("--crash");

// Registering the workflow also resumes the orders that were running when
// the process last stopped.
var orders = WorkflowHost.Register(
    actor, new OrderWorkflow(new Warehouse(), new Carrier(crash)));
```

Replace the code that starts the orders. This step places only `o-1`:

<!-- sample: orders/5-restart-during-shipping/fsharp Program.fs start -->
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
```

<div class="cs-alt"></div>

<!-- sample: orders/5-restart-during-shipping/csharp Program.cs start -->
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
```

## Run it with --crash

Delete `bin/Debug/net11.0/orders.db`, then run the program with `--crash`:

```text
dotnet run -- --crash
```

```text
Started o-1
  warehouse busy (key order/o-1/0)
  warehouse busy (key order/o-1/0)
  warehouse reserved 2 widget (key order/o-1/0)
  carrier shipped r-1 (key order/o-1/4)
  the process stops before this step is stored
```

The process exited while `o-1` was inside `ship`. The carrier shipped, but FCQRS never received the
result, so the journal has `ship`'s `StepScheduled` record and no `StepCompleted` record for it. The
same happens when a real process is killed, loses power, or is redeployed during a call.

## Run it again

Keep `orders.db` and run the program without the flag:

```text
dotnet run
```

```text
o-1 is already started (Running ["4 ship"])
  carrier shipped r-1 (key order/o-1/4)
o-1 finished: shipped as s-1
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

Registering the workflow found `o-1` in the journal and **resumed** it before the program asked:
the start found it already running, waiting on `ship` at position 4. To resume an order, FCQRS ran
the body from the beginning and **replayed** the stored results:

- `reserve-stock` had a stored result, so the warehouse was not called. No `warehouse` line
  appears.
- `now` and `newGuid` returned their stored values, so the confirmation number and the order time
  are the ones from the first run.
- `sleep` had ended before the process stopped, and its end was stored, so it did not wait
  again.
- `ship` had begun and had no result, so FCQRS ran it again, with the same key, `order/o-1/4`.

## What the carrier must do

The carrier was called twice for one shipment. The stand-in printed `carrier shipped` twice
because it lost its memory of keys when the process stopped. A real carrier must store the key
with the shipment and answer a repeated key with the first shipment. FCQRS cannot do this for it:
no stored record can tell whether a call that was running when the process stopped reached the
other system.

The same holds for every activity. FCQRS gives each step a stable key, stores each result once it
arrives, and never runs a step whose result is stored. The external system must make the repeated
call harmless.

## Next

Step 6: Charge the customer sends a command to an account from the workflow, and releases the
stock when the payment is refused.

*)

(*** hide ***)
printfn "Restart during shipping example checked."
