---
title: Use Polly inside an activity
category: Apply
categoryindex: 5
index: 10
---

# Use Polly inside an activity

This recipe protects a price service with a circuit breaker and a concurrency limit from
[Polly](https://www.pollydocs.org/), inside a workflow activity. The workflow's retry policy still
decides when to try again and when to give up. Polly decides whether a call is sent at all.

[Write a workflow](write-a-workflow.html) explains activities and retry policies. The complete
programs are in `samples/resilience/fsharp` and `samples/resilience/csharp`. They reference the
projects under `src/`, Polly.Core and Polly.RateLimiting 8.6.5, and need FCQRS.Workflows 7.8.0 or
later, where every activity names a time limit.

## Build one pipeline for the process

A circuit breaker counts failed calls. After enough of them it refuses calls for a while instead of
sending them to a service that is already failing. A concurrency limiter refuses a call when too
many are in flight. Both keep their counts in memory and share them across calls, so the pipeline
is built once and every workflow instance uses it.

<!-- sample: resilience/fsharp Price.fs pipeline -->
```fsharp
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
```

<div class="cs-alt"></div>

<!-- sample: resilience/csharp Price.cs pipeline -->
```csharp
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
```

The pipeline has no retry strategy. Retries stay in the step's policy, because its deadline is
measured from the stored start of the step and survives a restart. Polly's retries run in memory,
and they multiply with the step's: three Polly retries inside six attempts can send 24 calls.

## Let the step retry what Polly refuses

An open breaker throws `BrokenCircuitException`, and a full limiter throws
`RateLimiterRejectedException`. The step's `ShouldRetry` accepts both, so the step waits for its
schedule and tries again, by which time the breaker may have closed. A filter that accepts only
the service's own errors would fail the step at the first refusal.

<!-- sample: resilience/fsharp Price.fs retry -->
```fsharp
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
```

<div class="cs-alt"></div>

<!-- sample: resilience/csharp Price.cs retry -->
```csharp
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
```

`TimeoutException` is what a step sees when an attempt runs past its `AttemptTimeout`.

## Run the pipeline in the activity

The activity passes the attempt's cancellation token to the pipeline, and the pipeline passes its
own token to the service.

<!-- sample: resilience/fsharp Price.fs workflow -->
```fsharp
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
```

<div class="cs-alt"></div>

<!-- sample: resilience/csharp Price.cs workflow -->
```csharp
public override async WorkflowTask<decimal> Run(
    WorkflowContext ctx, string symbol) =>
    await ctx.Activity("fetch", PriceRetry, attempt =>
        // The attempt's token stops the pipeline when the attempt times
        // out, the deadline passes, or the workflow stops.
        PricePipeline.ExecuteAsync(
            cancel => new ValueTask<decimal>(
                prices.Fetch(attempt.IdempotencyKey, symbol, cancel)),
            attempt.Cancellation).AsTask());
```

The stand-in service in the programs fails its first two calls. The second failure opens the
breaker, so the next attempt is refused without a call, and a later attempt succeeds:

```text
  call 1: the service failed
  call 2: the service failed
  call 3: ABC costs 12.5
price 12.5 after 3 calls to the service
```

## What each part guarantees

- `AttemptTimeout` bounds the whole pipeline call. When it runs out, the attempt fails with
  `TimeoutException` even if the pipeline or the service ignores the token. The call keeps running
  in the background, and its result is ignored.
- The step stores only the result of a successful attempt. The breaker's state, the limiter's
  count, and the attempts are not stored: a restart starts them again.
- The pipeline is shared within one process. FCQRS runs on one node, so it is shared by every
  workflow instance that can call the service.
- A call can reach the service more than once: a retry repeats it, and so does a restart before the
  result is stored. Pass the idempotency key to the service, as for any activity.

When the step's policy runs out, for example because the breaker stayed open past the last attempt,
the body receives `WorkflowStepFailedException`. The body can catch it and choose another path,
such as a cached price or a person's decision.

[Run scheduled jobs](run-scheduled-jobs.html) can start many runs at once after downtime. A limiter
like this one keeps them from calling a service at the same moment.

Next: [Test your domain](test-your-domain.html) checks decisions, replay, and retries.
