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
// Registering the workflow also resumes the orders that were running when
// the process last stopped.
var orders = WorkflowHost.Register(
    actor, new OrderWorkflow(new Warehouse(), new Carrier()));
// docs:end

// docs:start
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

// Read the order's status while it waits for its timer.
while (true)
{
    var status = await orders.StatusAsync("o-1");
    if (status is WorkflowStatus.Running running
        && running.pending.Any(step => step.EndsWith(" sleep")))
    {
        Console.WriteLine($"o-1 status: {status}");
        break;
    }
    await Task.Delay(50);
}

// Wait for the order to end. The workflow does not depend on this wait.
var result = await orders.ResultAsync("o-1", TimeSpan.FromSeconds(30));
Console.WriteLine($"o-1 finished: reservation {result.Reservation}, "
    + $"shipment {result.Shipment}");
Console.WriteLine(
    $"  confirmation {result.Confirmation}, placed {result.PlacedAt:o}");

await Start("o-1");
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
// docs:end

// docs:carrier
// A stand-in for the carrier.
public sealed class Carrier : ICarrier
{
    private readonly Dictionary<string, string> shipments = new();

    public Task<string> Ship(string key, string reservation)
    {
        if (!shipments.ContainsKey(key))
        {
            shipments[key] = $"s-{shipments.Count + 1}";
            Console.WriteLine($"  carrier shipped {reservation} (key {key})");
        }
        return Task.FromResult(shipments[key]);
    }
}
// docs:end
