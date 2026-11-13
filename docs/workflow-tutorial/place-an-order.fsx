(**
---
title: 1. Place an order
category: Workflow tutorial
categoryindex: 3
index: 1
---
*)
(*** hide ***)
#r "nuget: FCQRS.Workflows, 7.2.1"
#load "../../samples/orders/1-place-an-order/fsharp/Order.fs"

(**
# Step 1: Place an order

This tutorial builds an order workflow one step at a time. An order reserves stock at a warehouse,
waits for a person to approve a large total, charges the customer, and ships. In this step the
workflow does one thing: it reserves the stock.

Each page shows every line of the program. Start in an empty folder, add what the page shows, and
run it. The CQRS tutorial, which starts at [Open an account](../tutorial/open-an-account.html), is
not required before this one.

## What a workflow is

Placing an order takes several calls to other systems, and the process can stop between any two of
them. A **workflow** is a function that FCQRS runs for you and records as it goes. After each
step, FCQRS stores the step's result in a journal. When the process starts again, FCQRS runs the
function from the beginning, but takes each finished step's result from the journal instead of
running the step again.

Each order is one **instance** of the workflow, identified by an ID such as `o-1`.

## Create the project

Create a folder named `Orders` and put these files in it. The project file names the packages:
FCQRS.Workflows, which brings in FCQRS, and SQLite, where the journal is stored.

<!-- sample: orders/1-place-an-order/fsharp Orders.fsproj file -->
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net11.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Order.fs" />
    <Compile Include="Program.fs" />
    <PackageReference Include="FCQRS.Workflows" Version="7.2.1" />
    <PackageReference Include="SQLitePCLRaw.bundle_e_sqlite3"
                      Version="3.0.2" />
    <PackageReference Include="SourceGear.sqlite3" Version="3.50.4.5" />
  </ItemGroup>
</Project>
```

<div class="cs-alt" data-fs="xml" data-cs="xml"></div>

<!-- sample: orders/1-place-an-order/csharp Orders.csproj file -->
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net11.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="FCQRS.Workflows" Version="7.2.1" />
    <PackageReference Include="SQLitePCLRaw.bundle_e_sqlite3"
                      Version="3.0.2" />
    <PackageReference Include="SourceGear.sqlite3" Version="3.50.4.5" />
  </ItemGroup>
</Project>
```

The file is `Orders.fsproj` in F# and `Orders.csproj` in C#. The programs target .NET 11, and the
C# program uses C# 15 from [step 6](charge-the-customer.html) on. This `global.json` selects the
.NET 11 SDK, which was a release candidate at the time of writing:

<!-- sample: orders global.json file -->
```json
{
  "sdk": {
    "version": "11.0.100-rc.1.26425.128",
    "rollForward": "latestFeature",
    "allowPrerelease": true
  }
}
```

The program's code goes in two files: `Order.fs` (`Order.cs` in C#) for the workflow, and
`Program.fs` (`Program.cs`) for the program that runs it.

## Name the input and the result

<!-- sample: orders/1-place-an-order/fsharp Order.fs messages -->
```fsharp
module Order

open System.Threading.Tasks
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

<!-- sample: orders/1-place-an-order/csharp Order.cs messages -->
```csharp
using FCQRS.Workflows;
using FCQRS.Workflows.CSharp;

// What an order workflow is started with. FCQRS stores it in the journal.
public sealed record OrderRequest(
    string Customer, string Item, int Quantity, decimal UnitPrice);

// What the workflow returns. FCQRS stores it too.
public sealed record OrderResult(string Reservation);
```

A workflow takes one input and returns one result. FCQRS stores both as JSON, so their field
names are part of what is stored. After a field is renamed, the new code does not find that field
in the orders already stored.

## Describe the warehouse

<!-- sample: orders/1-place-an-order/fsharp Order.fs services -->
```fsharp
// The warehouse is an external system. The key is the same for every
// attempt of one step, so the warehouse can recognise a repeated call.
type IWarehouse =
    abstract Reserve:
        key: string * item: string * quantity: int -> Task<string>
```

<div class="cs-alt"></div>

<!-- sample: orders/1-place-an-order/csharp Order.cs services -->
```csharp
// The warehouse is an external system. The key is the same for every
// attempt of one step, so the warehouse can recognise a repeated call.
public interface IWarehouse
{
    Task<string> Reserve(string key, string item, int quantity);
}
```

The workflow reaches the warehouse through this interface. `Reserve` returns a reservation ID.
The key is explained with the stand-in warehouse below.

## Write the workflow

<!-- sample: orders/1-place-an-order/fsharp Order.fs workflow -->
```fsharp
// "order" is stored with every instance. It must not change.
let definition (warehouse: IWarehouse) =
    Workflow.define "order" (fun (request: OrderRequest) ->
        workflow {
            // An activity is the only place the body calls another system.
            // FCQRS stores its result. "reserve-stock" is stored too and
            // must not change while orders are running.
            let! reservation =
                activity "reserve-stock" Retry.none (fun attempt ->
                    warehouse.Reserve(
                        attempt.IdempotencyKey, request.Item, request.Quantity))
            return { Reservation = reservation }
        })
```

<div class="cs-alt"></div>

<!-- sample: orders/1-place-an-order/csharp Order.cs workflow -->
```csharp
// "order" is stored with every instance. It must not change.
public sealed class OrderWorkflow(IWarehouse warehouse)
    : Workflow<OrderRequest, OrderResult>("order")
{
    public override async WorkflowTask<OrderResult> Run(
        WorkflowContext ctx, OrderRequest request)
    {
        // An activity is the only place the body calls another system.
        // FCQRS stores its result. "reserve-stock" is stored too and
        // must not change while orders are running.
        var reservation = await ctx.Activity("reserve-stock", Retry.none,
            attempt => warehouse.Reserve(
                attempt.IdempotencyKey, request.Item, request.Quantity));
        return new OrderResult(reservation);
    }
}
```

The function that receives the request is the workflow's **body**. Its one step is an
**activity**: a call to another system, named `reserve-stock`. `Retry.none` makes one attempt; step
2 adds retries.

In F#, the body is a `workflow { }` block, and `let!` waits for a step. In C#, the body is the
`Run` method, and `await` on a `ctx` method waits for a step.

The workflow's name, `order`, and the step's name, `reserve-stock`, are stored with every order.
They identify the stored records when FCQRS reads them back, so changing either name breaks the
orders that are already stored. [Step 8](add-a-step.html) shows what happens when the steps
change.

## Start FCQRS

<!-- sample: orders/1-place-an-order/fsharp Program.fs setup -->
```fsharp
open System
open System.Collections.Generic
open System.IO
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

<!-- sample: orders/1-place-an-order/csharp Program.cs setup -->
```csharp
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

This is the top of `Program.fs` (`Program.cs`). It starts FCQRS with its journal in `orders.db`,
next to the compiled program.

## Add a stand-in warehouse

<!-- sample: orders/1-place-an-order/fsharp Program.fs warehouse -->
```fsharp
// A stand-in for the warehouse. It remembers keys, so a repeated call
// returns the first reservation instead of reserving again.
type Warehouse() =
    let reservations = Dictionary<string, string>()
    interface IWarehouse with
        member _.Reserve(key, item, quantity) =
            task {
                if not (reservations.ContainsKey key) then
                    reservations[key] <- $"r-{reservations.Count + 1}"
                    printfn
                        $"  warehouse reserved {quantity} {item} (key {key})"
                return reservations[key]
            }
```

<div class="cs-alt"></div>

<!-- sample: orders/1-place-an-order/csharp Program.cs warehouse -->
```csharp
// A stand-in for the warehouse. It remembers keys, so a repeated call
// returns the first reservation instead of reserving again.
public sealed class Warehouse : IWarehouse
{
    private readonly Dictionary<string, string> reservations = new();

    public Task<string> Reserve(string key, string item, int quantity)
    {
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

In F#, this type follows the startup code. In C#, it goes at the end of `Program.cs`, because a
file with top-level statements declares its types after them.

The program has no real warehouse, so this class stands in for one and prints each reservation.
It reserves once per key. FCQRS gives every attempt of one step the same key, the
**idempotency key**, such as `order/o-1/0`: the workflow's name, the order's ID, and the step's
position. A real warehouse that remembers keys in the same way can tell a repeated call from a new
one.

A repeated call can happen. FCQRS stores a step's result after the call returns, so a process that
stops between the two runs the step again when it starts. An activity runs **at least once**.

## Register the workflow

<!-- sample: orders/1-place-an-order/fsharp Program.fs register -->
```fsharp
// Registering the workflow also resumes the orders that were running when
// the process last stopped.
let orders = Workflows.register api (definition (Warehouse()))
```

<div class="cs-alt"></div>

<!-- sample: orders/1-place-an-order/csharp Program.cs register -->
```csharp
// Registering the workflow also resumes the orders that were running when
// the process last stopped.
var orders = WorkflowHost.Register(actor, new OrderWorkflow(new Warehouse()));
```

`orders` starts orders and reads their state.

## Start an order

<!-- sample: orders/1-place-an-order/fsharp Program.fs start -->
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

// Wait for the order to end. The workflow does not depend on this wait.
let outcome =
    orders.Result "o-1" (TimeSpan.FromSeconds 30.0) |> Async.RunSynchronously

match outcome with
| WorkflowOutcome.Finished result ->
    printfn $"o-1 finished: reservation {result.Reservation}"
| other -> printfn $"o-1 ended: {other}"

start "o-1"
```

<div class="cs-alt"></div>

<!-- sample: orders/1-place-an-order/csharp Program.cs start -->
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

// Wait for the order to end. The workflow does not depend on this wait.
var result = await orders.ResultAsync("o-1", TimeSpan.FromSeconds(30));
Console.WriteLine($"o-1 finished: reservation {result.Reservation}");

await Start("o-1");
```

Starting `o-1` stores the request and returns. The order runs on its own; waiting for its result
is optional. The program starts `o-1` a second time to show that an ID starts one order only.

## Look at the journal

<!-- sample: orders/1-place-an-order/fsharp Program.fs journal -->
```fsharp
// Applications do not read this table. This program reads it only to show
// what FCQRS stored for o-1.
let printJournal () =
    use connection = new SqliteConnection($"Data Source={database}")
    connection.Open()
    let command = connection.CreateCommand()
    command.CommandText <-
        "SELECT sequence_number, manifest FROM journal
         WHERE persistence_id = 'order/default-shard/o-1'
         ORDER BY sequence_number"
    use rows = command.ExecuteReader()
    printfn "Journal of order/default-shard/o-1:"
    while rows.Read() do
        // The manifest names the stored type, for example
        // "FCQRS.Workflows.WorkflowRecord+Started, FCQRS.Workflows".
        let manifest = rows.GetString 1
        let record = manifest.Split(',').[0].Split('+').[1]
        let number = rows.GetInt64 0
        printfn $"  {number} {record}"

printJournal ()
api.Stop().Wait()
```

<div class="cs-alt"></div>

<!-- sample: orders/1-place-an-order/csharp Program.cs journal -->
```csharp
// Applications do not read this table. This program reads it only to show
// what FCQRS stored for o-1.
using (var connection = new SqliteConnection($"Data Source={database}"))
{
    connection.Open();
    var command = connection.CreateCommand();
    command.CommandText = """
        SELECT sequence_number, manifest FROM journal
        WHERE persistence_id = 'order/default-shard/o-1'
        ORDER BY sequence_number
        """;
    using var rows = command.ExecuteReader();
    Console.WriteLine("Journal of order/default-shard/o-1:");
    while (rows.Read())
    {
        // The manifest names the stored type, for example
        // "FCQRS.Workflows.WorkflowRecord+Started, FCQRS.Workflows".
        var record = rows.GetString(1).Split(',')[0].Split('+')[1];
        Console.WriteLine($"  {rows.GetInt64(0)} {record}");
    }
}

await actor.Stop();
```

This is the end of the program's statements. It prints the kind of each record FCQRS stored for
`o-1`, and stops FCQRS.

## Run it

From the `Orders` folder:

```text
dotnet run
```

```text
Started o-1
  warehouse reserved 2 widget (key order/o-1/0)
o-1 finished: reservation r-1
o-1 is already started (Completed)
Journal of order/default-shard/o-1:
  1 Started
  2 StepScheduled
  3 StepCompleted
  4 Finished
```

The order ran its one step and finished. The second start found `o-1` in the journal, stored
nothing, and returned its status, `Completed`.

`order/default-shard/o-1` identifies the order in the journal. Its four records are:

- `Started`: the request, stored before the start returned.
- `StepScheduled`: `reserve-stock` began, at position 0. A step with this record and no result
  was running when the process stopped, so FCQRS runs it again.
- `StepCompleted`: the step's result, `r-1`.
- `Finished`: the workflow's result.

## Run it again

```text
o-1 is already started (Completed)
o-1 finished: reservation r-1
o-1 is already started (Completed)
Journal of order/default-shard/o-1:
  1 Started
  2 StepScheduled
  3 StepCompleted
  4 Finished
```

The journal in `orders.db` still holds `o-1`, so the first start stores nothing, and the result
comes from the journal. The warehouse prints nothing: it is not called again. Each step from here
on starts new orders, so delete `bin/Debug/net11.0/orders.db` before you run the next step's
program.

## Next

[Step 2: Retry the warehouse](retry-the-warehouse.html) makes the warehouse fail some calls and
gives the step a retry policy.

*)

(*** hide ***)
let checkedDefinition = Order.definition { new Order.IWarehouse with member _.Reserve(_, _, _) = System.Threading.Tasks.Task.FromResult "r" }
printfn "Place an order example checked."
