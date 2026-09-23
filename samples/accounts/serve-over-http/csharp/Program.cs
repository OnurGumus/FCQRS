using System.Diagnostics.CodeAnalysis;
using Dapper;
using FCQRS;
using FCQRS.Model;
using Microsoft.Data.Sqlite;
using static FCQRS.Common;
using static FCQRS.CSharp;
using static FCQRS.ProjectionStorage;
using static FCQRS.Projections;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// Startup is the same as in step 4: the accounts and their statement.
var database = Path.Combine(AppContext.BaseDirectory, "accounts.db");
var connectionString = $"Data Source={database}";
using (var connection = new SqliteConnection(connectionString))
    Statement.CreateTable(connection);
var store = new SqlProjectionStore(
    ProjectionSqlDialect.Sqlite, () => new SqliteConnection(connectionString));
var options = new TransactionalProjectionOptions("Statement", store);
builder.Services.AddFcqrs(connectionString, "accounts")
    .AddAggregate<Account>()
    .AddTransactionalProjection(options, Statement.Handle);

var app = builder.Build();

var invalid = Results.BadRequest(
    new { error = "The ID and owner must be non-blank, at most 255 characters." });

// docs:send
// Sends a command and waits until the statement includes the event it stored,
// so the balance in the response includes this change.
async Task<IResult> Send(
    string id, AccountCommand command, CancellationToken ct)
{
    var accounts = app.Services
        .GetRequiredService<Handler<AccountCommand, AccountEvent>>();
    var statement = app.Services.GetRequiredService<IProjection>();
    var cid = Values.NewCID();
    // Subscribe first: a notification sent before the subscription is lost.
    using var projected = statement.SubscribeForFirst(cid);
    try
    {
        var account = Values.CreateAggregateId(id);
        var reply = await accounts(_ => true, cid, account, command)
            .WaitAsync(ct);
        // A rejection is not stored. Report the account's reason.
        if (reply.EventDetails is Rejected rejected)
            return Results.UnprocessableEntity(new { error = rejected.Reason });
        await projected.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        using var connection = new SqliteConnection(connectionString);
        var balance = await connection.ExecuteScalarAsync<decimal>(
            """
            SELECT balance FROM statement WHERE account = @Id
            ORDER BY version DESC LIMIT 1
            """,
            new { Id = id });
        return Results.Ok(new { balance });
    }
    catch (TimeoutException)
    {
        // The account may have stored the event after all.
        return Results.Problem(
            "The command may have completed. " +
            "Read the statement before retrying.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
// docs:end

// docs:endpoints
// Each POST turns its request into one account command.
app.MapPost("/accounts/{id}",
    (string id, OpenRequest request, CancellationToken ct) =>
        Valid(id) && Valid(request.Owner)
            ? Send(id, new Open(request.Owner), ct)
            : Task.FromResult(invalid));
app.MapPost("/accounts/{id}/deposits",
    (string id, AmountRequest request, CancellationToken ct) =>
        Valid(id) ? Send(id, new Deposit(request.Amount), ct)
            : Task.FromResult(invalid));
app.MapPost("/accounts/{id}/withdrawals",
    (string id, AmountRequest request, CancellationToken ct) =>
        Valid(id) ? Send(id, new Withdraw(request.Amount), ct)
            : Task.FromResult(invalid));
// docs:end

// docs:query
// Reads one account's statement with SQL.
app.MapGet("/accounts/{id}/statement", async (string id) =>
{
    using var connection = new SqliteConnection(connectionString);
    var rows = (await connection.QueryAsync<Row>(
        """
        SELECT version, entry, amount, balance FROM statement
        WHERE account = @Id ORDER BY version
        """,
        new { Id = id })).AsList();
    return rows.Count == 0 ? Results.NotFound() : Results.Ok(rows);
});
// docs:end

app.Lifetime.ApplicationStarted.Register(() =>
    Console.WriteLine($"Listening on {string.Join(", ", app.Urls)}"));
app.Run();

// An ID or an owner must be non-blank and at most 255 characters.
static bool Valid([NotNullWhen(true)] string? value) =>
    !string.IsNullOrWhiteSpace(value) && value.Length <= 255;

// docs:requests
// The JSON bodies the POST endpoints accept.
record OpenRequest(string? Owner);
record AmountRequest(decimal Amount);

// One statement row, as the GET endpoint returns it.
sealed class Row
{
    public long Version { get; init; }
    public string Entry { get; init; } = "";
    public decimal Amount { get; init; }
    public decimal Balance { get; init; }
}
// docs:end
