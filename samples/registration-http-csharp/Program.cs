using FCQRS;
using static FCQRS.CSharp;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var database = Path.Combine(AppContext.BaseDirectory, "registration-http.db");
var users = new UserView();
builder.Services.AddFcqrs($"Data Source={database};", "registration-http-csharp")
    .AddAggregate<Account>()
    .AddProjection(users.Project, lastOffset: 0);
await using var app = builder.Build();

// docs:register
app.MapPost("/accounts/{id}", async Task<IResult> (
    string id, RegistrationRequest request,
    Handler<RegisterUser, UserRegistered> accounts, CancellationToken ct) =>
{
    if (!Valid(id) || !Valid(request.Name))
        return Results.BadRequest(new {
            error = "ID and name must be non-blank (max 255 characters)."
        });

    try
    {
        var reply = await accounts(_ => true, Values.NewCID(),
            Values.CreateAggregateId(id), new RegisterUser(request.Name))
            .WaitAsync(ct);
        await users.WaitFor(id, ct);
        var body = new { id, name = reply.EventDetails.Name };
        return reply.Journaled?.Value == true
            ? Results.Created($"/accounts/{Uri.EscapeDataString(id)}", body)
            : Results.Ok(body);
    }
    catch (TimeoutException)
    {
        return Results.Problem(
            "Registration may have completed. Retry with the same account ID.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});
// docs:end

// docs:query
app.MapGet("/accounts/{id}", (string id) =>
{
    if (!Valid(id)) return Results.BadRequest();
    return users.TryGet(id, out var name)
        ? Results.Ok(new { id, name })
        : Results.NotFound();
});
// docs:end

app.Lifetime.ApplicationStarted.Register(() =>
    Console.WriteLine($"Listening on {string.Join(", ", app.Urls)}"));
await app.RunAsync();

static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 255;
public sealed record RegistrationRequest(string Name);
