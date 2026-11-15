using FCQRS.Workflows.CSharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using static FCQRS.CSharp;

// A new journal for every run, so the price service is always called.
var database = Path.Combine(
    Path.GetTempPath(), $"fcqrs-resilience-{Guid.NewGuid():N}.db");
var logging = LoggerFactory.Create(_ => { });
var configuration = new ConfigurationBuilder().Build();
var actor = ActorApi.Create(
    configuration, logging, $"Data Source={database}", "resilience");

var prices = new FlakyPriceService();
var flow = WorkflowHost.Register(actor, new PriceWorkflow(prices));

await flow.StartAsync("p-1", "ABC");
var price = await flow.ResultAsync("p-1", TimeSpan.FromSeconds(30));

// The breaker refused the attempts between the failures and the success, so
// the service saw fewer calls than the step made attempts.
Console.WriteLine($"price {price} after {prices.Calls} calls to the service");

await actor.Stop();

// A stand-in that fails its first two calls, as an overloaded service does.
sealed class FlakyPriceService : IPriceService
{
    public int Calls;

    public Task<decimal> Fetch(string key, string symbol, CancellationToken cancel)
    {
        var call = Interlocked.Increment(ref Calls);
        if (call <= 2)
        {
            Console.WriteLine($"  call {call}: the service failed");
            throw new InvalidOperationException("the price service is busy");
        }
        Console.WriteLine($"  call {call}: {symbol} costs 12.5");
        return Task.FromResult(12.5m);
    }
}
