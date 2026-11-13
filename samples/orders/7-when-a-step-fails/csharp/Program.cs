// docs:setup
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
// docs:end

// docs:register
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
// docs:end

// docs:start
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
// The carrier does not take crates.
await Place("o-6", "ada", "crate", 1, null);

// o-7 costs 1200 and waits for an approval that this program never sends.
// Step 8 finds it still waiting.
await Start("o-7", new OrderRequest("ada", "widget", 30, 40m));
while (true)
{
    var status = await orders.StatusAsync("o-7");
    if (status is WorkflowStatus.Running running
        && running.pending.Any(step => step.EndsWith(" approval")))
    {
        Console.WriteLine($"o-7 status: {status}");
        break;
    }
    await Task.Delay(50);
}
// docs:end

// docs:journal
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

PrintJournal("o-6");

await actor.Stop();
// docs:end

// docs:warehouse
// A stand-in for the warehouse. It has no gizmos, and it is busy for the
// first two calls with a key. It remembers keys, so a repeated call returns
// the first reservation.
public sealed class Warehouse : IWarehouse
{
    private readonly Dictionary<string, int> calls = new();
    private readonly Dictionary<string, string> reservations = new();
    private readonly HashSet<string> releases = new();

    public Task<string> Reserve(string key, string item, int quantity)
    {
        if (item == "gizmo")
        {
            Console.WriteLine($"  warehouse has no gizmo (key {key})");
            throw new InvalidOperationException("Out of stock");
        }
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
// docs:end

// docs:carrier
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
// docs:end
