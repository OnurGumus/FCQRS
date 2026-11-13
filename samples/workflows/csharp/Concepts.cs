// The C# forms of the examples on the workflow concept page,
// docs/concepts/workflows.md. The page quotes the marked regions.
// The program does not run these: the build checks that they compile.

using Akka;
using Akka.Streams.Dsl;
using FCQRS.Workflows;
using FCQRS.Workflows.CSharp;
using static FCQRS.CSharp;
using static Concepts.External;
// Akka.Streams has a Retry of its own.
using Retry = FCQRS.Workflows.Retry;

namespace Concepts;

public static class External
{
    public static Task<int> Score(string key, string order) =>
        Task.FromResult(42);

    public static Task<string> Charge(string key, decimal amount) =>
        Task.FromResult("paid");

    public static Task<bool> IsShipped(string key, string order) =>
        Task.FromResult(true);
}

public abstract record AccountCommand;
public sealed record Withdraw(decimal Amount) : AccountCommand;
public abstract record AccountEvent;
public sealed record Withdrawn(decimal Amount) : AccountEvent;
public sealed record Rejected(string Reason) : AccountEvent;

public sealed record Summary(string Detached, int Points, DateTime At);

// docs:enrich
public sealed record Enriched(int Points, DateTime At);

public sealed class Enrich() : Workflow<string, Enriched>("enrich")
{
    public override async WorkflowTask<Enriched> Run(
        WorkflowContext ctx, string order)
    {
        var points = await ctx.Activity("score", Retry.times(3), attempt =>
            Score(attempt.IdempotencyKey, order));
        await ctx.Sleep(TimeSpan.FromMinutes(5));
        var at = await ctx.Now();
        return new Enriched(points, at);
    }
}
// docs:end

// docs:parent
public sealed class Parent() : Workflow<string, Summary>("parent")
{
    public override async WorkflowTask<Summary> Run(
        WorkflowContext ctx, string order)
    {
        var detached = await ctx.StartChild<string, Enriched>("enrich", order);
        var again = await ctx.Child<string, Enriched>(
            "enrich", order + "-again");
        return new Summary(detached.Id, again.Points, again.At);
    }
}
// docs:end

public static class Steps
{
    // docs:approve
    static async WorkflowTask<bool> Approve(WorkflowContext ctx)
    {
        var answer = await ctx.WaitSignal<bool>(
            "approval", TimeSpan.FromDays(2));
        return answer.Received && answer.Value;
    }
    // docs:end

    // docs:both
    static async WorkflowTask<(IReadOnlyList<int>, string?)> Both(
        WorkflowContext ctx)
    {
        var orders = new[] { "A", "B", "C" };
        var points = await ctx.All(orders.Select(order =>
            ctx.Activity("score", Retry.times(3), attempt =>
                Score(attempt.IdempotencyKey, order))).ToArray());
        var first = await ctx.Race(
            ctx.Run(() => Charged(ctx)),
            ctx.Run(() => TimedOut(ctx)));
        return (points, first);
    }

    static async WorkflowTask<string?> Charged(WorkflowContext ctx) =>
        await ctx.Activity("charge", Retry.none, attempt =>
            Charge(attempt.IdempotencyKey, 30m));

    static async WorkflowTask<string?> TimedOut(WorkflowContext ctx)
    {
        await ctx.Sleep(TimeSpan.FromMinutes(1));
        return null;
    }
    // docs:end

    // docs:pair
    static async WorkflowTask<(int, string)> PointsAndReceipt(
        WorkflowContext ctx)
    {
        var (points, receipt) = await ctx.Both(
            ctx.Activity("score", Retry.times(3), attempt =>
                Score(attempt.IdempotencyKey, "A")),
            ctx.Activity("charge", Retry.none, attempt =>
                Charge(attempt.IdempotencyKey, 30m)));
        return (points, receipt);
    }
    // docs:end

    // docs:repeat
    static async WorkflowTask<int> WaitUntilShipped(
        WorkflowContext ctx, int checks)
    {
        var shipped = await ctx.Activity("check", Retry.times(3), attempt =>
            IsShipped(attempt.IdempotencyKey, "A"));
        if (shipped)
            return checks;
        await ctx.Sleep(TimeSpan.FromMinutes(5));
        return await WaitUntilShipped(ctx, checks + 1);
    }
    // docs:end

    // docs:pay
    static async WorkflowTask<string> Pay(
        WorkflowContext ctx, AggregateFactory accounts)
    {
        var reply = await ctx.Send<AccountCommand, AccountEvent>(
            accounts, "alice", new Withdraw(30m));
        return reply is Withdrawn taken
            ? $"took {taken.Amount}"
            : $"refused: {reply}";
    }
    // docs:end

    // docs:booking
    static async WorkflowTask<string> Booking(WorkflowContext ctx)
    {
        try
        {
            return await ctx.Activity("charge",
                Retry.forever(TimeSpan.FromHours(1)), attempt =>
                    Charge(attempt.IdempotencyKey, 30m));
        }
        catch (WorkflowStepFailedException failed)
            when (failed.Error.Kind.IsCancelled)
        {
            await ctx.Activity("refund", Retry.none, attempt =>
                Charge(attempt.IdempotencyKey, -30m));
            throw;
        }
    }
    // docs:end

    // docs:evolving
    static async WorkflowTask<int> Evolving(WorkflowContext ctx)
    {
        var points = await ctx.Activity("score", Retry.times(3), attempt =>
            Score(attempt.IdempotencyKey, "A"));
        var change = await ctx.Version("add-charge", 2);
        if (change >= 2)
            await ctx.Activity("charge", Retry.none, attempt =>
                Charge(attempt.IdempotencyKey, 5m));
        return points;
    }
    // docs:end

    // docs:stream
    static Flow<string, (string Id, Enriched Result), NotUsed> Outcomes(
        IWorkflowClient<string, Enriched> client) =>
        Flow.Create<string>().SelectAsync(8, async order =>
        {
            var id = $"enrich-{order}";
            await client.StartAsync(id, order);
            var result = await client.ResultAsync(
                id, TimeSpan.FromMinutes(5));
            return (id, result);
        });
    // docs:end
}
