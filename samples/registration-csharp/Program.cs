using System.Collections.Concurrent;
using FCQRS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static FCQRS.Common;
using static FCQRS.CSharp;

var accountId = "alice";
var id = Values.CreateAggregateId(accountId);
var database = Path.Combine(AppContext.BaseDirectory, "registration.db");
// docs:projection
var users = new ConcurrentDictionary<string, string>();
var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
void Project(long offset, object message)
{
    if (message is Event<UserRegistered> stored
        && stored.Sender?.Value.Equals(id) == true)
    {
        users[accountId] = stored.EventDetails.Name;
        ready.TrySetResult();
    }
}
// docs:end
// docs:startup
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
// Register the observer before sending. Offset 0 also reads earlier registrations.
builder.Services.AddFcqrs($"Data Source={database};", "registration-csharp")
    .AddAggregate<Account>()
    .AddProjection(Project, lastOffset: 0);
using var host = builder.Build();
await host.StartAsync();
// docs:end
try
{
    // docs:send
    var accounts = host.Services.GetRequiredService<Handler<RegisterUser, UserRegistered>>();
    var reply = await accounts(_ => true, Values.NewCID(), id, new RegisterUser("Alice"));
    // docs:end
    var result = reply.Journaled?.Value == true ? "Registered" : "Already registered";
    Console.WriteLine($"{result}: {reply.EventDetails.Name} (version {reply.Version})");
    await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
    Console.WriteLine($"Query: {users[accountId]}");
}
finally { await host.StopAsync(); }
