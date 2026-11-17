(**
---
title: 7. When a step fails for good
category: Workflow tutorial
categoryindex: 3
index: 7
---
*)
(*** hide ***)
#r "nuget: FCQRS.Workflows, 7.8.0"
#load "../../samples/orders/7-when-a-step-fails/fsharp/Account.fs"
#load "../../samples/orders/7-when-a-step-fails/fsharp/Order.fs"

(**
# Step 7: When a step fails for good

A step can end without the value the order needs in two ways. The external system can answer
that it cannot do what was asked, such as a warehouse with no stock. Or the call can throw until
the retry policy gives up, and then the step **fails**. This step returns the answer as a value,
catches a failure that has an answer in the domain, and lets a failure that has none end the
order.

This page changes the program from [step 6](charge-the-customer.html).

## Return an answer as a value

A warehouse that has no stock has answered, and a second call gets the same answer. FCQRS stores
whatever an activity returns as the step's result. The policy retries only a call that throws.

In `Order.fs` (`Order.cs`), replace the services. `Reserve` returns a `StockAnswer`, and the
carrier is told what it ships:

<!-- sample: orders/7-when-a-step-fails/fsharp Order.fs services -->
```fsharp
// What the warehouse answers when it is asked for stock. FCQRS stores the
// answer as the step's result.
type StockAnswer =
    | Reserved of reservation: string
    | OutOfStock

// The warehouse is an external system. The key is the same for every
// attempt of one step, so the warehouse can recognise a repeated call.
type IWarehouse =
    abstract Reserve:
        key: string * item: string * quantity: int -> Task<StockAnswer>
    abstract Release: key: string * reservation: string -> Task<string>

// The carrier is another external system. It returns a shipment ID.
type ICarrier =
    abstract Ship:
        key: string * reservation: string * item: string -> Task<string>
```

<div class="cs-alt"></div>

<!-- sample: orders/7-when-a-step-fails/csharp Order.cs services -->
```csharp
// What the warehouse answers when it is asked for stock. FCQRS stores the
// answer as the step's result.
public union StockAnswer(Reserved, OutOfStock);
public sealed record Reserved(string Reservation);
public sealed record OutOfStock;

// The warehouse is an external system. The key is the same for every
// attempt of one step, so the warehouse can recognise a repeated call.
public interface IWarehouse
{
    Task<StockAnswer> Reserve(string key, string item, int quantity);
    Task<string> Release(string key, string reservation);
}

// The carrier is another external system. It returns a shipment ID.
public interface ICarrier
{
    Task<string> Ship(string key, string reservation, string item);
}
```

In step 6, `reserve-stock` stored a string. It now stores a `StockAnswer`, and the type of a
stored result is a persistent contract: this code would park an order whose journal holds the
string. This step starts from an empty database.

## Catch the failure that has an answer

When a step's policy gives up, FCQRS stores the failure as the step's result, and the body sees a
`WorkflowStepFailedException`. On a replay the body sees the same exception again, from the
stored failure, without running the step.

Replace the workflow. The steps after the reservation move into `fulfil` (in C#, `Fulfil`). The
body reserves the stock inside a `try`, returns a result for `OutOfStock`, and runs `fulfil` for
`Reserved`. The approval now waits up to one day, and the carrier gets the item:

<!-- sample: orders/7-when-a-step-fails/fsharp Order.fs workflow -->
```fsharp
// "order" is stored with every instance. It must not change.
let definition
    (warehouse: IWarehouse)
    (carrier: ICarrier)
    (accounts: AggregateHandle<AccountCommand, AccountEvent>)
    =
    // The result of an order that reserved no stock.
    let unfilled status = {
        Status = status
        Reservation = ""
        Shipment = ""
        Confirmation = Guid.Empty
        PlacedAt = DateTime.MinValue
    }
    // The rest of an order, once its stock is reserved.
    let fulfil (request: OrderRequest) (reservation: string) =
        workflow {
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
            // An order over 1000 waits for a person to approve it, for at
            // most one day. With no answer by then, the wait returns None.
            let total = request.UnitPrice * decimal request.Quantity
            let approvalWait = TimeSpan.FromDays 1.0
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
                        activity "ship" callRetry (fun attempt ->
                            carrier.Ship(
                                attempt.IdempotencyKey,
                                reservation,
                                request.Item))
                    return finish "shipped" shipment
        }
    Workflow.define "order" (fun (request: OrderRequest) ->
        workflow {
            try
                // A failed attempt is not stored. FCQRS tries again until
                // an attempt succeeds or the policy gives up.
                let! answer =
                    activity "reserve-stock" reserveRetry (fun attempt ->
                        warehouse.Reserve(
                            attempt.IdempotencyKey,
                            request.Item,
                            request.Quantity))
                // An answer is the step's result, whichever case it is. It
                // is stored, and the policy does not retry it.
                match answer with
                | OutOfStock -> return unfilled "out of stock"
                | Reserved reservation -> return! fulfil request reservation
            // When a step's policy gives up, the step fails, and the body
            // sees WorkflowStepFailedException. A warehouse that was busy at
            // every attempt reserved nothing, so the order can end with a
            // result. Any other failure is not caught.
            with :? WorkflowStepFailedException as failed
                    when failed.StepName = "reserve-stock" ->
                return unfilled "warehouse unavailable"
        })
```

<div class="cs-alt"></div>

<!-- sample: orders/7-when-a-step-fails/csharp Order.cs workflow -->
```csharp
// "order" is stored with every instance. It must not change.
public sealed class OrderWorkflow(
    IWarehouse warehouse, ICarrier carrier, AggregateFactory accounts)
    : Workflow<OrderRequest, OrderResult>("order")
{
    // Up to five attempts, 200 ms apart, each allowed ten seconds.
    private static readonly RetryPolicy ReserveRetry =
        Retry.times(5, TimeSpan.FromSeconds(10))
            .WithSchedule(RetrySchedules.Fixed(TimeSpan.FromMilliseconds(200)));

    // Up to three attempts, each allowed ten seconds.
    private static readonly RetryPolicy CallRetry =
        Retry.times(3, TimeSpan.FromSeconds(10));

    public override async WorkflowTask<OrderResult> Run(
        WorkflowContext ctx, OrderRequest request)
    {
        try
        {
            // A failed attempt is not stored. FCQRS tries again until an
            // attempt succeeds or the policy gives up.
            var answer = await ctx.Activity("reserve-stock", ReserveRetry,
                attempt => warehouse.Reserve(
                    attempt.IdempotencyKey, request.Item, request.Quantity));
            // An answer is the step's result, whichever case it is. It is
            // stored, and the policy does not retry it.
            return answer switch
            {
                OutOfStock => Unfilled("out of stock"),
                Reserved reserved =>
                    await Fulfil(ctx, request, reserved.Reservation),
            };
        }
        // When a step's policy gives up, the step fails, and the body sees
        // WorkflowStepFailedException. A warehouse that was busy at every
        // attempt reserved nothing, so the order can end with a result. Any
        // other failure is not caught.
        catch (WorkflowStepFailedException failed)
            when (failed.StepName == "reserve-stock")
        {
            return Unfilled("warehouse unavailable");
        }
    }

    // The result of an order that reserved no stock.
    private static OrderResult Unfilled(string status) =>
        new(status, "", "", Guid.Empty, DateTime.MinValue);

    // The rest of an order, once its stock is reserved.
    private async WorkflowTask<OrderResult> Fulfil(
        WorkflowContext ctx, OrderRequest request, string reservation)
    {
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
        // An order over 1000 waits for a person to approve it, for at
        // most one day. With no answer by then, Received is false.
        var total = request.UnitPrice * request.Quantity;
        if (total > 1000m)
        {
            var answer = await ctx.WaitSignal<bool>(
                "approval", TimeSpan.FromDays(1));
            if (!(answer.Received && answer.Value))
            {
                await Release();
                return Finish("not approved", "");
            }
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
        var shipment = await ctx.Activity("ship", CallRetry,
            attempt => carrier.Ship(
                attempt.IdempotencyKey, reservation, request.Item));
        return Finish("shipped", shipment);
    }
}
```

`OutOfStock` reaches the body as a value, so the `match` (in C#, the `switch`) handles it. The
handler never sees it, because a handler sees only exceptions.

The handler catches only a failure of `reserve-stock`, checked by `StepName`. The stand-in
warehouse refuses a call before it reserves anything, so after five refusals nothing has happened
and the order can end with a result, `warehouse unavailable`. A call that times out is different:
the warehouse may have reserved the stock, and the body would have to ask it or cancel the
reservation.

The exception carries `Error.Kind`, which tells why the step failed, for example `Exhausted` when
the attempts ran out, `DeadlineExceeded` when the step's deadline passed, or `OutcomeUnknown` when
a command got no reply. It does not carry the exception the call threw. The body sees only that
exception's type name and message, while a returned answer such as `StockAnswer` keeps its fields.
[Return an expected failure as a value](../concepts/workflows.html#Return-an-expected-failure-as-a-value)
compares the two.

A failure of any other step is not caught. It leaves the body, and the order **faults**: FCQRS
stores the failure as the order's end, and the order runs nothing more. A faulted order is final.
Nothing restarts it, and a start with the same ID stores nothing.

## Make the stand-ins fail

In `Program.fs` (`Program.cs`), replace the stand-in warehouse. It returns `OutOfStock` for a
gizmo and throws at every call for an anvil:

<!-- sample: orders/7-when-a-step-fails/fsharp Program.fs warehouse -->
```fsharp
// A stand-in for the warehouse. It answers that it has no gizmos, and it is
// busy at every call for an anvil. For any other item it is busy for the
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
                    // Out of stock is an answer, so it is returned.
                    printfn $"  warehouse has no gizmo (key {key})"
                    return OutOfStock
                else
                    calls[key] <- (match calls.TryGetValue key with
                                   | true, count -> count + 1
                                   | _ -> 1)
                    if calls[key] <= 2 || item = "anvil" then
                        // Being busy is a failed call, so it is thrown.
                        printfn $"  warehouse busy (key {key})"
                        invalidOp "The warehouse is busy"
                    if not (reservations.ContainsKey key) then
                        reservations[key] <- $"r-{reservations.Count + 1}"
                        let stock = $"{quantity} {item}"
                        printfn $"  warehouse reserved {stock} (key {key})"
                    return Reserved reservations[key]
            }
        member _.Release(key, reservation) =
            task {
                if releases.Add key then
                    printfn $"  warehouse released {reservation} (key {key})"
                return reservation
            }
```

<div class="cs-alt"></div>

<!-- sample: orders/7-when-a-step-fails/csharp Program.cs warehouse -->
```csharp
// A stand-in for the warehouse. It answers that it has no gizmos, and it is
// busy at every call for an anvil. For any other item it is busy for the
// first two calls with a key. It remembers keys, so a repeated call returns
// the first reservation.
public sealed class Warehouse : IWarehouse
{
    private readonly Dictionary<string, int> calls = new();
    private readonly Dictionary<string, string> reservations = new();
    private readonly HashSet<string> releases = new();

    public Task<StockAnswer> Reserve(string key, string item, int quantity)
    {
        if (item == "gizmo")
        {
            // Out of stock is an answer, so it is returned.
            Console.WriteLine($"  warehouse has no gizmo (key {key})");
            return Task.FromResult<StockAnswer>(new OutOfStock());
        }
        calls[key] = calls.GetValueOrDefault(key) + 1;
        if (calls[key] <= 2 || item == "anvil")
        {
            // Being busy is a failed call, so it is thrown.
            Console.WriteLine($"  warehouse busy (key {key})");
            throw new InvalidOperationException("The warehouse is busy");
        }
        if (!reservations.ContainsKey(key))
        {
            reservations[key] = $"r-{reservations.Count + 1}";
            Console.WriteLine(
                $"  warehouse reserved {quantity} {item} (key {key})");
        }
        return Task.FromResult<StockAnswer>(new Reserved(reservations[key]));
    }

    public Task<string> Release(string key, string reservation)
    {
        if (releases.Add(key))
            Console.WriteLine(
                $"  warehouse released {reservation} (key {key})");
        return Task.FromResult(reservation);
    }
}
```

Replace the stand-in carrier. It does not take crates:

<!-- sample: orders/7-when-a-step-fails/fsharp Program.fs carrier -->
```fsharp
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
```

<div class="cs-alt"></div>

<!-- sample: orders/7-when-a-step-fails/csharp Program.cs carrier -->
```csharp
// A stand-in for the carrier. It does not take crates. A call takes two
// seconds. With `crash`, the process stops after the carrier ships and
// before FCQRS stores the result.
public sealed class Carrier(bool crash) : ICarrier
{
    private readonly Dictionary<string, string> shipments = new();

    public Task<string> Ship(string key, string reservation, string item)
    {
        if (item == "crate")
        {
            Console.WriteLine($"  carrier does not take crates (key {key})");
            throw new InvalidOperationException(
                "The carrier does not take crates");
        }
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

Replace the code that starts the orders. `place` takes the item, and reports a faulted order. The
program places a gizmo, an anvil, and a crate, then starts `o-8`, a large order, and leaves it
waiting for an approval it never sends:

<!-- sample: orders/7-when-a-step-fails/fsharp Program.fs start -->
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

deposit "ada" 1500m
deposit "bob" 50m
// The warehouse has no gizmos.
place "o-5" "ada" "gizmo" 1 None
// The warehouse is busy at every call for an anvil.
place "o-6" "ada" "anvil" 1 None
// The carrier does not take crates.
place "o-7" "ada" "crate" 1 None

// o-8 costs 1200 and waits for an approval that this program never sends.
// Step 8 finds it still waiting.
let big = { Customer = "ada"; Item = "widget"; Quantity = 30; UnitPrice = 40m }
start "o-8" big

let rec waitForApproval () =
    match orders.Status "o-8" |> Async.RunSynchronously with
    | WorkflowStatus.Running steps as status
        when steps |> List.exists (fun step -> step.EndsWith " approval") ->
        printfn $"o-8 status: {status}"
    | _ ->
        Thread.Sleep 50
        waitForApproval ()

waitForApproval ()
```

<div class="cs-alt"></div>

<!-- sample: orders/7-when-a-step-fails/csharp Program.cs start -->
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

await Deposit("ada", 1500m);
await Deposit("bob", 50m);
// The warehouse has no gizmos.
await Place("o-5", "ada", "gizmo", 1, null);
// The warehouse is busy at every call for an anvil.
await Place("o-6", "ada", "anvil", 1, null);
// The carrier does not take crates.
await Place("o-7", "ada", "crate", 1, null);

// o-8 costs 1200 and waits for an approval that this program never sends.
// Step 8 finds it still waiting.
await Start("o-8", new OrderRequest("ada", "widget", 30, 40m));
while (true)
{
    var status = await orders.StatusAsync("o-8");
    if (status is WorkflowStatus.Running running
        && running.pending.Any(step => step.EndsWith(" approval")))
    {
        Console.WriteLine($"o-8 status: {status}");
        break;
    }
    await Task.Delay(50);
}
```

Replace the code that prints the journal. It now takes an order's ID and prints `o-7`'s:

<!-- sample: orders/7-when-a-step-fails/fsharp Program.fs journal -->
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

<!-- sample: orders/7-when-a-step-fails/csharp Program.cs journal -->
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

Delete `bin/Debug/net11.0/orders.db`, then run `dotnet run`. Keep the database afterwards: step 8
continues from it.

```text
Deposited 1500 to ada
Deposited 50 to bob
Started o-5
  warehouse has no gizmo (key order/o-5/0)
o-5 finished: out of stock
Started o-6
  warehouse busy (key order/o-6/0)
  warehouse busy (key order/o-6/0)
  warehouse busy (key order/o-6/0)
  warehouse busy (key order/o-6/0)
  warehouse busy (key order/o-6/0)
o-6 finished: warehouse unavailable
Started o-7
  warehouse busy (key order/o-7/0)
  warehouse busy (key order/o-7/0)
  warehouse reserved 1 crate (key order/o-7/0)
  carrier does not take crates (key order/o-7/5)
  carrier does not take crates (key order/o-7/5)
  carrier does not take crates (key order/o-7/5)
o-7 faulted: The carrier does not take crates
Started o-8
  warehouse busy (key order/o-8/0)
  warehouse busy (key order/o-8/0)
  warehouse reserved 30 widget (key order/o-8/0)
o-8 status: Running ["3 approval"]
Journal of order/default-shard/o-7:
  1 Started
  2 StepScheduled
  3 StepCompleted
  4 StepCompleted
  5 StepCompleted
  6 StepScheduled
  7 StepCompleted
  8 StepScheduled
  9 StepCompleted
  10 StepScheduled
  11 StepFailed
  12 Faulted
```

- `o-5`: the warehouse was called once. `reserve-stock` stored its answer, and the body returned
  the result `out of stock`.
- `o-6`: `reserve-stock` made its five attempts and failed. The body caught the failure and
  returned the result `warehouse unavailable`.
- `o-7`: `ship` made its three attempts and failed. Nothing caught it, so the order faulted. In
  F#, `Result` returns `WorkflowOutcome.Faulted` with the stored error. In C#, `ResultAsync` throws
  `WorkflowFaultedException`.
- `o-8` is waiting for its approval at position 3 when the program stops.

## What a faulted order leaves behind

`o-7`'s journal shows what happened before the failure. Records 6 and 7 are the payment: Ada's
account was charged. Records 8 and 9 are the packing timer. Records 10 and 11 are `ship`, which
began and failed. Record 12 ends the order.

So the customer paid, the stock is reserved, and nothing will ship. FCQRS does not undo the earlier
steps of a faulted order, because it cannot know what undoing them means. If a failure leaves work
to undo, the body must catch it and run the undo steps, as `release-stock` does after a refused
payment. A failure that nobody expected is for a person to look at: a faulted order and its
journal record what it did.

## Next

[Step 8: Add a step](add-a-step.html) changes the workflow while `o-8` is still waiting.

*)

(*** hide ***)
printfn "When a step fails example checked."
