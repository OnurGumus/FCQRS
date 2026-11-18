using Dapper;
using FCQRS;
using FCQRS.Workflows.CSharp;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static FCQRS.ProjectionStorage;
using static FCQRS.Projections;

// Each run starts with an empty journal, so its output is the same.
var database = Path.Combine(AppContext.BaseDirectory, "approvals.db");
foreach (var file in new[] { database, database + "-wal", database + "-shm" })
    File.Delete(file);

var connectionString = $"Data Source={database}";

// docs:projection
using (var connection = new SqliteConnection(connectionString))
    connection.Execute(Approvals.CreateTable);

// The projection reads only the quote workflow's journal, and commits each
// row with its read position.
var store = new SqlProjectionStore(
    ProjectionSqlDialect.Sqlite, () => new SqliteConnection(connectionString));
var options = new TransactionalProjectionOptions("quote-approvals", store)
{
    Entities = ["quote"]
};

// Keyed, so it runs beside the read model an application already projects.
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
var fcqrs = builder.Services.AddFcqrs(connectionString, "approvals")
    .AddKeyedTransactionalProjection(options, Approvals.Handle);
builder.Services.AddSingleton<IPriceService, FixedPriceService>();
builder.Services.AddSingleton<IMailer, QuietMailer>();
builder.Services.AddWorkflow<QuoteWorkflow, QuoteRequest, Quote>();
// docs:end

// docs:instances
using (var connection = new SqliteConnection(connectionString))
    connection.Execute(Instances.CreateTable);

// A second projection, with its own name and progress.
var instanceOptions =
    new TransactionalProjectionOptions("workflow-instances", store)
    {
        Entities = ["quote"]
    };
fcqrs.AddKeyedTransactionalProjection(instanceOptions, Instances.Handle);

List<Instance> ListInstances()
{
    using var connection = new SqliteConnection(connectionString);
    return connection
        .Query<Instance>(
            """
            select workflow, id, status, last_step as LastStep
            from workflow_instances order by workflow, id
            """)
        .ToList();
}
// docs:end

using var host = builder.Build();
await host.StartAsync();
var quotes = host.Services
    .GetRequiredService<IWorkflowClient<QuoteRequest, Quote>>();
var approvals =
    host.Services.GetRequiredKeyedService<IProjection>("quote-approvals");

// docs:list
// The quotes that wait for a person. The list lags the workflows by one
// projection pass.
List<string> Waiting()
{
    using var connection = new SqliteConnection(connectionString);
    return connection
        .Query<string>("select id from quote_approvals order by id")
        .ToList();
}
// docs:end

// docs:approve
// Approves one quote. StatusAsync asks the instance, which replays its journal
// first when it is not in memory.
async Task<bool> Approve(string id)
{
    var status = await quotes.StatusAsync(id);
    if (!status.IsWaitingFor("approval"))
        return false;
    await quotes.SignalAsync(id, "approval", true);
    return true;
}
// docs:end

// Quantities of 10, 100, and 200 cost 125, 1250, and 2500. The two over
// 1000 wait for a person.
foreach (var (id, quantity) in new[] { ("q-1", 10), ("q-2", 100), ("q-3", 200) })
    await quotes.StartAsync(id, new QuoteRequest("Ada", "ABC", quantity));

// Waits until the quotes have fetched their price and reached the approval.
await quotes.ResultAsync("q-1", TimeSpan.FromSeconds(30));
foreach (var id in new[] { "q-2", "q-3" })
    while (!(await quotes.StatusAsync(id)).IsWaitingFor("approval"))
        await Task.Delay(50);

await approvals.CatchUpAsync();
Console.WriteLine($"waiting for approval: [{string.Join("; ", Waiting())}]");

foreach (var id in new[] { "q-1", "q-2" })
    Console.WriteLine($"approve {id}: {(await Approve(id) ? "true" : "false")}");

var quote = await quotes.ResultAsync("q-2", TimeSpan.FromSeconds(30));
Console.WriteLine($"q-2: {quote.Receipt}");

await approvals.CatchUpAsync();
Console.WriteLine($"waiting for approval: [{string.Join("; ", Waiting())}]");

var instances =
    host.Services.GetRequiredKeyedService<IProjection>("workflow-instances");
await instances.CatchUpAsync();
foreach (var instance in ListInstances())
    Console.WriteLine(
        $"{instance.Workflow} {instance.Id}: {instance.Status} " +
        $"(last step {instance.LastStep})");

await host.StopAsync();

// Stand-ins for external systems.
public sealed class FixedPriceService : IPriceService
{
    public Task<decimal> Fetch(string key, string symbol) => Task.FromResult(12.5m);
}

public sealed class QuietMailer : IMailer
{
    public Task<string> Send(string key, string customer, decimal total) =>
        Task.FromResult($"quote of {total} sent to {customer}");
}
