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
// docs:end

// docs:journal
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
// docs:end

// docs:warehouse
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
// docs:end

// docs:carrier
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
// docs:end
