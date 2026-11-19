using FCQRS;
using FCQRS.Workflows;
using FCQRS.Workflows.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static FCQRS.Common;
using static FCQRS.CSharp;
using static FCQRS.Projections;

// Events go to a SQLite file next to the program. A second run continues
// from it.
var database = Path.Combine(AppContext.BaseDirectory, "accounts.db");

var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Services.AddSingleton<IWelcomes>(new Welcomes());
builder.Services.AddWorkflow<WelcomeWorkflow, string, string>();
// docs:projection
// Starts a welcome for each stored Opened event. The workflow ID comes from
// the event, so a start for an event the projection hands over again stores
// nothing. The projection stores how far it has read after the handler
// returns, so a crash before that hands the event over again.
builder.Services.AddFcqrs($"Data Source={database}", "accounts")
    .AddAggregate<Account>()
    .AddProjection(services =>
    {
        var welcomes = services
            .GetRequiredService<IWorkflowClient<string, string>>();
        return (Action<object>)(message =>
        {
            if (message is Event<AccountEvent>
                {
                    EventDetails: Opened opened, Sender: { } sender
                })
            {
                var id = $"welcome-{sender.Value}";
                welcomes.StartAsync(id, opened.Owner).GetAwaiter().GetResult();
            }
        });
    }, name: "welcome-starter", entities: ["Account"]);
// docs:end

using var host = builder.Build();
await host.StartAsync();

var accounts = host.Services.GetRequiredService<Handler<Open, AccountEvent>>();
var welcomes = host.Services
    .GetRequiredService<IWorkflowClient<string, string>>();
var starter = host.Services.GetRequiredService<IProjection>();

var reply = await accounts(
    _ => true, Values.NewCID(), Values.CreateAggregateId("alice"),
    new Open("Alice"));
Console.WriteLine(reply.EventDetails switch
{
    Opened opened =>
        $"Opened for {opened.Owner} (version {reply.Version}, stored)",
    Rejected rejected =>
        $"Rejected: {rejected.Reason} (version {reply.Version}, not stored)",
    _ => ""
});

// Wait until the projection has handled every stored event, so the welcome
// has started, then wait for it to end.
await starter.CatchUpAsync();
var result = await welcomes.ResultAsync("welcome-alice", TimeSpan.FromSeconds(30));
Console.WriteLine($"welcome-alice: {result}");

await host.StopAsync();

// A stand-in for the service that sends welcomes.
sealed class Welcomes : IWelcomes
{
    public Task<bool> Send(string key, string owner)
    {
        Console.WriteLine($"Sending a welcome to {owner}");
        return Task.FromResult(true);
    }
}
