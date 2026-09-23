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
// Each account ID gets its own aggregate instance.
var alice = Values.CreateAggregateId("alice");

// Send a command and return the account's reply.
Task<Event<AccountEvent>> Request(AccountCommand command) =>
    accounts(_ => true, Values.NewCID(), alice, command);

// Print a reply. Journaled tells whether FCQRS stored the event.
void Show(Event<AccountEvent> reply)
{
    var stored = reply.Journaled?.Value == true ? "stored" : "not stored";
    var description = Describe(reply.EventDetails);
    Console.WriteLine($"{description} (version {reply.Version}, {stored})");
}

async Task Send(AccountCommand command) => Show(await Request(command));

await Send(new Open("Alice"));
await Send(new Deposit(100m));
await Send(new Withdraw(30m));
await Send(new Withdraw(500m));
await Send(new Open("Alice"));
// docs:end

// docs:together
// Two withdrawals of 60 arrive at the same moment.
var replies = await Task.WhenAll(
    Request(new Withdraw(60m)), Request(new Withdraw(60m)));

// Print the stored one first.
foreach (var reply in replies.OrderBy(reply => reply.Journaled?.Value != true))
    Show(reply);
// docs:end

PrintJournal(database);
await host.StopAsync();

static string Describe(AccountEvent @event) => @event switch
{
    Opened opened => $"Opened for {opened.Owner}",
    Deposited deposited => $"Deposited {deposited.Amount}",
    Withdrawn withdrawn => $"Withdrew {withdrawn.Amount}",
    Rejected rejected => $"Rejected: {rejected.Reason}"
};

// Applications read stored events through projections (step 4). This reads the
// journal table directly only to show what FCQRS stored.
static void PrintJournal(string database)
{
    using var connection = new SqliteConnection($"Data Source={database}");
    connection.Open();
    using var query = connection.CreateCommand();
    query.CommandText =
        """
        SELECT sequence_number, json_extract(CAST(message AS TEXT), '$.EventDetails') FROM journal
        WHERE persistence_id = 'Account/default-shard/alice' ORDER BY sequence_number
        """;
    using var rows = query.ExecuteReader();
    Console.WriteLine();
    Console.WriteLine("Journal rows for Account/default-shard/alice:");
    while (rows.Read())
        Console.WriteLine($"  {rows.GetInt64(0)}  {rows.GetString(1)}");
}
