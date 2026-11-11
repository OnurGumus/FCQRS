---
title: Write a workflow
category: Apply
categoryindex: 4
index: 8
---

# Write a workflow

This recipe builds a quote workflow. It fetches a price from an external service, waits for a
person to approve a large quote, and sends the quote. The process can stop at any point; the next
start continues the quote where it was.

Read [Workflows](../concepts/workflows.html) first for the model: steps, the journal, replay, and
what stays the application's responsibility.

> **Motivation:** Use a workflow here because a caller starts the process with input data, the work
> is a sequence of external calls, and it must finish even if the process restarts in the middle.

The complete programs are in `samples/workflows/fsharp` and `samples/workflows/csharp`.
`FCQRS.Workflows` is a separate package; the samples reference its project.

## Define the input and the result

Both are stored in the workflow's journal, so their shape is a persistent contract:

<!-- sample: workflows/fsharp Quote.fs messages -->
```fsharp
// What the workflow is started with.
type QuoteRequest =
    { Customer: string
      Symbol: string
      Quantity: int }

// What the workflow returns.
type Quote =
    { Total: decimal
      Approved: bool
      Receipt: string }
```

<div class="cs-alt"></div>

<!-- sample: workflows/csharp Quote.cs messages -->
```csharp
// What the workflow is started with.
public sealed record QuoteRequest(string Customer, string Symbol, int Quantity);

// What the workflow returns.
public sealed record Quote(decimal Total, bool Approved, string Receipt);
```

## Give external calls an idempotency key

An activity runs at least once. A crash after the call and before its result is stored repeats the
call, so each external system takes a key that is the same for every attempt of one step:

<!-- sample: workflows/fsharp Quote.fs services -->
```fsharp
// The external systems the workflow calls. Each call gets a key that is the
// same for every attempt of one step, so a system that remembers keys can
// recognise a repeated call.
type IPriceService =
    abstract Fetch: key: string * symbol: string -> Task<decimal>

type IMailer =
    abstract Send:
        key: string * customer: string * total: decimal -> Task<string>
```

<div class="cs-alt"></div>

<!-- sample: workflows/csharp Quote.cs services -->
```csharp
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
```

## Write the steps in order

<!-- sample: workflows/fsharp Quote.fs workflow -->
```fsharp
// Five attempts, 200 ms apart.
let private retry =
    { Retry.times 5 with
        Schedule = FixedInterval(TimeSpan.FromMilliseconds 200.0) }

let definition (prices: IPriceService) (mailer: IMailer) =
    Workflow.define "quote" (fun (request: QuoteRequest) -> workflow {
        // The price is stored once it arrives. A restart after this step
        // does not call the price service again.
        let! price =
            activity "price" retry (fun attempt ->
                prices.Fetch(attempt.IdempotencyKey, request.Symbol))
        let total = price * decimal request.Quantity

        // A quote over 1000 waits for a person, for at most one day.
        let! approved = workflow {
            if total <= 1000m then
                return true
            else
                let! answer =
                    waitSignalFor<bool> "approval" (TimeSpan.FromDays 1.0)
                return defaultArg answer false
        }

        if not approved then
            return { Total = total; Approved = false; Receipt = "" }
        else
            let! receipt =
                activity "send-quote" retry (fun attempt ->
                    mailer.Send(
                        attempt.IdempotencyKey, request.Customer, total))
            return { Total = total; Approved = true; Receipt = receipt }
    })
```

<div class="cs-alt"></div>

<!-- sample: workflows/csharp Quote.cs workflow -->
```csharp
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
```

The workflow has three steps: `price`, the wait for `approval`, and `send-quote`. Their names are
stored. Renaming a step that a running instance has passed parks that instance; see
[Change a workflow while instances are running](../concepts/workflows.html#Change-a-workflow-while-instances-are-running).

The code between steps must give the same result every time. Computing `total` from the stored
price is fine. Reading the clock or calling a service there is not: do that in a step.

## Register the workflow

<!-- sample: workflows/fsharp Program.fs register -->
```fsharp
// Registering the workflow also resumes the instances that were running when
// the process last stopped.
let quotes = Workflows.register api (definition prices mailer)
```

<div class="cs-alt"></div>

<!-- sample: workflows/csharp Program.cs register -->
```csharp
// Registering the workflow also resumes the instances that were running when
// the process last stopped.
var quotes = WorkflowHost.Register(actor, new QuoteWorkflow(prices, mailer));
```

Register every workflow when the process starts, before it serves requests. An instance that was
running when the process stopped resumes only once its workflow is registered. In a C# host,
`services.AddWorkflow<QuoteWorkflow, QuoteRequest, Quote>()` after `AddFcqrs` does this at startup
and provides `IWorkflowClient<QuoteRequest, Quote>`.

## Start an instance, signal it, and read its result

<!-- sample: workflows/fsharp Program.fs start -->
```fsharp
let id = "q-1001"
let request = { Customer = "Ada"; Symbol = "ABC"; Quantity = 100 }

match quotes.Start id request |> Async.RunSynchronously with
| StartResult.Started ->
    printfn "started %s" id
    // The quote is over 1000, so the workflow waits for this signal.
    Workflows.signal quotes id "approval" true |> Async.RunSynchronously
| StartResult.AlreadyStarted status ->
    printfn "%s is already in the journal: %A" id status

// Waits for the workflow to end. The workflow does not depend on this wait.
let outcome =
    quotes.Result id (TimeSpan.FromSeconds 30.0) |> Async.RunSynchronously
```

<div class="cs-alt"></div>

<!-- sample: workflows/csharp Program.cs start -->
```csharp
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
```

- **The ID is yours to choose.** A second start with the same ID stores nothing and reports the
  existing instance. Compute the ID from the request, such as an order number, so a caller that
  retries does not start a second workflow.
- **Start returns once the start is stored.** From then on the instance runs whether or not anyone
  waits for it.
- **The signal is stored before the call returns.** It can be sent before the workflow reaches its
  wait.
- **Waiting for the result is optional.** The wait is in memory. If the caller gives up, the
  workflow carries on, and a later call reads the stored result.

## Stop the process in the middle

Run the sample with `--crash`. The mailer stops the process inside the last step:

```text
started q-1001
  price service called for ABC (key quote/q-1001/0)
  price service called for ABC (key quote/q-1001/0)
  price service called for ABC (key quote/q-1001/0)
  mailer called (key quote/q-1001/2)
  the process stops before this step is stored
```

The price service failed twice and answered on the third attempt. All three attempts carried the
same key.

Run it again without the flag:

```text
  mailer called (key quote/q-1001/2)
q-1001 is already in the journal: Running ["2 send-quote"]
finished: quote of 1250.0 sent to Ada (approved: true)
price service calls in this run: 0
```

- The instance resumed when its workflow was registered, before the program asked for it.
- The price and the approval were in the journal, so neither step ran again.
- `send-quote` had no stored result, so it ran again, with the same key as before. A mailer that
  remembers keys would send one message.

## What this recipe leaves to you

- The stand-in services ignore the key. A real one must use it.
- A quote whose approval never arrives waits one day and ends unapproved. Choose that time, and the
  outcome, for your business.
- If `send-quote` fails five times, the workflow faults and stays faulted. Catch
  `WorkflowStepFailedException` in the body where a failure should lead somewhere else.

Next: [Observe your system](observability.html#Workflows) lists the spans and metrics a workflow
emits.
