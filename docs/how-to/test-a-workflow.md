---
title: Test a workflow
category: Apply
categoryindex: 5
index: 9
---

# Test a workflow

This guide tests the quote workflow from [Write a workflow](write-a-workflow.html). It fetches a
price, waits up to a day for an `approval` signal when the quote is over 1000, and sends the quote.
`WorkflowTest` runs it in memory: it stores the records the workflow actor would store, with no
actor system and no database, so a test takes milliseconds.

The tests are in `samples/workflows/fsharp-tests` (Expecto) and `samples/workflows/csharp-tests`
(xUnit). They compile the quote workflow from `samples/workflows` unchanged, reference the projects
under `src/`, and need FCQRS.Workflows 7.11.0 or later.

## Give each step's outcome

No activity runs in a test. The test gives each step's outcome, so the services the workflow takes
are stand-ins that fail the test if a step calls them:

<!-- sample: workflows/fsharp-tests QuoteTests.fs setup -->
```fsharp
// No activity runs in a test: the test gives each step's outcome. These
// stand-ins fail the test if one runs anyway.
let private prices = {
    new IPriceService with
        member _.Fetch(_, _) = failwith "a test runs no activity"
}

let private mailer = {
    new IMailer with
        member _.Send(_, _, _) = failwith "a test runs no activity"
}

let private quote = definition prices mailer
let private large = { Customer = "Ada"; Symbol = "ABC"; Quantity = 100 }
```

<div class="cs-alt"></div>

<!-- sample: workflows/csharp-tests QuoteTests.cs setup -->
```csharp
// No activity runs in a test: the test gives each step's outcome. These
// stand-ins fail the test if one runs anyway.
private sealed class NoPrices : IPriceService
{
    public Task<decimal> Fetch(string key, string symbol) =>
        throw new InvalidOperationException("a test runs no activity");
}

private sealed class NoMailer : IMailer
{
    public Task<string> Send(string key, string customer, decimal total) =>
        throw new InvalidOperationException("a test runs no activity");
}

private static readonly QuoteWorkflow Quote =
    new(new NoPrices(), new NoMailer());
private static readonly QuoteRequest Large = new("Ada", "ABC", 100);
```

Test the activities' own code separately, as ordinary functions. These tests check the order of the
steps and what the body does with each outcome.

## Follow one path through the workflow

<!-- sample: workflows/fsharp-tests QuoteTests.fs approved -->
```fsharp
let approved =
    test "a quote over 1000 is sent once a person approves it" {
        let flow = WorkflowTest.Start(quote, large)
        flow.Complete("price", 12.5m)
        let waits = flow.Status.IsWaitingFor "approval"
        Expect.isTrue waits "1250 needs approval"
        flow.Signal("approval", true)
        flow.Complete("send-quote", "receipt-1")
        let expected = { Total = 1250m; Approved = true; Receipt = "receipt-1" }
        Expect.equal flow.Result expected "the quote is sent"
        // A restart after any stored record must store the same journal.
        flow.CheckReplay()
    }
```

<div class="cs-alt"></div>

<!-- sample: workflows/csharp-tests QuoteTests.cs approved -->
```csharp
[Fact]
public void A_quote_over_1000_is_sent_once_a_person_approves_it()
{
    var flow = WorkflowTest.Start(Quote, Large);
    flow.Complete("price", 12.5m);
    Assert.True(flow.Status.IsWaitingFor("approval"), "1250 needs approval");
    flow.Signal("approval", true);
    flow.Complete("send-quote", "receipt-1");
    Assert.Equal(new Quote(1250m, true, "receipt-1"), flow.Result);
    // A restart after any stored record must store the same journal.
    flow.CheckReplay();
}
```

`WorkflowTest.Start` stores the input and runs the body until it waits. Each call that follows
stores one record, as the actor would, and runs the body to its next wait:

| Call | Stores |
|---|---|
| `Complete(step, value)` | the result of the waiting activity, command, or child step with that name |
| `Fail(step, error)` | the step's failure, as `Exhausted`; another overload takes the `ErrorKind` |
| `Signal(name, value)` | a signal, which a wait for it takes now or when it starts |
| `Advance(time)` | nothing itself; it moves the clock and fires the timers and timeouts that are due |
| `Cancel()` | a cancel request and the failure of every waiting step |

`Waiting` lists the steps the body waits on, `Status` reports what a registered workflow's `Status`
would, and `Result` gives the body's result once it has ended. `Complete` raises
`InvalidOperationException` when no step with that name waits, so a test that gives an outcome too
early fails at that line.

Every value goes through storage. The input, each result, and each signal are stored and read back,
as recovery reads them. A result that replay cannot read fails its step as `NotRetryable`, as it
would in production.

## Move the time

The test's clock starts at `WorkflowTest.DefaultStart` (1 January 2026, 00:00 UTC) or at the time
given to `Start`, and moves only with `Advance`:

<!-- sample: workflows/fsharp-tests QuoteTests.fs declined -->
```fsharp
let declined =
    test "a quote nobody approves within a day is declined" {
        let flow = WorkflowTest.Start(quote, large)
        flow.Complete("price", 12.5m)
        flow.Advance(TimeSpan.FromDays 1.0)
        let expected = { Total = 1250m; Approved = false; Receipt = "" }
        Expect.equal flow.Result expected "the wait timed out"
        flow.CheckReplay()
    }
```

<div class="cs-alt"></div>

<!-- sample: workflows/csharp-tests QuoteTests.cs declined -->
```csharp
[Fact]
public void A_quote_nobody_approves_within_a_day_is_declined()
{
    var flow = WorkflowTest.Start(Quote, Large);
    flow.Complete("price", 12.5m);
    flow.Advance(TimeSpan.FromDays(1));
    Assert.Equal(new Quote(1250m, false, ""), flow.Result);
    flow.CheckReplay();
}
```

A timer and a signal wait's timeout store their due time when they start, and `Advance` fires each
one whose due time has passed. A step that reads the time, such as `now`, reads the test's clock.

## Fail a step

<!-- sample: workflows/fsharp-tests QuoteTests.fs failed -->
```fsharp
let mailerDown =
    test "a mailer that stays down faults the quote" {
        let flow = WorkflowTest.Start(quote, { large with Quantity = 10 })
        flow.Complete("price", 12.5m)
        // As if the retry policy's five attempts had failed.
        flow.Fail("send-quote", TimeoutException "the mailer is down")
        match flow.Status with
        | WorkflowStatus.Failed error ->
            Expect.equal error.Kind ErrorKind.Exhausted "the attempts ran out"
        | other -> failtestf "expected Failed, found %A" other
    }
```

<div class="cs-alt"></div>

<!-- sample: workflows/csharp-tests QuoteTests.cs failed -->
```csharp
[Fact]
public void A_mailer_that_stays_down_faults_the_quote()
{
    var flow = WorkflowTest.Start(Quote, Large with { Quantity = 10 });
    flow.Complete("price", 12.5m);
    // As if the retry policy's five attempts had failed.
    flow.Fail("send-quote", new TimeoutException("the mailer is down"));
    var failed = Assert.IsType<WorkflowStatus.Failed>(flow.Status);
    Assert.Equal(ErrorKind.Exhausted, failed.Item.Kind);
}
```

The outcome a test gives is the step's final one. Retry policies are not applied: `Fail` stands for
the last attempt failing. A command step failed as `Exhausted` or `DeadlineExceeded` is stored as
`OutcomeUnknown`, as the actor stores it.

## Check that a restart replays

`CheckReplay` restarts the instance after every record the test stored, gives it the same outcomes,
and checks that each restart stores the same journal. It raises `WorkflowReplayException` for the
first restart that differs. That finds a body whose steps, stored values, or result change on
replay, for example because it chooses a step from the clock, a random number, or state outside the
workflow. FCQRS cannot detect such a body while it runs; in production it shows up as a parked
instance.

A passing check does not show that a body is deterministic. The check sees only what the journal
stores, and an activity's arguments are not stored. A body that passes `Guid.NewGuid()` or
`DateTime.UtcNow` to an activity sends a different value after each restart, and the check passes.
Read time, GUIDs, and random numbers through `now`, `newGuid`, and `random`, and look for them
outside steps when you review a body.

`Restart()` does one restart, as the process does after it stops: steps with a stored outcome are
not run again, and a step that was waiting waits again.

## Pin the stored steps

Step names, their order, and their kinds are persistent contracts: an instance that is running when
new code is deployed replays its journal against that code. A test of `Summary`, one line per
stored record, shows a change to them:

<!-- sample: workflows/fsharp-tests QuoteTests.fs journal -->
```fsharp
let journal =
    test "a quote stores the step names that running quotes depend on" {
        let flow = WorkflowTest.Start(quote, large)
        flow.Complete("price", 12.5m)
        flow.Signal("approval", true)
        flow.Complete("send-quote", "receipt-1")
        let expected = [
            "started"
            "scheduled 0 price"; "completed 0 price"
            "scheduled 1 approval"; "signal approval"; "completed 1 approval"
            "scheduled 2 send-quote"; "completed 2 send-quote"
            "finished"
        ]
        Expect.equal (List.ofSeq flow.Summary) expected "the stored steps"
    }
```

<div class="cs-alt"></div>

<!-- sample: workflows/csharp-tests QuoteTests.cs journal -->
```csharp
[Fact]
public void A_quote_stores_the_step_names_that_running_quotes_depend_on()
{
    var flow = WorkflowTest.Start(Quote, Large);
    flow.Complete("price", 12.5m);
    flow.Signal("approval", true);
    flow.Complete("send-quote", "receipt-1");
    string[] expected =
    [
        "started",
        "scheduled 0 price", "completed 0 price",
        "scheduled 1 approval", "signal approval", "completed 1 approval",
        "scheduled 2 send-quote", "completed 2 send-quote",
        "finished",
    ];
    Assert.Equal(expected, flow.Summary);
}
```

The path before each name is the step's position in the body. `Journal` holds the records
themselves, with each stored value. `WorkflowTest.Resume(definition, journal)` runs code over a
journal another test stored, as a deploy does, and its `Status` reports `Parked` when the journal no
longer matches; see [Change a workflow while instances are
running](../concepts/workflows.html#Change-a-workflow-while-instances-are-running).

## Run the tests

```bash
dotnet run --project samples/workflows/fsharp-tests -c Release
dotnet test samples/workflows/csharp-tests -c Release
```

All four tests pass in each language.

## What these tests leave to you

- The activities' code, and the idempotency key each external system must honour.
- Retry timing and deadlines, which run only on an actor system.
- An aggregate's answer to a command, which a test gives as the step's result. Test the aggregate
  as [Test your domain](test-your-domain.html) shows.
- A real restart, with the journal in a database: run the application, stop it in the middle, and
  start it again, as [Write a workflow](write-a-workflow.html#Stop-the-process-in-the-middle) does.

Next: [Find workflows that wait for a signal](find-waiting-workflows.html).
