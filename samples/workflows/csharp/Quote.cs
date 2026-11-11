using FCQRS.Workflows;
using FCQRS.Workflows.CSharp;
using static FCQRS.CSharp;

// docs:messages
// What the workflow is started with.
public sealed record QuoteRequest(string Customer, string Symbol, int Quantity);

// What the workflow returns.
public sealed record Quote(decimal Total, bool Approved, string Receipt);
// docs:end

// docs:services
// The external systems the workflow calls. Each call gets a key that is the
// same for every attempt of one step, so a system that remembers keys can
// recognise a repeated call.
public interface IPriceService
{
    Task<decimal> Fetch(string key, string symbol);
}

public interface IMailer
{
    Task<string> Send(string key, string customer, decimal total);
}
// docs:end

// docs:workflow
public sealed class QuoteWorkflow(IPriceService prices, IMailer mailer)
    : Workflow<QuoteRequest, Quote>("quote")
{
    // Five attempts, 200 ms apart.
    private static readonly RetryPolicy Policy = Retry.times(5)
        .WithSchedule(RetrySchedules.Fixed(TimeSpan.FromMilliseconds(200)));

    public override async WorkflowTask<Quote> Run(
        WorkflowContext ctx, QuoteRequest request)
    {
        // The price is stored once it arrives. A restart after this step
        // does not call the price service again.
        var price = await ctx.Activity("price", Policy, attempt =>
            prices.Fetch(attempt.IdempotencyKey, request.Symbol));
        var total = price * request.Quantity;

        // A quote over 1000 waits for a person, for at most one day.
        var approved = true;
        if (total > 1000m)
        {
            var answer = await ctx.WaitSignal<bool>(
                "approval", TimeSpan.FromDays(1));
            approved = answer.Received && answer.Value;
        }

        if (!approved)
            return new Quote(total, false, "");

        var receipt = await ctx.Activity("send-quote", Policy, attempt =>
            mailer.Send(attempt.IdempotencyKey, request.Customer, total));
        return new Quote(total, true, receipt);
    }
}
// docs:end
