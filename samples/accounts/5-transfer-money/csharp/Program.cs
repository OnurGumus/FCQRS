using FCQRS;
using FCQRS.Model;
using FCQRS.Workflows;
using FCQRS.Workflows.CSharp;
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

// The statement from step 4, with rows for transfers.
using (var connection = new SqliteConnection(connectionString))
    Statement.CreateTable(connection);
var store = new SqlProjectionStore(
    ProjectionSqlDialect.Sqlite, () => new SqliteConnection(connectionString));
var options = new TransactionalProjectionOptions("Statement", store);

var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
// docs:register
// Register the workflow beside the account. The host registers it at startup,
// which also resumes the transfers that were running when the process stopped.
builder.Services.AddFcqrs(connectionString, "accounts")
    .AddAggregate<Account>()
    .AddTransactionalProjection(options, Statement.Handle);
builder.Services
    .AddWorkflow<TransferWorkflow, TransferRequest, TransferOutcome>();
// docs:end
using var host = builder.Build();
await host.StartAsync();

var accounts = host.Services
    .GetRequiredService<Handler<AccountCommand, AccountEvent>>();
var statement = host.Services.GetRequiredService<IProjection>();
var transfers = host.Services
    .GetRequiredService<IWorkflowClient<TransferRequest, TransferOutcome>>();

// Print a reply. Journaled tells whether FCQRS stored the event.
void Show(Event<AccountEvent> reply)
{
    var stored = reply.Journaled?.Value == true ? "stored" : "not stored";
    var description = Describe(reply.EventDetails);
    Console.WriteLine($"{description} (version {reply.Version}, {stored})");
}

var alice = Values.CreateAggregateId("alice");
var bob = Values.CreateAggregateId("bob");

// Send a command and wait until the statement includes the event it stored.
async Task Send(Data.AggregateId account, AccountCommand command)
{
    var cid = Values.NewCID();
    using var projected = statement.SubscribeForFirst(cid);
    var reply = await accounts(_ => true, cid, account, command);
    if (reply.Journaled?.Value != false)
        await projected.Task.WaitAsync(TimeSpan.FromSeconds(30));
    Show(reply);
}

await Send(alice, new Open("Alice"));
await Send(alice, new Deposit(100m));
await Send(bob, new Open("Bob"));

// docs:transfer
static string DescribeOutcome(TransferOutcome outcome) => outcome switch
{
    Completed => "completed",
    Refunded => "refunded",
    Refused refused => $"refused: {refused.Reason}"
};

// Start a transfer from Alice and wait for it to end. A start with an ID that
// is already in the journal stores nothing, so a repeated request moves no
// money: the transfer's ID is the workflow's ID.
async Task Transfer(string id, string target, decimal amount)
{
    var request = new TransferRequest(id, "alice", target, amount);
    var started = await transfers.StartAsync(id, request);
    var repeated = started ? "" : " (already started)";
    var outcome = await transfers.ResultAsync(id, TimeSpan.FromSeconds(30));
    Console.WriteLine($"Transfer {id}: {DescribeOutcome(outcome)}{repeated}");
}

await Transfer("t1", "bob", 30m);
// Carol has no account, so this transfer comes back.
await Transfer("t2", "carol", 20m);
// docs:end

// docs:repeat
// After a restart, a workflow sends a command again when its reply was not
// stored. Do the same by hand:
await Send(bob, new ReceiveTransfer("t1", "alice", 30m));
// docs:end

// Read one account's statement with SQL.
void PrintStatement(string account)
{
    using var connection = new SqliteConnection(connectionString);
    connection.Open();
    using var query = connection.CreateCommand();
    query.CommandText =
        """
        SELECT version, entry, amount, balance FROM statement
        WHERE account = $account ORDER BY version
        """;
    query.Parameters.AddWithValue("$account", account);
    using var rows = query.ExecuteReader();
    Console.WriteLine();
    Console.WriteLine($"Statement for {account}:");
    Console.WriteLine("  version  entry                    amount  balance");
    while (rows.Read())
    {
        var (version, entry) = (rows.GetInt64(0), rows.GetString(1));
        var (amount, balance) = (rows.GetDecimal(2), rows.GetDecimal(3));
        Console.WriteLine($"  {version,7}  {entry,-24}{amount,7}{balance,9}");
    }
}

// The statement follows the journal on its own; wait until it has read
// everything the transfers stored.
await statement.CatchUpAsync();
PrintStatement("alice");
PrintStatement("bob");

await host.StopAsync();

static string Describe(AccountEvent @event) => @event switch
{
    Opened opened => $"Opened for {opened.Owner}",
    Deposited deposited => $"Deposited {deposited.Amount}",
    Withdrawn withdrawn => $"Withdrew {withdrawn.Amount}",
    TransferSent sent => $"Sent {sent.Amount} to {sent.Target} ({sent.TransferId})",
    TransferReceived received =>
        $"Received {received.Amount} from {received.Source} ({received.TransferId})",
    TransferRefunded refunded => $"Refunded {refunded.Amount} ({refunded.TransferId})",
    Rejected rejected => $"Rejected: {rejected.Reason}"
};
