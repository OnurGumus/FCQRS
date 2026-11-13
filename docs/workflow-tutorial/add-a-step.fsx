(**
---
title: 8. Add a step
category: Workflow tutorial
categoryindex: 3
index: 8
---
*)
(*** hide ***)
#r "nuget: FCQRS.Workflows, 7.2.1"
#load "../../samples/orders/8-add-a-step/fsharp/Account.fs"
#load "../../samples/orders/8-add-a-step/fsharp/Order.fs"

(**
# Step 8: Add a step

Code changes while orders are running. This step adds a fraud check to the workflow and runs it
against the database from step 7, where `o-7` is still waiting for its approval. The new code does
not match what `o-7` has stored, and FCQRS **parks** the order.

This page changes the program from [step 7](when-a-step-fails.html). Keep `orders.db` from step 7.

## Why a running order cannot take a new step

An order's journal records each step with its position, name, and kind. To resume an order,
FCQRS runs the body from the start and matches each step the body asks for against the record
at the same position. `o-7` stored these steps:

```text
0  reserve-stock  Activity
1  now            Value
2  newGuid        Value
3  approval       Signal  (waiting)
```

The new code asks for `check-fraud` before the approval, at position 3. Replaying `o-7`, FCQRS
finds a signal wait where the body asks for an activity.

## Add the fraud check

In `Order.fs` (`Order.cs`), add the fraud check service after the carrier:

<!-- sample: orders/8-add-a-step/fsharp Order.fs services -->
```fsharp
// The warehouse is an external system. The key is the same for every
// attempt of one step, so the warehouse can recognise a repeated call.
type IWarehouse =
    abstract Reserve:
        key: string * item: string * quantity: int -> Task<string>
    abstract Release: key: string * reservation: string -> Task<string>

// The carrier is another external system. It returns a shipment ID.
type ICarrier =
    abstract Ship:
        key: string * reservation: string * item: string -> Task<string>

// A fraud check is a third external system. It answers whether an order
// may go ahead.
type IFraudCheck =
    abstract Check:
        key: string * customer: string * total: decimal -> Task<bool>
```

<div class="cs-alt"></div>

<!-- sample: orders/8-add-a-step/csharp Order.cs services -->
```csharp
// The warehouse is an external system. The key is the same for every
// attempt of one step, so the warehouse can recognise a repeated call.
public interface IWarehouse
{
    Task<string> Reserve(string key, string item, int quantity);
    Task<string> Release(string key, string reservation);
}

// The carrier is another external system. It returns a shipment ID.
public interface ICarrier
{
    Task<string> Ship(string key, string reservation, string item);
}

// A fraud check is a third external system. It answers whether an order
// may go ahead.
public interface IFraudCheck
{
    Task<bool> Check(string key, string customer, decimal total);
}
```

Replace the workflow. It takes the fraud check, runs `check-fraud` after computing the total, and
treats a failed check as a refusal:

<!-- sample: orders/8-add-a-step/fsharp Order.fs workflow -->
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
                // The new step: an order that fails the fraud check is not
                // approved.
                let! passed =
                    activity "check-fraud" (Retry.times 3) (fun attempt ->
                        fraud.Check(
                            attempt.IdempotencyKey, request.Customer, total))
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

<!-- sample: orders/8-add-a-step/csharp Order.cs workflow -->
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
        // The new step: an order that fails the fraud check is not
        // approved.
        var passed = await ctx.Activity("check-fraud", Retry.times(3),
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

## Run the new code on step 7's orders

In `Program.fs` (`Program.cs`), add a stand-in fraud check. In F#, put it after the carrier. In
C#, put it at the end of the file:

<!-- sample: orders/8-add-a-step/fsharp Program.fs fraud -->
```fsharp
// A stand-in for the fraud check. It passes every order.
type FraudCheck() =
    interface IFraudCheck with
        member _.Check(key, customer, _) =
            task {
                printfn $"  fraud check passed for {customer} (key {key})"
                return true
            }
```

<div class="cs-alt"></div>

<!-- sample: orders/8-add-a-step/csharp Program.cs fraud -->
```csharp
// A stand-in for the fraud check. It passes every order.
public sealed class FraudCheck : IFraudCheck
{
    public Task<bool> Check(string key, string customer, decimal total)
    {
        Console.WriteLine($"  fraud check passed for {customer} (key {key})");
        return Task.FromResult(true);
    }
}
```

Give the workflow the fraud check when you register it:

<!-- sample: orders/8-add-a-step/fsharp Program.fs register -->
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

// Run the program with --crash to stop it in the middle of shipping.
let crash = Environment.GetCommandLineArgs() |> Array.contains "--crash"

// Registering the workflow also resumes the orders that were running when
// the process last stopped.
let orders =
    Workflows.register
        api (definition (Warehouse()) (Carrier crash) accounts (FraudCheck()))
```

<div class="cs-alt"></div>

<!-- sample: orders/8-add-a-step/csharp Program.cs register -->
```csharp
// Register the account rules; the workflow sends commands to them.
var accounts = new Account().Init(actor);

// Finish the aggregate startup. This program has no sagas.
ActorWiring.InitSagaStarterEmpty(actor);

// Run the program with --crash to stop it in the middle of shipping.
var crash = args.Contains("--crash");

// Registering the workflow also resumes the orders that were running when
// the process last stopped.
var orders = WorkflowHost.Register(actor, new OrderWorkflow(
    new Warehouse(), new Carrier(crash), accounts.Factory, new FraudCheck()));
```

Replace the code that starts the orders. The functions stay; the lines after them change. The
program reads `o-7`'s status, sends it the approval it was waiting for, and places a new order:

<!-- sample: orders/8-add-a-step/fsharp Program.fs start -->
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

// o-7 was started by step 7's code and is still waiting for its approval.
match orders.Status "o-7" |> Async.RunSynchronously with
| WorkflowStatus.Parked reason -> printfn $"o-7 is parked: {reason}"
| status -> printfn $"o-7 status: {status}"
// A parked order keeps the signals it gets until it can run again.
Workflows.signal orders "o-7" "approval" true |> Async.RunSynchronously
printfn "Sent approval to o-7"

// A new order runs the new step.
place "o-8" "ada" "widget" 2 None
```

<div class="cs-alt"></div>

<!-- sample: orders/8-add-a-step/csharp Program.cs start -->
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

// o-7 was started by step 7's code and is still waiting for its approval.
var status = await orders.StatusAsync("o-7");
Console.WriteLine(status is WorkflowStatus.Parked parked
    ? $"o-7 is parked: {parked.reason}"
    : $"o-7 status: {status}");
// A parked order keeps the signals it gets until it can run again.
await orders.SignalAsync("o-7", "approval", true);
Console.WriteLine("Sent approval to o-7");

// A new order runs the new step.
await Place("o-8", "ada", "widget", 2, null);
```

Print `o-7`'s journal instead of `o-6`'s:

<!-- sample: orders/8-add-a-step/fsharp Program.fs journal -->
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

printJournal "o-7"
api.Stop().Wait()
```

<div class="cs-alt"></div>

<!-- sample: orders/8-add-a-step/csharp Program.cs journal -->
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

PrintJournal("o-7");

await actor.Stop();
```

## Run it

Run `dotnet run` with step 7's `orders.db` in place:

```text
o-7 is parked: The workflow body asks for Activity 'check-fraud' at step 3, but the journal holds Signal 'approval'.
Sent approval to o-7
Started o-8
  warehouse busy (key order/o-8/0)
  warehouse busy (key order/o-8/0)
  warehouse reserved 2 widget (key order/o-8/0)
  fraud check passed for ada (key order/o-8/3)
  carrier shipped r-1 (key order/o-8/6)
o-8 finished: shipped as s-1
Journal of order/default-shard/o-7:
  1 Started
  2 StepScheduled
  3 StepCompleted
  4 StepCompleted
  5 StepCompleted
  6 StepScheduled
  7 SignalReceived
```

- `o-7` is parked. A **parked** order runs nothing. Its status gives the reason, and FCQRS logs it
  as an error. The process keeps running, and so do the other orders: one order whose journal does
  not match the code does not stop the rest.
- The approval sent to `o-7` was stored anyway: record 7, `SignalReceived`. A parked order keeps
  its signals until code that matches its journal runs it.
- `o-8` started under the new code, so its journal and its code agree. Its fraud check ran at
  position 3, and its carrier key ends in 6.

The stand-in warehouse and carrier number their IDs from 1 in each process, so `o-8` gets `r-1` and
`s-1` again. A real warehouse issues its own unique IDs.

## What may change while orders run

Changes that keep each running order's stored steps in place are safe:

- changing what an activity does, such as the code inside `Reserve`;
- changing steps that no running order has reached yet;
- any change to a workflow that has no running orders.

Adding, removing, reordering, or renaming a step that a running order has passed is not. Step and
workflow names are part of what is stored, so a renamed step parks every order that stored it.

## Next

Step 9: Guard the change keeps the old steps for `o-7` and the new ones for new orders.

*)

(*** hide ***)
printfn "Add a step example checked."
