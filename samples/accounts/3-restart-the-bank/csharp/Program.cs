using FCQRS;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static FCQRS.Common;
using static FCQRS.CSharp;

var database = Path.Combine(AppContext.BaseDirectory, "accounts.db");

var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
// Events go to a SQLite file next to the program; register the account rules.
builder.Services.AddFcqrs($"Data Source={database};", "accounts")
    .AddAggregate<Account>();
using var host = builder.Build();
await host.StartAsync();

// docs:send
// Sends commands to accounts and returns the event each one replied with.
var accounts = host.Services
    .GetRequiredService<Handler<AccountCommand, AccountEvent>>();
var alice = Values.CreateAggregateId("alice");

// Send a command and wait for the account's reply.
Task<Event<AccountEvent>> Request(AccountCommand command) =>
    accounts(_ => true, Values.NewCID(), alice, command);

void Show(Event<AccountEvent> reply)
{
    var description = Describe(reply.EventDetails);
    Console.WriteLine($"{description} (version {reply.Version})");
}

Show(await Request(new Open("Alice")));

// Deposit 10, 250 times, and print the last reply.
var replies = new List<Event<AccountEvent>>();
for (var i = 0; i < 250; i++)
    replies.Add(await Request(new Deposit(10m)));
Show(replies[^1]);
// docs:end

PrintTables(database);
await host.StopAsync();

static string Describe(AccountEvent @event) => @event switch
{
    Opened opened => $"Opened for {opened.Owner}",
    Deposited deposited => $"Deposited {deposited.Amount}",
    Withdrawn withdrawn => $"Withdrew {withdrawn.Amount}",
    Rejected rejected => $"Rejected: {rejected.Reason}"
};

// Applications do not read these tables. This program reads them only to show
// what FCQRS stored.
static void PrintTables(string database)
{
    using var connection = new SqliteConnection($"Data Source={database}");
    connection.Open();
    SqliteCommand Query(string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }
    // FCQRS saves a snapshot in the background after it replies. Wait up to ten
    // seconds until the newest snapshot that the 100-event cadence calls for is stored.
    using var caughtUp = Query(
        """
        SELECT (SELECT COALESCE(MAX(sequence_number), 0) FROM snapshot
                WHERE persistence_id = 'Account/default-shard/alice')
            >= (SELECT MAX(sequence_number) FROM journal
                WHERE persistence_id = 'Account/default-shard/alice') / 100 * 100
        """);
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while ((long)caughtUp.ExecuteScalar()! == 0 && DateTime.UtcNow < deadline)
        Thread.Sleep(50);
    using var count = Query(
        "SELECT COUNT(*) FROM journal WHERE persistence_id = 'Account/default-shard/alice'");
    Console.WriteLine();
    Console.WriteLine($"Journal: {count.ExecuteScalar()} events for Account/default-shard/alice");
    using var snapshots = Query(
        """
        SELECT sequence_number, json_extract(CAST(snapshot AS TEXT), '$.State') FROM snapshot
        WHERE persistence_id = 'Account/default-shard/alice' ORDER BY sequence_number
        """);
    using var rows = snapshots.ExecuteReader();
    Console.WriteLine("Snapshots:");
    while (rows.Read())
        Console.WriteLine($"  version {rows.GetInt64(0)}  {rows.GetString(1)}");
}
