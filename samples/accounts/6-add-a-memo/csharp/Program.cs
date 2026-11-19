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

// docs:continue
// This step is the bank's next release. On its first run, it copies the
// database step 5 wrote, so its journal starts with events without a memo.
var database = Path.Combine(AppContext.BaseDirectory, "accounts.db");
if (!File.Exists(database))
{
    // Step 5 builds into the same folder layout next to this step.
    var previous =
        AppContext.BaseDirectory.Replace("6-add-a-memo", "5-transfer-money");
    var step5 = Path.Combine(previous, "accounts.db");
    if (!File.Exists(step5))
    {
        Console.Error.WriteLine(
            "Run step 5 first: this step continues from its database.");
        return 1;
    }
    File.Copy(step5, database);
}
// docs:end

var connectionString = $"Data Source={database}";

// docs:register
// The new statement has its own table and its own projection name. FCQRS has no
// progress for that name yet, so the projection starts from the first event.
using (var connection = new SqliteConnection(connectionString))
    Statement.CreateTable(connection);
var store = new SqlProjectionStore(
    ProjectionSqlDialect.Sqlite, () => new SqliteConnection(connectionString));
var options = new TransactionalProjectionOptions("StatementV2", store);
// docs:end

var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
// Register the accounts, the new statement, and the transfer workflow.
builder.Services.AddFcqrs(connectionString, "accounts")
    .AddAggregate<Account>()
    .AddTransactionalProjection(options, Statement.Handle);
builder.Services
    .AddWorkflow<TransferWorkflow, TransferRequest, TransferOutcome>();
using var host = builder.Build();
await host.StartAsync();

var statement = host.Services.GetRequiredService<IProjection>();
var transfers = host.Services
    .GetRequiredService<IWorkflowClient<TransferRequest, TransferOutcome>>();

// docs:transfer
static string DescribeOutcome(TransferOutcome outcome) => outcome switch
{
    Completed => "completed",
    Refunded => "refunded",
    Refused refused => $"refused: {refused.Reason}"
};

// Start a transfer from Alice with a memo, and wait for it to end.
async Task Transfer(string id, string target, decimal amount, string? memo)
{
    var request = new TransferRequest(id, "alice", target, amount, memo);
    var started = await transfers.StartAsync(id, request);
    var repeated = started ? "" : " (already started)";
    var outcome = await transfers.ResultAsync(id, TimeSpan.FromSeconds(30));
    Console.WriteLine($"Transfer {id}: {DescribeOutcome(outcome)}{repeated}");
}

await Transfer("t3", "bob", 25m, "rent");
// docs:end

// Applications do not read the journal. This program reads it only to show how
// Alice's transfers are stored: without a memo before this step, with one after.
void PrintTransfers()
{
    using var connection = new SqliteConnection(connectionString);
    connection.Open();
    using var query = connection.CreateCommand();
    query.CommandText =
        """
        SELECT sequence_number,
               json_extract(CAST(message AS TEXT), '$.EventDetails."$value"')
        FROM journal
        WHERE persistence_id = 'Account/default-shard/alice'
          AND json_extract(CAST(message AS TEXT), '$.EventDetails."$case"') = 'TransferSent'
        ORDER BY sequence_number
        """;
    using var rows = query.ExecuteReader();
    Console.WriteLine();
    Console.WriteLine("Transfers stored for alice:");
    while (rows.Read())
        Console.WriteLine($"  {rows.GetInt64(0)}  {rows.GetString(1)}");
}

// Read one account's statement with SQL.
void PrintStatement(string account)
{
    using var connection = new SqliteConnection(connectionString);
    connection.Open();
    using var query = connection.CreateCommand();
    query.CommandText =
        """
        SELECT version, entry, memo, amount, balance FROM statement_v2
        WHERE account = $account ORDER BY version
        """;
    query.Parameters.AddWithValue("$account", account);
    using var rows = query.ExecuteReader();
    Console.WriteLine();
    Console.WriteLine($"Statement for {account}:");
    Console.WriteLine("  version  entry                    memo   amount  balance");
    while (rows.Read())
    {
        var (version, entry) = (rows.GetInt64(0), rows.GetString(1));
        var memo = rows.IsDBNull(2) ? "" : rows.GetString(2);
        var (amount, balance) = (rows.GetDecimal(3), rows.GetDecimal(4));
        Console.WriteLine($"  {version,7}  {entry,-24} {memo,-6}{amount,7}{balance,9}");
    }
}

// The statement follows the journal on its own; wait until it has read
// everything, including the events step 5 stored.
await statement.CatchUpAsync();
PrintTransfers();
PrintStatement("alice");
PrintStatement("bob");

await host.StopAsync();
return 0;
