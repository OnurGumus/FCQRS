(**
---
title: 9. Guard the change
category: Workflow tutorial
categoryindex: 3
index: 9
---
*)
(*** hide ***)
#r "nuget: FCQRS.Workflows, 7.5.0"
#load "../../samples/orders/9-guard-the-change/fsharp/Account.fs"
#load "../../samples/orders/9-guard-the-change/fsharp/Order.fs"

(**
# Step 9: Guard the change

`o-7` is parked: it started before the fraud check existed, and the new code asks it for a step it
never had. This step keeps both versions of the workflow in one body. Orders that passed the
change point keep the old steps, and new orders take the new one. When the guarded code runs,
`o-7` resumes.

This page changes the program from [step 8](add-a-step.html). Keep `orders.db` from step 8.

## Record which code an order runs

In `Order.fs` (`Order.cs`), replace the workflow. The fraud check is now guarded by a `version`
step:

<!-- sample: orders/9-guard-the-change/fsharp Order.fs workflow -->
```fsharp
// "order" is stored with every instance. It must not change.
let definition
    (warehouse: IWarehouse)
    (carrier: ICarrier)
    (accounts: AggregateHandle<AccountCommand, AccountEvent>)
    (fraud: IFraudCheck)
    =
    Workflow.define "order" (fun (request: OrderRequest) ->
        workflow {
            try
                // A failed attempt is not stored. FCQRS tries again until
                // an attempt succeeds or the policy gives up.
                let! reservation =
                    activity "reserve-stock" reserveRetry (fun attempt ->
                        warehouse.Reserve(
                            attempt.IdempotencyKey,
                            request.Item,
                            request.Quantity))
                // The body must give the same answers on every replay, so it
                // reads the clock and makes GUIDs only through steps. FCQRS
                // stores their values and replays them.
                let! placedAt = now
                let! confirmation = newGuid
                let finish status shipment = {
                    Status = status
                    Reservation = reservation
                    Shipment = shipment
                    Confirmation = confirmation
                    PlacedAt = placedAt
                }
                // Gives the stock back when the order does not ship.
                let release =
                    activity "release-stock" reserveRetry (fun attempt ->
                        warehouse.Release(attempt.IdempotencyKey, reservation))
                let total = request.UnitPrice * decimal request.Quantity
                // An order that had passed this point before the fraud check
                // was added gets 1 and skips it. A new order gets 2. An order
                // that fails the fraud check is not approved.
                let! change = version "fraud-check" 2
                let check (attempt: AttemptContext) =
                    fraud.Check(attempt.IdempotencyKey, request.Customer, total)
                let! passed =
                    workflow {
                        if change < 2 then
                            return true
                        else
                            return! activity "check-fraud" (Retry.times 3) check
                    }
                // An order over 1000 waits for a person to approve it, for at
                // most one day. With no answer by then, the wait returns None.
                let approvalWait = TimeSpan.FromDays 1.0
                let! approved =
                    workflow {
                        if not passed then
                            return false
                        elif total <= 1000m then
                            return true
                        else
                            let! answer =
                                waitSignalFor<bool> "approval" approvalWait
                            return defaultArg answer false
                    }
                if not approved then
                    let! _ = release
                    return finish "not approved" ""
                else
                    // The confirmation number identifies the payment, so a
                    // repeated debit is recognised as the same payment.
                    let payment = string confirmation
                    let! reply =
                        send accounts request.Customer (Debit(payment, total))
                    match reply with
                    | Rejected _ ->
                        let! _ = release
                        return finish "payment refused" ""
                    | _ ->
                        // Packing takes two seconds. The timer's due time
                        // is stored, so a restart does not start the wait.
                        do! sleep (TimeSpan.FromSeconds 2.0)
                        let! shipment =
                            activity "ship" (Retry.times 3) (fun attempt ->
                                carrier.Ship(
                                    attempt.IdempotencyKey,
                                    reservation,
                                    request.Item))
                        return finish "shipped" shipment
            // When a step's policy gives up, the step fails, and the body
            // sees WorkflowStepFailedException. Running out of stock is an
            // answer the order can give; any other failure is not caught.
            with :? WorkflowStepFailedException as failed
                    when failed.StepName = "reserve-stock" ->
                return {
                    Status = "out of stock"
                    Reservation = ""
                    Shipment = ""
                    Confirmation = Guid.Empty
                    PlacedAt = DateTime.MinValue
                }
        })
```

<div class="cs-alt"></div>

<!-- sample: orders/9-guard-the-change/csharp Order.cs workflow -->
```csharp
// "order" is stored with every instance. It must not change.
public sealed class OrderWorkflow(
    IWarehouse warehouse, ICarrier carrier, AggregateFactory accounts,
    IFraudCheck fraud)
    : Workflow<OrderRequest, OrderResult>("order")
{
    // Up to five attempts, 200 ms apart.
    private static readonly RetryPolicy ReserveRetry = Retry.times(5)
        .WithSchedule(RetrySchedules.Fixed(TimeSpan.FromMilliseconds(200)));

    public override async WorkflowTask<OrderResult> Run(
        WorkflowContext ctx, OrderRequest request)
    {
        try
        {
            return await Fulfil(ctx, request);
        }
        // When a step's policy gives up, the step fails, and the body sees
        // WorkflowStepFailedException. Running out of stock is an answer
        // the order can give; any other failure is not caught.
        catch (WorkflowStepFailedException failed)
            when (failed.StepName == "reserve-stock")
        {
            return new OrderResult(
                "out of stock", "", "", Guid.Empty, DateTime.MinValue);
        }
    }

    private async WorkflowTask<OrderResult> Fulfil(
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
        OrderResult Finish(string status, string shipment) =>
            new(status, reservation, shipment, confirmation, placedAt);
        // Gives the stock back when the order does not ship.
        Step<string> Release() =>
            ctx.Activity("release-stock", ReserveRetry, attempt =>
                warehouse.Release(attempt.IdempotencyKey, reservation));
        var total = request.UnitPrice * request.Quantity;
        // An order that had passed this point before the fraud check was
        // added gets 1 and skips it. A new order gets 2. An order that
        // fails the fraud check is not approved.
        var change = await ctx.Version("fraud-check", 2);
        var passed = change < 2
            || await ctx.Activity("check-fraud", Retry.times(3),
                attempt => fraud.Check(
                    attempt.IdempotencyKey, request.Customer, total));
        // An order over 1000 waits for a person to approve it, for at
        // most one day. With no answer by then, Received is false.
        var approved = passed;
        if (passed && total > 1000m)
        {
            var answer = await ctx.WaitSignal<bool>(
                "approval", TimeSpan.FromDays(1));
            approved = answer.Received && answer.Value;
        }
        if (!approved)
        {
            await Release();
            return Finish("not approved", "");
        }
        // The confirmation number identifies the payment, so a repeated
        // debit is recognised as the same payment.
        var payment = confirmation.ToString();
        var reply = await ctx.Send<AccountCommand, AccountEvent>(
            accounts, request.Customer, new Debit(payment, total));
        if (reply is Rejected)
        {
            await Release();
            return Finish("payment refused", "");
        }
        // Packing takes two seconds. The timer's due time is stored, so
        // a restart does not start the wait again.
        await ctx.Sleep(TimeSpan.FromSeconds(2));
        var shipment = await ctx.Activity("ship", Retry.times(3),
            attempt => carrier.Ship(
                attempt.IdempotencyKey, reservation, request.Item));
        return Finish("shipped", shipment);
    }
}
```

`version "fraud-check" 2` (`ctx.Version` in C#) answers which code this order runs:

- An order that reaches it for the first time stores 2 and gets 2. It runs the fraud check.
- An order whose journal already has a step after this point gets 1 and stores nothing. It takes
  the old path, without the fraud check. `o-7` stored its approval wait after this point, so it
  gets 1.

A `version` step takes no position, so the steps after it keep the positions they had. For `o-7`,
the approval is at position 3 again, where its journal has it.

Put a `version` step before the steps it guards and before at least one step that the old code
already had. A `version` step with no stored step after it, such as one added at the end of the
body, gives 2 to every running order.

## Resume the parked order

In `Program.fs` (`Program.cs`), replace the lines after the functions. The program waits for
`o-7`, which needs no new signal, and places a new order:

<!-- sample: orders/9-guard-the-change/fsharp Program.fs start -->
```fsharp
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

// o-7 runs again under this code. It skips the fraud check, uses the
// approval it got while it was parked, and ships.
let resumed =
    orders.Result "o-7" (TimeSpan.FromSeconds 30.0) |> Async.RunSynchronously

match resumed with
| WorkflowOutcome.Finished result ->
    printfn $"o-7 finished: shipped as {result.Shipment}"
| other -> printfn $"o-7 ended: {other}"

// A new order runs the fraud check.
place "o-9" "ada" "widget" 2 None
```

<div class="cs-alt"></div>

<!-- sample: orders/9-guard-the-change/csharp Program.cs start -->
```csharp
// Put money in a customer's account.
async Task Deposit(string customer, decimal amount)
{
    await accounts.Handler(_ => true, Values.NewCID(),
        Values.CreateAggregateId(customer), new Deposit(amount));
    Console.WriteLine($"Deposited {amount} to {customer}");
}

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
async Task Place(
    string id, string customer, string item, int quantity, bool? approval)
{
    await Start(id, new OrderRequest(customer, item, quantity, 40m));
    if (approval is bool approve)
        await orders.SignalAsync(id, "approval", approve);
    try
    {
        var result = await orders.ResultAsync(id, TimeSpan.FromSeconds(30));
        Console.WriteLine(result.Status == "shipped"
            ? $"{id} finished: shipped as {result.Shipment}"
            : $"{id} finished: {result.Status}");
    }
    catch (WorkflowFaultedException faulted)
    {
        Console.WriteLine($"{id} faulted: {faulted.Error.Message}");
    }
}

// o-7 runs again under this code. It skips the fraud check, uses the
// approval it got while it was parked, and ships.
var resumed = await orders.ResultAsync("o-7", TimeSpan.FromSeconds(30));
Console.WriteLine($"o-7 finished: shipped as {resumed.Shipment}");

// A new order runs the fraud check.
await Place("o-9", "ada", "widget", 2, null);
```

## Run it

Run `dotnet run` with step 8's `orders.db` in place:

```text
  carrier shipped r-2 (key order/o-7/6)
o-7 finished: shipped as s-1
Started o-9
  warehouse busy (key order/o-9/0)
  warehouse busy (key order/o-9/0)
  warehouse reserved 2 widget (key order/o-9/0)
  fraud check passed for ada (key order/o-9/3)
  carrier shipped r-1 (key order/o-9/6)
o-9 finished: shipped as s-2
Journal of order/default-shard/o-7:
  1 Started
  2 StepScheduled
  3 StepCompleted
  4 StepCompleted
  5 StepCompleted
  6 StepScheduled
  7 SignalReceived
  8 StepCompleted
  9 StepScheduled
  10 StepCompleted
  11 StepScheduled
  12 StepCompleted
  13 StepScheduled
  14 StepCompleted
  15 Finished
```

Registering the workflow resumed `o-7` with the new code. Its journal matched: the `version` step
gave 1, the fraud check was skipped, and the approval wait at position 3 found the approval that
step 8 sent while the order was parked (record 7). Record 8 ends the wait. Then the payment
(records 9 and 10), the packing timer (11 and 12), and `ship` (13 and 14) ran, and the order
finished. `ship` is at position 6, as it would have been under step 7's code.

`o-9` is new, so its `version` step gave 2, and it ran the fraud check at position 3.

## Keep or remove the guard

The old path must stay in the body while an order that takes it is still running. An order that
has ended does not run its body again: FCQRS reads its result from the journal. So once every order
started before the change has ended, a later change can remove the old path.

A larger change, such as reordering many steps, is easier as a new workflow: register the new code
under a new name, start new orders with it, and keep the old definition registered until its orders
have ended.

## Next

[Step 10: Feed orders from a stream](feed-orders-from-a-stream.html) starts orders from an
Akka.Streams flow, two at a time, and stops the process while two orders are shipping.

*)

(*** hide ***)
printfn "Guard the change example checked."
