module Price

open System
open System.Threading
open System.Threading.Tasks
open Polly
open Polly.CircuitBreaker
open Polly.RateLimiting
open FCQRS.Common
open FCQRS.Workflows

/// The price service. A real one is an HTTP client.
type IPriceService =
    abstract Fetch: key: string * symbol: string * cancel: CancellationToken -> Task<decimal>

// docs:pipeline
// One pipeline for the process, shared by every workflow instance. Its state
// is in memory: a restart closes the breaker and empties the limiter.
let pricePipeline: ResiliencePipeline =
    ResiliencePipelineBuilder()
        // At most four calls at once; a fifth is refused at once.
        .AddConcurrencyLimiter(4, 0)
        // After half of at least two calls in ten seconds fail, refuse
        // calls for one second instead of sending them.
        .AddCircuitBreaker(
            CircuitBreakerStrategyOptions(
                FailureRatio = 0.5,
                MinimumThroughput = 2,
                SamplingDuration = TimeSpan.FromSeconds 10.0,
                BreakDuration = TimeSpan.FromSeconds 1.0))
        .Build()
// docs:end

// docs:retry
// The step retries, not Polly: six attempts, half a second apart, each
// allowed five seconds for the whole pipeline. An open breaker and a full
// limiter are worth another attempt; so is a service error.
let priceRetry = {
    Retry.times 6 (TimeSpan.FromSeconds 5.0) with
        Schedule = FixedInterval(TimeSpan.FromMilliseconds 500.0)
        ShouldRetry = fun error ->
            error :? BrokenCircuitException
            || error :? RateLimiterRejectedException
            || error :? TimeoutException
            || error :? InvalidOperationException
}
// docs:end

// docs:workflow
let definition (prices: IPriceService) =
    Workflow.define "price" (fun (symbol: string) -> workflow {
        return!
            activity "fetch" priceRetry (fun attempt ->
                // The attempt's token stops the pipeline when the attempt
                // times out, the deadline passes, or the workflow stops.
                let call (cancel: CancellationToken) =
                    ValueTask<decimal>(
                        prices.Fetch(attempt.IdempotencyKey, symbol, cancel))
                pricePipeline
                    .ExecuteAsync(call, attempt.Cancellation)
                    .AsTask())
    })
// docs:end
