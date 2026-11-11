using FCQRS.Workflows.CSharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using static FCQRS.CSharp;

// `--crash` stops the process in the middle of the last step.
var crash = args.Contains("--crash");

var database = Path.Combine(AppContext.BaseDirectory, "quotes.db");
var logging = LoggerFactory.Create(_ => { });
var configuration = new ConfigurationBuilder().Build();
var actor = ActorApi.Create(
    configuration, logging, $"Data Source={database}", "quotes");

var prices = new BusyPriceService();
var mailer = new ConsoleMailer(crash);

// docs:register
// Registering the workflow also resumes the instances that were running when
// the process last stopped.
var quotes = WorkflowHost.Register(actor, new QuoteWorkflow(prices, mailer));
// docs:end

// docs:start
var id = "q-1001";
var request = new QuoteRequest("Ada", "ABC", 100);

if (await quotes.StartAsync(id, request))
{
    Console.WriteLine($"started {id}");
    // The quote is over 1000, so the workflow waits for this signal.
    await quotes.SignalAsync(id, "approval", true);
}
else
{
    var status = await quotes.StatusAsync(id);
    Console.WriteLine($"{id} is already in the journal: {status}");
}

// Waits for the workflow to end. The workflow does not depend on this wait.
var quote = await quotes.ResultAsync(id, TimeSpan.FromSeconds(30));
// docs:end

Console.WriteLine($"finished: {quote.Receipt} (approved: {quote.Approved})");
Console.WriteLine($"price service calls in this run: {prices.Calls}");
await actor.Stop();

// Stand-ins for external systems. The price service fails twice.
public sealed class BusyPriceService : IPriceService
{
    public int Calls;

    public Task<decimal> Fetch(string key, string symbol)
    {
        Calls++;
        Console.WriteLine($"  price service called for {symbol} (key {key})");
        if (Calls < 3) throw new InvalidOperationException("the price service is busy");
        return Task.FromResult(12.5m);
    }
}

public sealed class ConsoleMailer(bool crash) : IMailer
{
    public Task<string> Send(string key, string customer, decimal total)
    {
        Console.WriteLine($"  mailer called (key {key})");
        if (crash)
        {
            Console.WriteLine("  the process stops before this step is stored");
            Environment.Exit(1);
        }
        return Task.FromResult($"quote of {total} sent to {customer}");
    }
}
