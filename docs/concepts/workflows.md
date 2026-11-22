---
title: Workflows
category: Understand
categoryindex: 4
index: 11
---

# Workflows: durable code that resumes where it stopped

A workflow takes input, calls external systems, waits, and returns a result. FCQRS stores the
result of each step. After a crash, a workflow instance continues at the step it had reached, and
steps whose results are stored do not run again.

Workflows are in the `FCQRS.Workflows` package. They sit beside [sagas](sagas.html) and do not
replace them. The [workflow tutorial](../workflow-tutorial/place-an-order.html) builds one step by
step; this page explains the model behind it with activities and timers.
[Workflow steps](workflow-steps.html) covers signals, parallel steps, loops, child workflows, and
commands to aggregates, and [Schedules](schedules.html) starts workflows at due times.

## Start with a workflow that calls an external system

`enrich` scores an order with an external service, waits five minutes, and reads the time:

```fsharp
let enrich =
    Workflow.define "enrich" (fun (order: string) -> workflow {
        let! points =
            activity "score" (Retry.times 3 (TimeSpan.FromSeconds 30.0)) (fun attempt ->
                score attempt.IdempotencyKey order)
        do! sleep (TimeSpan.FromMinutes 5.0)
        let! at = now
        return points, at
    })
```

<div class="cs-alt"></div>

<!-- sample: workflows/csharp Concepts.cs enrich -->
```csharp
public sealed record Enriched(int Points, DateTime At);

public sealed class Enrich() : Workflow<string, Enriched>("enrich")
{
    public override async WorkflowTask<Enriched> Run(
        WorkflowContext ctx, string order)
    {
        var points = await ctx.Activity(
            "score", Retry.times(3, TimeSpan.FromSeconds(30)),
            attempt => Score(attempt.IdempotencyKey, order));
        await ctx.Sleep(TimeSpan.FromMinutes(5));
        var at = await ctx.Now();
        return new Enriched(points, at);
    }
}
```

Each `let!` and `do!` in F#, and each `await` in C#, is a **step**. Registering the workflow
returns the object that starts instances: in F#, `Workflows.register api enrich` returns a handle
and `handle.Start "order-17" "ABC"` starts the instance with the ID `order-17`. In C#,
`WorkflowHost.Register(actor, new Enrich())` returns a client with
`client.StartAsync("order-17", "ABC")`. No aggregate has to store an event first.

`handle.Result "order-17" timeout` waits for the instance to end and returns a `WorkflowOutcome`:
`Finished` with the body's value, `Faulted` with the stored failure, or `Cancelled`. The C# client
has two ways to wait. `client.ResultAsync("order-17", timeout)` returns the body's value and throws
`WorkflowFaultedException` or `WorkflowCancelledException` when the workflow faulted or was
cancelled. `client.OutcomeAsync("order-17", timeout)` returns the `WorkflowOutcome` instead and
throws neither. All three throw `TimeoutException` when the timeout passes first, and
`InvalidOperationException` when no workflow with that ID was started.

Most of the later examples are parts of a workflow. In F# each is a `workflow { }` value, and in C# a method
that returns `WorkflowTask<T>`. The prose names the F# steps; the
[table at the end](#C-equivalents) gives each one's C# name.

## The body is a description

`workflow { }` does not run code when it is evaluated. It builds a value that says "this step, then
the rest". A workflow instance is a persistent actor that interprets that value:

1. It reaches a step and stores that the step began.
2. It runs the step. An activity runs outside the actor, so a slow call blocks nothing.
3. It stores the step's result and gives the result to the rest of the body.

The stored records are the instance's journal. When the body returns, its result is stored too.

## Recovery replays the journal

A registered workflow's running instances restart with the process. Each one runs its body again
from the top. At every step it looks in its journal:

| The journal holds | The instance |
|---|---|
| the step's result | gives the stored result to the body and runs nothing |
| the step's failure | raises the same exception in the body |
| only the step's start | runs the step again |
| nothing | runs the step for the first time |

A stored step result is never computed again. A step that was running when the process stopped has
no stored result, so it runs again.

What happens to an instance at the next start depends on where it was:

- **Running:** it resumes when its workflow is registered, without a request. Register every
  workflow when the process starts, before it serves requests. A timer or a signal wait keeps the
  due time it stored, so the time the process was stopped counts toward it.
- **Finished, faulted, or cancelled:** it has ended, and nothing restarts it by itself. A start with
  its ID stores nothing and reports its status.
- **Parked:** it runs nothing until code that matches its journal is deployed. See [Change a workflow
  while instances are running](#Change-a-workflow-while-instances-are-running).

### Resume a faulted workflow

A faulted instance can continue from the step whose failure faulted it: in F#,
`handle.Resume "order-17"`, and in C#, `client.ResumeAsync("order-17")`. Fix the cause first, because
a step that fails the same way faults the instance again.

- A `Resumed` record sets the step's failure aside. The body replays its stored steps, so the steps
  that completed before the failure do not run again, and it runs the failed step again and goes on.
- The step keeps its idempotency key: it is the same operation, tried again. Its retry policy starts
  over, with its attempts and deadline counted from the step's new start.
- A service that stores its answer for a key, failures included, as many payment APIs do, gives
  the resumed step its stored failure again, as it would for any retry with that key. Resume such a
  step only when its earlier attempts did not reach the service, such as after a timeout or a
  refused connection. For another try with a new key, start a new run. An answer such as a declined
  card is a result, not a failure: [return it as a value](#Return-an-expected-failure-as-a-value).
- A resume is refused with `InvalidOperationException` when the instance has not faulted, when the
  body threw an exception of its own, which would fault it the same way, and when the body ran steps
  after the failure, such as compensation that would otherwise be left out of order.

### Start a new run

To run faulted or cancelled work again from the first step under the same ID, start it with a reuse
policy: in F#, `handle.StartWith WorkflowIdReuse.AfterFailure "order-17" input`, and in C#,
`client.StartAsync("order-17", input, WorkflowIdReuse.AfterFailure)`. That begins a new **run** with
the new input, stored in the same journal:

- The new run does not continue where the earlier run stopped. It replays none of that run's steps
  or signals and runs every step from the first one, including the steps that succeeded before the
  fault.
- From run 2 on, idempotency keys, command IDs, and child IDs name the run as `<ID>#<run>`, such as
  `order-17#2`, so an external system does not take the new run's calls for repeats. Run 1 keeps
  `order-17`. A workflow ID cannot contain `#`.
- So work the failed run did is done again: a card it charged is charged a second time. Before a
  new run, the failed run must have undone its work, for example in a `try ... with` that
  compensates before the body ends. Or an activity whose work must happen once per order passes a
  key of its own, such as the order number, instead of the step's idempotency key, so that the
  external system recognises the second call.
- A finished instance stays final, and a running or parked one is never replaced: the start stores
  nothing and reports it.
- Recovery reads every run's records, so each run adds to the time an instance takes to recover.
- A projection over the workflow's records sees the new run's `Started` record, after the earlier
  run's end.

[Find workflows that wait for a signal](../how-to/find-waiting-workflows.html#List-every-instance-and-its-status)
lists every instance with its status.

## An activity runs at least once

A crash can fall between the external call and the storing of its result. The call then happens
twice. FCQRS cannot prevent that, because the external system and the journal do not share a
transaction.

Each attempt receives an `IdempotencyKey` built from the workflow's name, the instance's ID, and
the step's position. It is the same for every attempt and after every restart. Pass it to the
external system. The external system must use it to recognise a repeated call.

## The body must be deterministic

Replay only works if the body takes the same path every time it is given the same stored results.

- Read the time with `now`, not `DateTime.UtcNow`.
- Create identifiers with `newGuid` and random numbers with `random`.
- Do all I/O inside an activity.

`now`, `newGuid`, and `random` store their value, so replay returns the first value. A body that
reads the clock directly takes a different path on replay, and the instance is parked or does the
wrong thing. FCQRS does not detect that at run time. The FCQRS.Analyzers package reports a direct
read of the clock, a GUID generator, a random source, or the environment in a body:
[Check bodies and folds for nondeterminism](../how-to/check-for-nondeterminism.html).

## Retries belong to the step

An activity's `RetryPolicy` has a schedule, an attempt limit, a deadline for the whole step, a
timeout for one attempt, and a filter that decides which exceptions are worth another attempt.

- `Retry.once timeout` makes one attempt.
- `Retry.times 5 timeout` makes at most five, with backoff from one second to one minute.
- `Retry.forever deadline` retries until the deadline.

Every activity has a time limit: an `AttemptTimeout`, which `once` and `times` take, or a
`Deadline`. An attempt that runs out of time fails even when the activity ignores its cancellation
token. The call keeps running, and its result is ignored. `activity` raises `ArgumentException`
for a policy with neither, or with a limit that is not positive.

The deadline is measured from the stored start of the step, so a restart does not extend it. An
activity whose deadline passed while the process was stopped fails as `DeadlineExceeded` without
another attempt. Attempts are not stored: after a restart the attempt count and the backoff begin
again.

When the policy runs out, the failure is stored and raised in the body as
`WorkflowStepFailedException`. The body can catch it and continue. Replay raises the same exception,
never the original one.

An activity's result is stored as JSON and read back on replay. A result that the serializer can
write and cannot read back, such as a record with an interface-typed field, fails the step as
`NotRetryable` after the call was made.

## See what is running, and what a shutdown waits for

`Workflows.running api` (C#: `WorkflowHost.Running(actor)`) lists the step attempts the process is
running, oldest first: each activity call, command waiting for its reply, and child start, with its
workflow, instance, step, attempt, and start time.

```fsharp
for attempt in Workflows.running api do
    printfn "%s %s %s attempt %d since %O, cancelled: %b"
        attempt.Workflow attempt.Instance attempt.Step attempt.Attempt
        attempt.Started attempt.CancellationRequested
```

An attempt stays listed until its call returns. An attempt whose `CancellationRequested` is true
and that stays listed is a call that ignores its token, after a timeout, an abandoned branch, or a
stop.

When the actor system shuts down, it stops every workflow, which cancels each running attempt's
token, and then waits for the calls to return, for up to `akka.fcqrs.workflow-drain-timeout`
(three seconds). An activity that observes its token can finish its cleanup in that time. The
shutdown logs a warning naming each call still running when the time is up, and ends without it.
Akka.NET gives the shutdown phase five seconds by default: a longer drain timeout needs
`akka.coordinated-shutdown.phases.before-actor-system-terminate.timeout` raised too. A call that
outlives the shutdown can still reach the external system, and the step runs again after the
restart, as for a crash.

## Return an expected failure as a value

`checkout` reserves stock and charges a card. Stock that has run out and a declined card are
answers from the external system, so its calls return them as values. `reserveStock` and
`chargeCard` return a `Task<Result<string, CheckoutError>>`, in C# a `Task<Outcome>`, and the error
type lists the answers:

```fsharp
type CheckoutError =
    | OutOfStock of available: int
    | Declined of reason: string

// The external calls. Each takes the step's idempotency key.
let reserveStock (key: string) (order: string)
    : Task<Result<string, CheckoutError>> =
    Task.FromResult(Ok "r-1")

let chargeCard (key: string) (order: string)
    : Task<Result<string, CheckoutError>> =
    Task.FromResult(Error(Declined "card expired"))

let releaseStock (key: string) (reservation: string) : Task<string> =
    Task.FromResult "released"
```

<div class="cs-alt"></div>

<!-- sample: workflows/csharp Concepts.cs outcome -->
```csharp
// Each case is named so that a stored value reads back as its case.
[JsonDerivedType(typeof(OutOfStock), "OutOfStock")]
[JsonDerivedType(typeof(Declined), "Declined")]
public abstract record CheckoutError;
public sealed record OutOfStock(int Available) : CheckoutError;
public sealed record Declined(string Reason) : CheckoutError;

// A step's value, or the error that ends the checkout.
public sealed record Outcome(string? Value, CheckoutError? Error);

// The external calls. Each takes the step's idempotency key.
public static class Warehouse
{
    public static Task<Outcome> ReserveStock(string key, string order) =>
        Task.FromResult(new Outcome("r-1", null));

    public static Task<Outcome> ChargeCard(string key, string order) =>
        Task.FromResult(new Outcome(null, new Declined("card expired")));

    public static Task<string> ReleaseStock(string key, string reservation) =>
        Task.FromResult("released");
}
```

`workflowResult` builds a workflow that gives a `Result`. A `let!` on a step that gives an `Error`
ends the block with that `Error`. A `let!` on any other step, such as `now` or `sleep`, continues
with its value:

```fsharp
let policy = Retry.times 3 (TimeSpan.FromSeconds 30.0)

let checkout =
    Workflow.define "checkout" (fun (order: string) -> workflowResult {
        let! reservation =
            activity "reserve" policy (fun attempt ->
                reserveStock attempt.IdempotencyKey order)
        // The inner block gives the Result itself, so this one can release
        // the stock before it ends with the Error.
        let! payment = workflow {
            match! activity "charge" policy (fun attempt ->
                chargeCard attempt.IdempotencyKey order) with
            | Ok payment -> return Ok payment
            | Error error ->
                // An Error is a value, so `try ... with` would not see it.
                let! _ = activity "release" policy (fun attempt ->
                    releaseStock attempt.IdempotencyKey reservation)
                return Error error
        }
        return reservation, payment
    })
```

<div class="cs-alt"></div>

<!-- sample: workflows/csharp Concepts.cs checkout -->
```csharp
public sealed record Checkout(string Reservation, string Payment);
public sealed record CheckoutResult(Checkout? Done, CheckoutError? Error);

public sealed class CheckoutWorkflow()
    : Workflow<string, CheckoutResult>("checkout")
{
    static readonly RetryPolicy Policy =
        Retry.times(3, TimeSpan.FromSeconds(30));

    public override async WorkflowTask<CheckoutResult> Run(
        WorkflowContext ctx, string order)
    {
        var reserved = await ctx.Activity("reserve", Policy,
            attempt => ReserveStock(attempt.IdempotencyKey, order));
        if (reserved.Error is { } refused)
            return new CheckoutResult(null, refused);
        var reservation = reserved.Value!;
        var charged = await ctx.Activity("charge", Policy,
            attempt => ChargeCard(attempt.IdempotencyKey, order));
        if (charged.Error is { } declined)
        {
            // An error is a value, so a catch block would not see it.
            await ctx.Activity("release", Policy,
                attempt => ReleaseStock(attempt.IdempotencyKey, reservation));
            return new CheckoutResult(null, declined);
        }
        return new CheckoutResult(
            new Checkout(reservation, charged.Value!), null);
    }
}
```

The `Error` is the step's stored result. It is not retried, and replay returns it with its fields.
The steps before it stay stored. C# has no `workflowResult`: an `await` followed by a `return`
ends the body the same way.

An exception takes the other path. The policy retries it, and when the attempts run out the body
sees `WorkflowStepFailedException`, which holds only the exception's type name and message. An
activity that throws for a declined card retries a refusal that will not change, and the reason is
lost. An activity that returns an `Error` for a timeout stores it, and the call is never tried
again. Return the answers the external system gives, and throw when the call did not complete.

`try ... with` and `catch` see only exceptions, so compensation for an `Error` belongs in the code
that receives it, as `checkout` releases the stock. `try ... finally` runs in both cases.

The error type is stored with each step's result, so its cases and fields are a persistent
contract like an event's. In C#, the abstract record needs a `JsonDerivedType` for every case.
Without them the stored value cannot be read back, and the step fails as `NotRetryable` after the
call was made. `WorkflowResult.mapError` converts a step's error type, so steps with different
error types can share one block.

## Cancel a workflow

`handle.Cancel id` stores the request together with a `Cancelled` failure for every step the body
is waiting on. Running activities have their cancellation token cancelled. The body sees the
failure as a `WorkflowStepFailedException` and can run cleanup steps before it lets the exception
pass:

```fsharp
let booking =
    workflow {
        try
            let! paid = activity "charge" (Retry.forever (TimeSpan.FromHours 1.0)) (fun attempt ->
                            charge attempt.IdempotencyKey 30m)
            return paid
        with :? WorkflowStepFailedException as failed
                when failed.Error.Kind = ErrorKind.Cancelled ->
            let! _ = activity "refund" (Retry.once (TimeSpan.FromSeconds 30.0)) (fun attempt ->
                         charge attempt.IdempotencyKey -30m)
            return raise failed
    }
```

<div class="cs-alt"></div>

<!-- sample: workflows/csharp Concepts.cs booking -->
```csharp
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
        await ctx.Activity(
            "refund", Retry.once(TimeSpan.FromSeconds(30)),
            attempt => Charge(attempt.IdempotencyKey, -30m));
        throw;
    }
}
```

A body that lets the cancellation pass ends as cancelled. A body that catches it and returns a value
ends as finished. A [child workflow](workflow-steps.html#Start-other-workflows) the body was
waiting for is cancelled with it.

- Put cleanup around a parallel block, not inside one branch. When one branch of `all` fails, the
  other branches are abandoned, and a cleanup step a branch had begun is abandoned with it.
- A second cancel request while cleanup runs changes nothing.
- A child that was started with `startChild` and is not being awaited keeps running. A child whose
  start was still in flight when the parent was cancelled is cancelled too.
- A parked parent that is cancelled cancels the child it was starting or waiting for. A child in a
  race's losing branch keeps running.
- The parent sends these cancels again each time it recovers after storing the cancel. A parent
  that has ended recovers only when a request reaches it. Until then, a child that missed the cancel
  because the process stopped keeps running.

## Change a workflow while instances are running

The journal stores each step's name, kind, and position. On replay, the body must ask for the same
step at the same position.

- **Safe:** changing an activity's implementation, changing steps no running instance has reached,
  and changing a workflow that has no running instances.
- **Not safe:** adding, removing, reordering, or renaming steps that a running instance has passed,
  and replacing a step with a parallel block or a block with a step.

`version` records which code an instance runs:

```fsharp
let evolving =
    workflow {
        let! points = activity "score" (Retry.times 3 (TimeSpan.FromSeconds 30.0)) (fun attempt ->
                          score attempt.IdempotencyKey "A")
        let! change = version "add-charge" 2
        if change >= 2 then
            let! _ = activity "charge" (Retry.once (TimeSpan.FromSeconds 30.0)) (fun attempt ->
                         charge attempt.IdempotencyKey 5m)
            ()
        return points
    }
```

<div class="cs-alt"></div>

<!-- sample: workflows/csharp Concepts.cs evolving -->
```csharp
static async WorkflowTask<int> Evolving(WorkflowContext ctx)
{
    var points = await ctx.Activity(
        "score", Retry.times(3, TimeSpan.FromSeconds(30)),
        attempt => Score(attempt.IdempotencyKey, "A"));
    var change = await ctx.Version("add-charge", 2);
    if (change >= 2)
        await ctx.Activity(
            "charge", Retry.once(TimeSpan.FromSeconds(30)),
            attempt => Charge(attempt.IdempotencyKey, 5m));
    return points;
}
```

A new instance stores the latest number, 2, and takes the new branch. An instance that had already
passed this point gets 1 and keeps its old steps. "Had passed" means the journal stores a step after
the `version` step, so put a `version` step before the steps it guards and before at least one step
the old code already had: as the last step of a workflow, it gives 2 to every running instance.

In a loop, an instance that is part-way through gets 1 for the iterations it has done and 2 for the
ones it has not. For a large change, register the new code under a new workflow name and keep the
old definition until its instances have ended.

The first argument, `"add-charge"`, names the change. FCQRS stores the number an instance got under
that name and the step's position, so the name is a persistent contract:

- Use one name per position. A `version` step in a loop or a recursive workflow stores one marker
  per round, each at its own position. Two `version` steps in a row, for two changes, need two
  names: with one name, both would read and write one marker.
- For a later change at the same point, keep the name and raise the number:
  `version "add-charge" 3`. An instance that stored 2 keeps 2.
- Do not rename the change while instances that ran it exist. An instance that stored 2 finds no
  marker under the new name, has later steps stored, and so gets 1: it switches to the old steps on
  its next replay.

An instance whose journal does not match its code is **parked**. It runs nothing, logs an error,
reports `Parked` from `handle.Status`, and resumes when code that matches its journal is deployed.
A body that ends before a step its journal holds does not match either, so removing the last steps
of a workflow parks the instances that had stored them.
FCQRS does not stop the process for this, because every running instance restarts with the process
and one mismatched instance would stop all the others on every start. A parked instance can be
cancelled; it then ends without cleanup.

## What the application must still do

- Make external calls safe to repeat, using the idempotency key.
- Keep the body deterministic.
- Make aggregates answer a repeated command with their first verdict.
- Give every `Retry.forever` a deadline the business can live with, and handle
  `WorkflowStepFailedException` where a failure has a business meaning.
- Decide what `OutcomeUnknown` means for each command.
- Watch for parked and faulted instances. Nothing restarts a faulted instance by itself: once the
  cause is fixed, resume it at its failed step, or start a new run with
  `WorkflowIdReuse.AfterFailure`.
- Run on one node. FCQRS does not support multi-node deployment.

## Choose between a saga and a workflow

| | Saga | Workflow |
|---|---|---|
| Starts from | an aggregate's stored event | a call with an ID and input |
| Written as | states and transitions | straight-line code |
| External calls | through an aggregate | activities with retry policies |
| Recovery | re-drives the current state's commands | replays the journal to the current step |
| Recovery cost | the current state, with snapshots | the whole journal, without snapshots |
| Changing code | add or change states | `version`, or a new workflow name |
| C# | state classes | `async` methods |

Use a saga when a stored event must set a process in motion and every state should be visible. Use
a workflow when a caller starts the process and the steps read best in order.

## C# equivalents

A C# workflow derives from `Workflow<TInput, TResult>` and awaits steps from its `WorkflowContext`:

| F# | C# |
|---|---|
| `activity name policy run` | `ctx.Activity(name, policy, run)` |
| `sleep`, `until` | `ctx.Sleep`, `ctx.Until` |
| `now`, `newGuid`, `random` | `ctx.Now()`, `ctx.NewGuid()`, `ctx.Random()` |
| `waitSignal`, `waitSignalFor` | `ctx.WaitSignal<T>(name)`, `ctx.WaitSignal<T>(name, timeout)` |
| `all`, `race` | `ctx.All(...)`, `ctx.Race(...)`, with `ctx.Run(() => Method(ctx))` for a method |
| `let! a = ... and! b = ...` | `var (a, b) = await ctx.Both(..., ...)` |
| `startChild`, `awaitChild`, `child` | `ctx.StartChild`, `ctx.AwaitChild`, `ctx.Child`, by workflow name |
| `send` | `ctx.Send<TCommand, TEvent>(factory, id, command)` |
| `sendWith policy` | `ctx.Send<TCommand, TEvent>(policy, factory, id, command)` |
| `sendWithFilter policy filter` | `ctx.Send<TCommand, TEvent>(policy, factory, id, command, filter)` |
| `version` | `ctx.Version(changeId, latest)` |
| `try ... with` for cleanup | `try`/`catch`, or `await` in a `finally` block |
| `handle.Result id timeout` | `client.OutcomeAsync(id, timeout)`, or `client.ResultAsync(id, timeout)`, which throws for a faulted or cancelled workflow |
| `WorkflowFlow.run` | `WorkflowFlow.Run(client, idOf, parallelism, timeout)` |
| `WorkflowFlow.start` | `WorkflowFlow.Start(client, idOf, parallelism)` |

Inside a method that returns `WorkflowTask<T>`, `await` accepts workflow steps. Awaiting an
unfinished `Task`, such as `Task.Delay` or an `HttpClient` call, throws and faults the instance,
because its result could not be stored or replayed. An `await` of a task that has already finished
is not detected.

Next: [Workflow steps](workflow-steps.html) covers the other steps a body can await, and [Write a
workflow](../how-to/write-a-workflow.html) builds and runs one in both languages.
