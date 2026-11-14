// docs:setup
using Akka.Streams.Dsl;
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

// Put money in a customer's account.
async Task Deposit(string customer, decimal amount)
{
    await accounts.Handler(_ => true, Values.NewCID(),
        Values.CreateAggregateId(customer), new Deposit(amount));
    Console.WriteLine($"Deposited {amount} to {customer}");
}

// Ada pays for the orders. The deposit comes before the workflow is
// registered, so the program reads the status of the resumed orders right
// after registering, while they are still shipping.
await Deposit("ada", 500m);

// Run the program with --stop to stop it while two orders are shipping.
var stop = args.Contains("--stop");

// Registering the workflow also resumes the orders that were running when
// the process last stopped.
var orders = WorkflowHost.Register(actor, new OrderWorkflow(
    new Warehouse(), new Carrier(stop), accounts.Factory, new FraudCheck()));
// docs:end

// docs:batch
// Five orders from ada, one per item.
var batch = new[] { "widget", "gadget", "gizmo", "crate", "sprocket" }
    .Select(item => new OrderRequest("ada", item, 2, 40m))
    .ToList();

// The order ID comes from the element, so an element that arrives again
// reaches the order it started before.
string IdOf(OrderRequest request) => $"o-{request.Item}";
// docs:end

// docs:start
// Orders that registering resumed are running before the stream starts.
foreach (var id in new[] { "o-widget", "o-gadget" })
{
    if (await orders.StatusAsync(id) is WorkflowStatus.Running running)
    {
        var steps = string.Join(", ", running.pending);
        Console.WriteLine($"{id} is running: {steps}");
    }
}

// Prints an outcome as the flow emits it.
void Print(WorkflowEnded<OrderResult> ended)
{
    if (ended.IsFinished)
        Console.WriteLine($"{ended.Id} finished: {ended.Result.Status}");
    else if (ended.IsFaulted)
        Console.WriteLine($"{ended.Id} faulted: {ended.Error!.Message}");
    else
        Console.WriteLine($"{ended.Id} was cancelled");
}

// Counts the orders between entering the flow and leaving it. The stages
// of one stream run one at a time, so plain counters are enough.
var inFlow = 0;
var most = 0;

// Start an order per element, at most two at once, and emit each outcome
// in the batch's order. An order that takes longer than 30 seconds fails
// the stream.
await Source
    .From(batch)
    .Select(request =>
    {
        inFlow++;
        most = Math.Max(most, inFlow);
        return request;
    })
    .Via(WorkflowFlow.Run(orders, IdOf, 2, TimeSpan.FromSeconds(30)))
    .Select(ended =>
    {
        inFlow--;
        return ended;
    })
    .RunForeach(Print, actor.Materializer);

Console.WriteLine($"At most {most} orders were in the stream at once");
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

PrintJournal("o-widget");

await actor.Stop();
// docs:end

// docs:warehouse
// A stand-in for the warehouse. It has no gizmos, and it is busy for the
// first two calls with a key. It remembers keys, so a repeated call returns
// the first reservation. It prints nothing, and a lock keeps it correct
// when two orders call it at once.
public sealed class Warehouse : IWarehouse
{
    private readonly Dictionary<string, int> calls = new();
    private readonly Dictionary<string, string> reservations = new();

    public Task<string> Reserve(string key, string item, int quantity)
    {
        lock (calls)
        {
            if (item == "gizmo")
                throw new InvalidOperationException("Out of stock");
            calls[key] = calls.GetValueOrDefault(key) + 1;
            if (calls[key] <= 2)
                throw new InvalidOperationException("The warehouse is busy");
            if (!reservations.ContainsKey(key))
                reservations[key] = $"r-{reservations.Count + 1}";
            return Task.FromResult(reservations[key]);
        }
    }

    public Task<string> Release(string key, string reservation) =>
        Task.FromResult(reservation);
}
// docs:end

// docs:carrier
// A stand-in for the carrier. It does not take crates, and a call takes two
// seconds. With `stop`, the first call waits for a second one, and the
// second stops the process while both orders are inside `ship`.
public sealed class Carrier(bool stop) : ICarrier
{
    private readonly Dictionary<string, string> shipments = new();
    private int calls;

    public async Task<string> Ship(string key, string reservation, string item)
    {
        if (item == "crate")
            throw new InvalidOperationException(
                "The carrier does not take crates");
        if (stop)
        {
            if (Interlocked.Increment(ref calls) == 1)
                await Task.Delay(Timeout.Infinite);
            Console.WriteLine(
                "  the process stops while two orders are being shipped");
            Environment.Exit(1);
        }
        await Task.Delay(2000);
        lock (shipments)
        {
            if (!shipments.ContainsKey(key))
                shipments[key] = $"s-{shipments.Count + 1}";
            return shipments[key];
        }
    }
}
// docs:end

// docs:fraud
// A stand-in for the fraud check. It passes every order and prints nothing.
public sealed class FraudCheck : IFraudCheck
{
    public Task<bool> Check(string key, string customer, decimal total) =>
        Task.FromResult(true);
}
// docs:end
