// C# workflows compiled by the C# compiler: the WorkflowTask async builder, every kind of step,
// recovery after the actor system stops, the foreign-await error, and host registration.
// Exits with 0 when every check passes.

using System.Collections.Concurrent;
using FCQRS;
using static FCQRS.CSharp;
using FCQRS.Workflows;
using FCQRS.Workflows.CSharp;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

var failures = new List<string>();
var wait = TimeSpan.FromSeconds(30);

void Check(bool condition, string what)
{
    Console.WriteLine($"{(condition ? "ok  " : "FAIL")} {what}");
    if (!condition) failures.Add(what);
}

string NewDb() => Path.Combine(Path.GetTempPath(), $"fcqrs_csharp_workflow_{Guid.NewGuid():N}.db");

void DeleteDb(string db)
{
    SqliteConnection.ClearAllPools();
    foreach (var path in new[] { db, db + "-wal", db + "-shm" })
        if (File.Exists(path)) File.Delete(path);
}

Common.IActor Boot(string db, string name) =>
    ActorApi.Create(new ConfigurationBuilder().Build(), NullLoggerFactory.Instance, $"Data Source={db};", name);

async Task Eventually(Func<bool> condition, string what)
{
    var deadline = DateTime.UtcNow.AddSeconds(30);
    while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(50);
    Check(condition(), what);
}

// --- every kind of step, in one run -----------------------------------------------------
{
    var db = NewDb();
    var actor = Boot(db, "CSharpSteps");
    try
    {
        var counter = ActorWiring.InitAggregate<int, CounterCommand, CounterEvent>(
            actor, 0, "Counter",
            (command, _) => EventActions.Persist<CounterEvent>(new CounterEvent(command.CommandDetails.Amount)),
            (@event, total) => total + @event.EventDetails.Amount);
        ActorWiring.InitSagaStarterEmpty(actor);

        WorkflowHost.Register(actor, new Doubler());
        var orders = WorkflowHost.Register(actor, new OrderWorkflow(counter.Factory));

        Check(await orders.StartAsync("o1", new Order("ABC", 3)), "a C# workflow starts");
        Check(!await orders.StartAsync("o1", new Order("XYZ", 9)), "a second start with the same ID stores nothing");
        await orders.SignalAsync("o1", "approval", "approved by Ada");
        var summary = await orders.ResultAsync("o1", wait);
        Check(summary == new OrderSummary("ABC", 3, "approved by Ada", false, 30, "Exhausted", 10, 3, "cleaned", true),
            $"the C# workflow ran every kind of step: {summary}");
        Check(OrderWorkflow.PriceCalls == 3, "the flaky activity was retried until it succeeded");
    }
    finally
    {
        await actor.Stop();
        DeleteDb(db);
    }
}

// --- recovery ---------------------------------------------------------------------------
{
    var db = NewDb();
    var first = Boot(db, "CSharpRestart");
    var slow = WorkflowHost.Register(first, new SlowWorkflow());
    await slow.StartAsync("s1", 20);
    await Eventually(() => SlowWorkflow.Calls.ContainsKey("ship"), "the second activity is running");
    await first.Stop();

    var second = Boot(db, "CSharpRestart");
    try
    {
        slow = WorkflowHost.Register(second, new SlowWorkflow());
        await Eventually(() => SlowWorkflow.Calls["ship"] == 2, "after a restart the C# workflow resumes by itself");
        SlowWorkflow.Gate.Set();
        Check(await slow.ResultAsync("s1", wait) == 41, "it finishes with the result stored before the restart");
        Check(SlowWorkflow.Calls["reserve"] == 1, "the stored step did not run again");
    }
    finally
    {
        await second.Stop();
        DeleteDb(db);
    }
}

// --- deep recursion ------------------------------------------------------------------------
{
    var db = NewDb();
    var actor = Boot(db, "CSharpRecursion");
    try
    {
        // Each level awaits a stored step and then the next level, so every level stays open
        // until the deepest one returns.
        var countdown = WorkflowHost.Register(actor, new CountdownWorkflow());
        await countdown.StartAsync("r1", CountdownWorkflow.Depth);
        Check(await countdown.ResultAsync("r1", TimeSpan.FromMinutes(2)) == CountdownWorkflow.Depth,
            $"a C# workflow method that calls itself {CountdownWorkflow.Depth} levels deep finishes");
    }
    finally
    {
        await actor.Stop();
        DeleteDb(db);
    }
}

// --- a foreign await, a fault, and cancellation --------------------------------------------
{
    var db = NewDb();
    var actor = Boot(db, "CSharpFaults");
    try
    {
        var careless = WorkflowHost.Register(actor, new CarelessWorkflow());
        await careless.StartAsync("c1", 1);
        try
        {
            await careless.ResultAsync("c1", wait);
            Check(false, "awaiting a Task in a workflow method faults the workflow");
        }
        catch (WorkflowFaultedException fault)
        {
            Check(fault.Error.Message.Contains("not a workflow step"), $"awaiting a Task in a workflow method faults the workflow: {fault.Error.Message}");
        }

        var waiting = WorkflowHost.Register(actor, new WaitingWorkflow());
        await waiting.StartAsync("w1", 1);
        await Eventually(() => WaitingWorkflow.Started, "the waiting workflow is in its activity");
        await waiting.CancelAsync("w1");
        try
        {
            await waiting.ResultAsync("w1", wait);
            Check(false, "a cancelled C# workflow reports cancellation");
        }
        catch (WorkflowCancelledException)
        {
            Check(WaitingWorkflow.Cleaned, "a cancelled C# workflow ran the awaits in its finally block");
        }

        // The same cleanup written as a catch with a filter, an await, and a rethrow.
        var catching = WorkflowHost.Register(actor, new CatchingWorkflow());
        await catching.StartAsync("k1", 1);
        await Eventually(() => CatchingWorkflow.Started, "the catching workflow is in its activity");
        await catching.CancelAsync("k1");
        try
        {
            await catching.ResultAsync("k1", wait);
            Check(false, "a C# workflow that rethrows its cancellation reports cancellation");
        }
        catch (WorkflowCancelledException)
        {
            Check(CatchingWorkflow.Cleaned, "a cancelled C# workflow ran the await in its catch block");
        }
    }
    finally
    {
        await actor.Stop();
        DeleteDb(db);
    }
}

// --- host registration ----------------------------------------------------------------------
{
    var db = NewDb();
    var builder = Host.CreateApplicationBuilder();
    builder.Services.AddFcqrs($"Data Source={db};", "CSharpHost");
    builder.Services.AddSingleton(new Greeting("hello"));
    builder.Services.AddWorkflow<GreetWorkflow, string, string>();
    // A second workflow with the same input and result types.
    builder.Services.AddWorkflow<ShoutWorkflow, string, string>();
    using var host = builder.Build();
    await host.StartAsync();
    try
    {
        var greet = host.Services.GetRequiredService<WorkflowClient<GreetWorkflow, string, string>>().Client;
        await greet.StartAsync("g1", "Ada");
        Check(await greet.ResultAsync("g1", wait) == "hello Ada", "a workflow added to the host gets its constructor's services");
        var shout = host.Services.GetRequiredService<WorkflowClient<ShoutWorkflow, string, string>>().Client;
        await shout.StartAsync("s1", "Ada");
        Check(await shout.ResultAsync("s1", wait) == "ADA", "two workflows with the same input and result types are both registered");
        var latest = host.Services.GetRequiredService<IWorkflowClient<string, string>>();
        Check(latest.Name == "shout", "the plain client interface resolves the workflow added last");
    }
    finally
    {
        await host.StopAsync();
        DeleteDb(db);
    }
}

Console.WriteLine(failures.Count == 0 ? "All C# workflow checks passed." : $"{failures.Count} C# workflow check(s) failed.");
return failures.Count == 0 ? 0 : 1;

public record Order(string Symbol, int Quantity);
public record OrderSummary(string Symbol, int Quantity, string Approval, bool SecondApproval, int Doubled, string Failure, int Sum, int Counted, string Cleanup, bool TimeMoved);
public record CounterCommand(int Amount);
public record CounterEvent(int Amount);
public record Greeting(string Text);

public sealed class CountdownWorkflow() : Workflow<int, int>("countdown")
{
    public const int Depth = 3000;

    public override WorkflowTask<int> Run(WorkflowContext ctx, int depth) => Count(ctx, depth);

    private static async WorkflowTask<int> Count(WorkflowContext ctx, int remaining)
    {
        if (remaining == 0) return 0;
        await ctx.Now();
        return 1 + await Count(ctx, remaining - 1);
    }
}

public sealed class Doubler() : Workflow<int, int>("doubler")
{
    public override async WorkflowTask<int> Run(WorkflowContext ctx, int input)
    {
        await ctx.Sleep(TimeSpan.FromMilliseconds(20));
        return input * 2;
    }
}

public sealed class OrderWorkflow(AggregateFactory counter) : Workflow<Order, OrderSummary>("order")
{
    public static int PriceCalls;

    public override async WorkflowTask<OrderSummary> Run(WorkflowContext ctx, Order order)
    {
        var before = await ctx.Now();
        var retry = Retry.times(5).WithSchedule(RetrySchedules.Fixed(TimeSpan.FromMilliseconds(20)));

        // An activity that fails twice, then succeeds.
        var symbol = await ctx.Activity("price", retry, async attempt =>
        {
            await Task.Delay(5, attempt.Cancellation);
            if (Interlocked.Increment(ref PriceCalls) < 3) throw new TimeoutException("not yet");
            return order.Symbol;
        });

        await ctx.Sleep(TimeSpan.FromMilliseconds(50));
        var approval = await ctx.WaitSignal<string>("approval");
        var second = await ctx.WaitSignal<string>("approval", TimeSpan.FromMilliseconds(100));

        // A child workflow, and a nested workflow method.
        var doubled = await ctx.Child<int, int>("doubler", 15);
        var failure = await Failing(ctx);

        // Two branches together, each a workflow method.
        var parts = await ctx.All(ctx.Run(() => Part(ctx, 1)), ctx.Run(() => Part(ctx, 2)));
        var winner = await ctx.Race(ctx.Run(() => Part(ctx, 3)), ctx.Activity("never", Retry.forever(TimeSpan.FromMinutes(5)),
            async attempt => { await Task.Delay(Timeout.Infinite, attempt.Cancellation); return 0; }));
        // Two steps of different types together.
        var (label, four) = await ctx.Both(
            ctx.Activity("label", Retry.none, _ => Task.FromResult("pair")),
            ctx.Run(() => Part(ctx, 4)));

        var counted = await ctx.Send<CounterCommand, CounterEvent>(counter, "c1", new CounterCommand(order.Quantity));

        var cleanup = "not cleaned";
        try
        {
            await ctx.Version("add-cleanup", 2);
        }
        finally
        {
            // C# allows an await in a finally block, so cleanup can be a stored step.
            cleanup = await ctx.Activity("cleanup", Retry.none, _ => Task.FromResult("cleaned"));
        }

        var after = await ctx.Now();
        return new OrderSummary(symbol, order.Quantity, approval, second.Received, doubled, failure, parts.Sum() + winner + (label == "pair" ? four : 0), counted.Amount, cleanup, after >= before);
    }

    private static async WorkflowTask<int> Part(WorkflowContext ctx, int value)
    {
        var result = await ctx.Activity($"part-{value}", Retry.none, _ => Task.FromResult(value));
        await ctx.Sleep(TimeSpan.FromMilliseconds(10));
        return result;
    }

    private static async WorkflowTask<string> Failing(WorkflowContext ctx)
    {
        try
        {
            await ctx.Activity<int>("down", Retry.times(2).WithSchedule(RetrySchedules.Fixed(TimeSpan.FromMilliseconds(10))),
                _ => throw new InvalidOperationException("down"));
            return "no failure";
        }
        catch (WorkflowStepFailedException failed)
        {
            return failed.Error.Kind.ToString();
        }
    }
}

public sealed class SlowWorkflow() : Workflow<int, int>("slow")
{
    public static readonly ConcurrentDictionary<string, int> Calls = new();
    public static readonly ManualResetEventSlim Gate = new(false);

    public override async WorkflowTask<int> Run(WorkflowContext ctx, int amount)
    {
        var reserved = await ctx.Activity("reserve", Retry.times(3), _ =>
        {
            Calls.AddOrUpdate("reserve", 1, (_, seen) => seen + 1);
            return Task.FromResult(amount * 2);
        });
        return await ctx.Activity("ship", Retry.forever(TimeSpan.FromMinutes(5)), async attempt =>
        {
            Calls.AddOrUpdate("ship", 1, (_, seen) => seen + 1);
            await Task.Run(() => Gate.Wait(attempt.Cancellation));
            return reserved + 1;
        });
    }
}

public sealed class CarelessWorkflow() : Workflow<int, int>("careless")
{
    public override async WorkflowTask<int> Run(WorkflowContext ctx, int input)
    {
        await ctx.Now();
        // Not a workflow step: the workflow could not store or replay it.
        await Task.Delay(200);
        return input;
    }
}

public sealed class CatchingWorkflow() : Workflow<int, int>("catching")
{
    public static volatile bool Started;
    public static volatile bool Cleaned;

    public override async WorkflowTask<int> Run(WorkflowContext ctx, int input)
    {
        try
        {
            return await ctx.Activity("wait", Retry.forever(TimeSpan.FromMinutes(5)), async attempt =>
            {
                Started = true;
                await Task.Delay(Timeout.Infinite, attempt.Cancellation);
                return input;
            });
        }
        catch (WorkflowStepFailedException failed) when (failed.Error.Kind.IsCancelled)
        {
            await ctx.Activity("clean", Retry.none, _ =>
            {
                Cleaned = true;
                return Task.FromResult(1);
            });
            throw;
        }
    }
}

public sealed class WaitingWorkflow() : Workflow<int, int>("waiting")
{
    public static volatile bool Started;
    public static volatile bool Cleaned;

    public override async WorkflowTask<int> Run(WorkflowContext ctx, int input)
    {
        try
        {
            return await ctx.Activity("wait", Retry.forever(TimeSpan.FromMinutes(5)), async attempt =>
            {
                Started = true;
                await Task.Delay(Timeout.Infinite, attempt.Cancellation);
                return input;
            });
        }
        finally
        {
            await ctx.Activity("clean", Retry.none, _ =>
            {
                Cleaned = true;
                return Task.FromResult(1);
            });
        }
    }
}

public sealed class ShoutWorkflow() : Workflow<string, string>("shout")
{
    public override async WorkflowTask<string> Run(WorkflowContext ctx, string name)
    {
        return await ctx.Activity("shout", Retry.none, _ => Task.FromResult(name.ToUpperInvariant()));
    }
}

public sealed class GreetWorkflow(Greeting greeting) : Workflow<string, string>("greet")
{
    public override async WorkflowTask<string> Run(WorkflowContext ctx, string name)
    {
        var text = await ctx.Activity("greet", Retry.none, _ => Task.FromResult($"{greeting.Text} {name}"));
        return text;
    }
}
