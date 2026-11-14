(**
---
title: 6. Charge the customer
category: Workflow tutorial
categoryindex: 3
index: 6
---
*)
(*** hide ***)
#r "nuget: FCQRS.Workflows, 7.5.0"
#load "../../samples/orders/6-charge-the-customer/fsharp/Account.fs"
#load "../../samples/orders/6-charge-the-customer/fsharp/Order.fs"

(**
# Step 6: Charge the customer

An order must be paid before it ships. Each customer has an account, and the workflow asks the
account to take the money. The account can refuse, and then the order must give its stock back.

This page changes the program from [step 5](restart-during-shipping.html).

## Add an account

The account is an **aggregate**: FCQRS keeps one instance per customer, gives it one command at a
time, and stores the events it accepts. Its balance is computed from those events. The [CQRS
tutorial](../tutorial/open-an-account.html) builds an account step by step; this one has only what
the order needs.

Add a file `Account.fs` (`Account.cs`):

<!-- sample: orders/6-charge-the-customer/fsharp Account.fs messages -->
```fsharp
module Account

open FCQRS.Common

// What the program and the order workflow can ask an account to do.
type AccountCommand =
    | Deposit of amount: decimal
    | Debit of payment: string * amount: decimal

// What the account replies. Rejected is a reply only: it is never stored.
type AccountEvent =
    | Deposited of amount: decimal
    | Debited of payment: string * amount: decimal
    | Rejected of reason: string

// What the account knows now. Payments holds the payments it has taken, so
// a repeated debit takes the money once.
type AccountState = { Balance: decimal; Payments: Set<string> }

// The state before the account's first event.
let initial = { Balance = 0m; Payments = Set.empty }
```

<div class="cs-alt"></div>

<!-- sample: orders/6-charge-the-customer/csharp Account.cs messages -->
```csharp
using System.Collections.Immutable;
using static FCQRS.Common;
using static FCQRS.CSharp;

// What the program and the order workflow can ask an account to do.
public union AccountCommand(Deposit, Debit);
public sealed record Deposit(decimal Amount);
public sealed record Debit(string Payment, decimal Amount);

// What the account replies. Rejected is a reply only: it is never stored.
public union AccountEvent(Deposited, Debited, Rejected);
public sealed record Deposited(decimal Amount);
public sealed record Debited(string Payment, decimal Amount);
public sealed record Rejected(string Reason);

// What the account knows now. Payments holds the payments it has taken, so
// a repeated debit takes the money once.
public sealed record AccountState(
    decimal Balance, ImmutableHashSet<string> Payments);
```

A **command** asks the account to do something. An **event** records what it did, or, as
`Rejected`, answers without recording anything. In C#, `union` (new in C# 15) declares a closed set
of cases, as an F# union does.

<!-- sample: orders/6-charge-the-customer/fsharp Account.fs rules -->
```fsharp
// Chooses what to do with a command, based on the current state.
let decide (command: Command<AccountCommand>) (state: AccountState) =
    match command.CommandDetails with
    | Deposit amount -> PersistEvent(Deposited amount)
    // A repeated debit gets the first answer. It is checked before the
    // balance, because the first debit has already lowered the balance.
    | Debit(payment, amount) when state.Payments.Contains payment ->
        DeferEvent(Debited(payment, amount))
    | Debit(_, amount) when amount > state.Balance ->
        DeferEvent(Rejected $"Insufficient funds: {state.Balance} available")
    | Debit(payment, amount) -> PersistEvent(Debited(payment, amount))

// Applies one event. A rejection or a repeated reply changes nothing.
let fold (event: Event<AccountEvent>) (state: AccountState) =
    match event.EventDetails with
    | Deposited amount -> { state with Balance = state.Balance + amount }
    | Debited(payment, _) when state.Payments.Contains payment -> state
    | Debited(payment, amount) ->
        { state with
            Balance = state.Balance - amount
            Payments = state.Payments.Add payment
        }
    | Rejected _ -> state
```

<div class="cs-alt"></div>

<!-- sample: orders/6-charge-the-customer/csharp Account.cs rules -->
```csharp
public sealed class Account
    : Aggregate<AccountState, AccountCommand, AccountEvent>
{
    // The name stored with every event of this aggregate.
    public override string EntityName => "Account";
    // The state before the account's first event.
    public override AccountState InitialState => new(0m, []);

    // Chooses what to do with a command, based on the current state.
    public override EventAction<AccountEvent> HandleCommand(
        Command<AccountCommand> command, AccountState state) =>
        command.CommandDetails switch
        {
            Deposit deposit => Store(new Deposited(deposit.Amount)),
            // A repeated debit gets the first answer. It is checked before
            // the balance, because the first debit has already lowered it.
            Debit debit when state.Payments.Contains(debit.Payment) =>
                Repeat(new Debited(debit.Payment, debit.Amount)),
            Debit debit when debit.Amount > state.Balance =>
                Reject($"Insufficient funds: {state.Balance} available"),
            Debit debit => Store(new Debited(debit.Payment, debit.Amount))
        };

    // Applies one event. A rejection or a repeated reply changes nothing.
    public override AccountState ApplyEvent(
        Event<AccountEvent> stored, AccountState state) =>
        stored.EventDetails switch
        {
            Deposited deposited =>
                state with { Balance = state.Balance + deposited.Amount },
            Debited debited when state.Payments.Contains(debited.Payment) =>
                state,
            Debited debited => state with
            {
                Balance = state.Balance - debited.Amount,
                Payments = state.Payments.Add(debited.Payment)
            },
            Rejected => state
        };

    // Stores the event and replies with it.
    static EventAction<AccountEvent> Store(AccountEvent @event) =>
        EventActions.Persist(@event);

    // Replies without storing anything.
    static EventAction<AccountEvent> Reject(string reason) =>
        EventActions.Defer<AccountEvent>(new Rejected(reason));

    // Replies with an earlier answer again, without storing it.
    static EventAction<AccountEvent> Repeat(AccountEvent @event) =>
        EventActions.Defer(@event);
}
```

`decide` (`HandleCommand` in C#) answers a command. `PersistEvent` stores the event and replies
with it. `DeferEvent` replies without storing. `fold` (`ApplyEvent`) applies a stored event to the
state.

The first rule for `Debit` is the one a workflow depends on. A workflow can send the same command
twice, as the next sections show. The account recognises a repeat by its payment ID and answers it
with `Debited` again, without taking the money twice. It checks for the repeat before it checks the
balance: after the first debit, the balance may be too low for a second one, and a repeat answered
with `Rejected` would tell the workflow that a payment that went through had failed.

In F#, add `Account.fs` to the project before `Order.fs`. In C#, the project compiles every `.cs`
file in the folder, so `Orders.csproj` does not change:

<!-- sample: orders/6-charge-the-customer/fsharp Orders.fsproj file -->
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net11.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Account.fs" />
    <Compile Include="Order.fs" />
    <Compile Include="Program.fs" />
    <PackageReference Include="FCQRS.Workflows" Version="7.5.0" />
    <PackageReference Include="SQLitePCLRaw.bundle_e_sqlite3"
                      Version="3.0.2" />
    <PackageReference Include="SourceGear.sqlite3" Version="3.50.4.5" />
  </ItemGroup>
</Project>
```

## Charge in the workflow

In F#, `Order.fs` opens two more namespaces: `FCQRS.FSharp` for the account handle's type, and
`Account`. `Order.cs` needs no change here:

<!-- sample: orders/6-charge-the-customer/fsharp Order.fs messages -->
```fsharp
module Order

open System
open System.Threading.Tasks
open FCQRS.Common
open FCQRS.FSharp
open FCQRS.Workflows
open Account

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

The warehouse gets a second operation, which gives back a reservation:

<!-- sample: orders/6-charge-the-customer/fsharp Order.fs services -->
```fsharp
// The warehouse is an external system. The key is the same for every
// attempt of one step, so the warehouse can recognise a repeated call.
type IWarehouse =
    abstract Reserve:
        key: string * item: string * quantity: int -> Task<string>
    abstract Release: key: string * reservation: string -> Task<string>

// The carrier is another external system. It returns a shipment ID.
type ICarrier =
    abstract Ship: key: string * reservation: string -> Task<string>
```

<div class="cs-alt"></div>

<!-- sample: orders/6-charge-the-customer/csharp Order.cs services -->
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
    Task<string> Ship(string key, string reservation);
}
```

Replace the workflow:

<!-- sample: orders/6-charge-the-customer/fsharp Order.fs workflow -->
```fsharp
// "order" is stored with every instance. It must not change.
let definition
    (warehouse: IWarehouse)
    (carrier: ICarrier)
    (accounts: AggregateHandle<AccountCommand, AccountEvent>)
    =
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
                    // Packing takes two seconds. The timer's due time is
                    // stored, so a restart does not start the wait again.
                    do! sleep (TimeSpan.FromSeconds 2.0)
                    let! shipment =
                        activity "ship" (Retry.times 3) (fun attempt ->
                            carrier.Ship(attempt.IdempotencyKey, reservation))
                    return finish "shipped" shipment
        })
```

<div class="cs-alt"></div>

<!-- sample: orders/6-charge-the-customer/csharp Order.cs workflow -->
```csharp
// "order" is stored with every instance. It must not change.
public sealed class OrderWorkflow(
    IWarehouse warehouse, ICarrier carrier, AggregateFactory accounts)
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
        OrderResult Finish(string status, string shipment) =>
            new(status, reservation, shipment, confirmation, placedAt);
        // Gives the stock back when the order does not ship.
        Step<string> Release() =>
            ctx.Activity("release-stock", ReserveRetry, attempt =>
                warehouse.Release(attempt.IdempotencyKey, reservation));
        // An order over 1000 waits for a person to approve it, for at
        // most three seconds in this program. With no answer by then,
        // Received is false.
        var total = request.UnitPrice * request.Quantity;
        if (total > 1000m)
        {
            var answer = await ctx.WaitSignal<bool>(
                "approval", TimeSpan.FromSeconds(3));
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
        var shipment = await ctx.Activity("ship", Retry.times(3),
            attempt => carrier.Ship(attempt.IdempotencyKey, reservation));
        return Finish("shipped", shipment);
    }
}
```

`send accounts request.Customer (Debit(payment, total))` (`ctx.Send` in C#) is a **command step**.
It sends `Debit` to the customer's account and gives back the event the account replied with. A
`Rejected` reply is the account's verdict, not a failed step: the step succeeds, and the body
decides what a refusal means for the order.

The step's result is stored like an activity's, so a replay does not send the command again. If the
process stops after the step began and before the reply was stored, FCQRS sends the command again,
with the same correlation ID, and the account's repeat rule answers it.

The step waits up to five minutes for a reply, and while it waits it sends the command again,
with the same correlation ID, each time an attempt times out. With no reply by then, it fails as
`OutcomeUnknown`. That is not
a refusal: the account may have taken the money, and its reply may have been lost.
[Step 7](when-a-step-fails.html) shows how a body catches a failed step.

`release-stock` undoes `reserve-stock` when the order does not ship, either because nobody approved
it or because the account refused. Such an undo step is called **compensation**. It is an ordinary
activity with its own key, so the warehouse must recognise a repeated release as well.

## Run the account and the new orders

In F#, `Program.fs` opens `FCQRS.Common` for the aggregate's settings, and `Account`:

<!-- sample: orders/6-charge-the-customer/fsharp Program.fs setup -->
```fsharp
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
```

Replace the stand-in warehouse. It now releases, once per key:

<!-- sample: orders/6-charge-the-customer/fsharp Program.fs warehouse -->
```fsharp
// A stand-in for the warehouse. It is busy for the first two calls with a
// key. It remembers keys, so a repeated call returns the first reservation.
type Warehouse() =
    let calls = Dictionary<string, int>()
    let reservations = Dictionary<string, string>()
    let releases = HashSet<string>()
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
        member _.Release(key, reservation) =
            task {
                if releases.Add key then
                    printfn $"  warehouse released {reservation} (key {key})"
                return reservation
            }
```

<div class="cs-alt"></div>

<!-- sample: orders/6-charge-the-customer/csharp Program.cs warehouse -->
```csharp
// A stand-in for the warehouse. It is busy for the first two calls with a
// key. It remembers keys, so a repeated call returns the first reservation.
public sealed class Warehouse : IWarehouse
{
    private readonly Dictionary<string, int> calls = new();
    private readonly Dictionary<string, string> reservations = new();
    private readonly HashSet<string> releases = new();

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

    public Task<string> Release(string key, string reservation)
    {
        if (releases.Add(key))
            Console.WriteLine(
                $"  warehouse released {reservation} (key {key})");
        return Task.FromResult(reservation);
    }
}
```

Replace the registration. The program registers the account before the workflow, because
registering the workflow resumes stored orders at once, and they may send commands:

<!-- sample: orders/6-charge-the-customer/fsharp Program.fs register -->
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
    Workflows.register api (definition (Warehouse()) (Carrier crash) accounts)
```

<div class="cs-alt"></div>

<!-- sample: orders/6-charge-the-customer/csharp Program.cs register -->
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
    new Warehouse(), new Carrier(crash), accounts.Factory));
```

Replace the code that starts the orders. It puts money in two accounts and places four orders:

<!-- sample: orders/6-charge-the-customer/fsharp Program.fs start -->
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
let place id customer quantity approval =
    let request = {
        Customer = customer
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

deposit "ada" 1500m
deposit "bob" 50m
place "o-1" "ada" 2 None
// 30 widgets at 40 cost 1200. Ada approves, and her account pays.
place "o-2" "ada" 30 (Some true)
// 50 widgets cost 2000. Nobody approves this order.
place "o-3" "bob" 50 None
// 2 widgets cost 80, and Bob has 50.
place "o-4" "bob" 2 None
```

<div class="cs-alt"></div>

<!-- sample: orders/6-charge-the-customer/csharp Program.cs start -->
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
async Task Place(string id, string customer, int quantity, bool? approval)
{
    await Start(id, new OrderRequest(customer, "widget", quantity, 40m));
    if (approval is bool approve)
        await orders.SignalAsync(id, "approval", approve);
    var result = await orders.ResultAsync(id, TimeSpan.FromSeconds(30));
    Console.WriteLine(result.Status == "shipped"
        ? $"{id} finished: shipped as {result.Shipment}"
        : $"{id} finished: {result.Status}");
}

await Deposit("ada", 1500m);
await Deposit("bob", 50m);
await Place("o-1", "ada", 2, null);
// 30 widgets at 40 cost 1200. Ada approves, and her account pays.
await Place("o-2", "ada", 30, true);
// 50 widgets cost 2000. Nobody approves this order.
await Place("o-3", "bob", 50, null);
// 2 widgets cost 80, and Bob has 50.
await Place("o-4", "bob", 2, null);
```

## Run it

Delete `bin/Debug/net11.0/orders.db`, then run `dotnet run`:

```text
Deposited 1500 to ada
Deposited 50 to bob
Started o-1
  warehouse busy (key order/o-1/0)
  warehouse busy (key order/o-1/0)
  warehouse reserved 2 widget (key order/o-1/0)
  carrier shipped r-1 (key order/o-1/5)
o-1 finished: shipped as s-1
Started o-2
  warehouse busy (key order/o-2/0)
  warehouse busy (key order/o-2/0)
  warehouse reserved 30 widget (key order/o-2/0)
  carrier shipped r-2 (key order/o-2/6)
o-2 finished: shipped as s-2
Started o-3
  warehouse busy (key order/o-3/0)
  warehouse busy (key order/o-3/0)
  warehouse reserved 50 widget (key order/o-3/0)
  warehouse released r-3 (key order/o-3/4)
o-3 finished: not approved
Started o-4
  warehouse busy (key order/o-4/0)
  warehouse busy (key order/o-4/0)
  warehouse reserved 2 widget (key order/o-4/0)
  warehouse released r-4 (key order/o-4/4)
o-4 finished: payment refused
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
  10 StepScheduled
  11 StepCompleted
  12 Finished
```

- `o-1` and `o-2` were paid from Ada's 1500, which leaves her 220. The payment step took position 3
  in `o-1` and 4 in `o-2`, after the approval, so the carrier's keys end in 5 and 6.
- `o-3` was not approved, and its stock was released.
- `o-4` costs 80 and Bob has 50. The account refused the debit, and the stock was released.

`o-1`'s journal has two more records than in step 5: records 6 and 7 are the payment step, a
`StepScheduled` when the command was sent and a `StepCompleted` with the reply. The packing timer
(8 and 9) and `ship` (10 and 11) follow it.

## Next

[Step 7: When a step fails for good](when-a-step-fails.html) handles a step whose attempts run out.

*)

(*** hide ***)
open FCQRS.Common
open FCQRS.CSharp
open Account

let expect name actual expected =
    if actual <> expected then failwithf "%s: expected %A but got %A" name expected actual

let paid = fold (TestEnvelope.Event(Debited("p-1", 80m), 2L)) { Balance = 100m; Payments = Set.empty }
expect "debit" (decide (TestEnvelope.Command(Debit("p-2", 80m))) { Balance = 100m; Payments = Set.empty }) (PersistEvent(Debited("p-2", 80m)))
// A repeat after the balance fell gets the first answer, not a rejection.
expect "repeat" (decide (TestEnvelope.Command(Debit("p-1", 80m))) paid) (DeferEvent(Debited("p-1", 80m)))
expect "refuse" (decide (TestEnvelope.Command(Debit("p-3", 80m))) paid) (DeferEvent(Rejected "Insufficient funds: 20 available"))
printfn "Charge the customer example checked."
