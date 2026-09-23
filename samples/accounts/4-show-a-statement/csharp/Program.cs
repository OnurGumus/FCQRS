using FCQRS;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static FCQRS.Common;
using static FCQRS.CSharp;
using static FCQRS.ProjectionStorage;
using static FCQRS.Projections;

var database = Path.Combine(AppContext.BaseDirectory, "accounts.db");
var connectionString = $"Data Source={database}";

// docs:register
// The statement table lives in the same SQLite file as the journal.
using (var connection = new SqliteConnection(connectionString))
    Statement.CreateTable(connection);

// FCQRS passes each new journal event to Statement.Handle, and commits the
// handler's rows and the projection's progress in one transaction.
var store = new SqlProjectionStore(
    ProjectionSqlDialect.Sqlite, () => new SqliteConnection(connectionString));
var options = new TransactionalProjectionOptions("Statement", store);

var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Services.AddFcqrs(connectionString, "accounts")
    .AddAggregate<Account>()
    .AddTransactionalProjection(options, Statement.Handle);
using var host = builder.Build();
await host.StartAsync();
// docs:end

// docs:send
// Sends commands to accounts and returns the event each one replied with.
var accounts = host.Services
    .GetRequiredService<Handler<AccountCommand, AccountEvent>>();
// Publishes a notification after it commits each event.
var statement = host.Services.GetRequiredService<IProjection>();
var alice = Values.CreateAggregateId("alice");

// Send a command and wait until the statement includes the event it stored.
async Task Send(AccountCommand command)
{
    var cid = Values.NewCID();
    // Subscribe first: a notification sent before the subscription is lost.
    using var projected = statement.SubscribeForFirst(cid);
    var reply = await accounts(_ => true, cid, alice, command);
    // A rejection is not stored, so no notification comes for it.
    if (reply.Journaled?.Value != false)
        await projected.Task.WaitAsync(TimeSpan.FromSeconds(30));
    var description = Describe(reply.EventDetails);
    Console.WriteLine($"{description} (version {reply.Version})");
}

await Send(new Open("Alice"));
await Send(new Deposit(100m));
await Send(new Withdraw(30m));
await Send(new Deposit(50m));
await Send(new Withdraw(500m));
// docs:end

// docs:query
// The statement is an ordinary table: read it with SQL.
void PrintStatement()
{
    using var connection = new SqliteConnection(connectionString);
    connection.Open();
    using var query = connection.CreateCommand();
    query.CommandText =
        """
        SELECT version, entry, amount, balance FROM statement
        WHERE account = 'alice' ORDER BY version
        """;
    using var rows = query.ExecuteReader();
    Console.WriteLine();
    Console.WriteLine("Statement for alice:");
    Console.WriteLine("  version  entry              amount  balance");
    while (rows.Read())
    {
        var (version, entry) = (rows.GetInt64(0), rows.GetString(1));
        var (amount, balance) = (rows.GetDecimal(2), rows.GetDecimal(3));
        Console.WriteLine($"  {version,7}  {entry,-18}{amount,7}{balance,9}");
    }
}

PrintStatement();
// docs:end

await host.StopAsync();

static string Describe(AccountEvent @event) => @event switch
{
    Opened opened => $"Opened for {opened.Owner}",
    Deposited deposited => $"Deposited {deposited.Amount}",
    Withdrawn withdrawn => $"Withdrew {withdrawn.Amount}",
    Rejected rejected => $"Rejected: {rejected.Reason}"
};
