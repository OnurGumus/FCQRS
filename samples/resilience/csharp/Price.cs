using FCQRS.Workflows;
using FCQRS.Workflows.CSharp;
using Polly;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using static FCQRS.CSharp;

/// The price service. A real one is an HTTP client.
public interface IPriceService
{
    Task<decimal> Fetch(string key, string symbol, CancellationToken cancel);
}

public sealed class PriceWorkflow(IPriceService prices)
    : Workflow<string, decimal>("price")
{
    // docs:pipeline
    // One pipeline for the process, shared by every workflow instance. Its
    // state is in memory: a restart closes the breaker and empties the limiter.
    private static readonly ResiliencePipeline PricePipeline =
        new ResiliencePipelineBuilder()
            // At most four calls at once; a fifth is refused at once.
            .AddConcurrencyLimiter(4, 0)
            // After half of at least two calls in ten seconds fail, refuse
            // calls for one second instead of sending them.
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = 2,
                SamplingDuration = TimeSpan.FromSeconds(10),
                BreakDuration = TimeSpan.FromSeconds(1),
            })
            .Build();
    // docs:end

    // docs:retry
    // The step retries, not Polly: six attempts, half a second apart, each
    // allowed five seconds for the whole pipeline. An open breaker and a full
    // limiter are worth another attempt; so is a service error.
    private static readonly RetryPolicy PriceRetry =
        Retry.times(6, TimeSpan.FromSeconds(5))
            .WithSchedule(RetrySchedules.Fixed(TimeSpan.FromMilliseconds(500)))
            .RetryWhen(error =>
                error is BrokenCircuitException
                    or RateLimiterRejectedException
                    or TimeoutException
                    or InvalidOperationException);
    // docs:end

    // docs:workflow
    public override async WorkflowTask<decimal> Run(
        WorkflowContext ctx, string symbol) =>
        await ctx.Activity("fetch", PriceRetry, attempt =>
            // The attempt's token stops the pipeline when the attempt times
            // out, the deadline passes, or the workflow stops.
            PricePipeline.ExecuteAsync(
                cancel => new ValueTask<decimal>(
                    prices.Fetch(attempt.IdempotencyKey, symbol, cancel)),
                attempt.Cancellation).AsTask());
    // docs:end
}
