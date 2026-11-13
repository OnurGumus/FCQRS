---
title: Workflows
category: Understand
categoryindex: 3
index: 11
---

# Workflows: durable code that resumes where it stopped

A workflow takes input, calls external systems, waits, and returns a result. FCQRS stores the
result of each step. After a crash, a workflow instance continues at the step it had reached, and
steps whose results are stored do not run again.

Workflows are in the `FCQRS.Workflows` package. They sit beside [sagas](sagas.html) and do not
replace them.

## Start with a workflow that calls an external system

`enrich` scores an order with an external service, waits five minutes, and reads the time:

```fsharp
let enrich =
    Workflow.define "enrich" (fun (order: string) -> workflow {
        let! points =
            activity "score" (Retry.times 3) (fun attempt ->
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
        var points = await ctx.Activity("score", Retry.times(3), attempt =>
            Score(attempt.IdempotencyKey, order));
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

`now`, `newGuid`, and `random` store their value, so replay returns the first value. FCQRS cannot
detect a body that reads the clock directly: the body takes a different path on replay, and the
instance is parked or does the wrong thing.

## Retries belong to the step

An activity's `RetryPolicy` has a schedule, an attempt limit, a deadline for the whole step, a
timeout for one attempt, and a filter that decides which exceptions are worth another attempt.

- `Retry.none` makes one attempt.
- `Retry.times 5` makes at most five, with backoff from one second to one minute.
- `Retry.forever deadline` retries until the deadline.

The deadline is measured from the stored start of the step, so a restart does not extend it.
Attempts are not stored: after a restart the attempt count and the backoff begin again.

When the policy runs out, the failure is stored and raised in the body as
`WorkflowStepFailedException`. The body can catch it and continue. Replay raises the same exception,
never the original one.

## Wait for an answer that arrives later

A signal is a named message sent to an instance from outside:

```fsharp
let approve =
    workflow {
        let! answer = waitSignalFor<bool> "approval" (TimeSpan.FromDays 2.0)
        return defaultArg answer false
    }
```

<div class="cs-alt"></div>

<!-- sample: workflows/csharp Concepts.cs approve -->
```csharp
static async WorkflowTask<bool> Approve(WorkflowContext ctx)
{
    var answer = await ctx.WaitSignal<bool>(
        "approval", TimeSpan.FromDays(2));
    return answer.Received && answer.Value;
}
```

`Workflows.signal handle id "approval" true` stores the signal and then returns. A signal that
arrives before the body reaches its wait is kept, and waits consume signals with one name oldest
first. `waitSignalFor` gives `None` when its time passes; a signal that arrives later stays stored
for the next wait. A signal whose payload the wait cannot read fails that wait with
`WorkflowStepFailedException` and is consumed.

A timer stores its due time when the body first reaches it. A restart arms it again with the time
that remains.

## Run steps together

```fsharp
let both =
    workflow {
        let! points =
            all [
                for order in [ "A"; "B"; "C" ] ->
                    activity "score" (Retry.times 3) (fun attempt ->
                        score attempt.IdempotencyKey order)
            ]
        let! first =
            race [
                workflow {
                    let! paid =
                        activity "charge" Retry.none (fun attempt ->
                            charge attempt.IdempotencyKey 30m)
                    return Some paid
                }
                workflow {
                    do! sleep (TimeSpan.FromMinutes 1.0)
                    return None
                }
            ]
        return points, first
    }
```

<div class="cs-alt"></div>

<!-- sample: workflows/csharp Concepts.cs both -->
```csharp
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
```

- `all` finishes when every branch has finished and gives the results in branch order. If a branch
  fails, the other branches are abandoned.
- `race` takes the first branch to finish and abandons the rest. The winner is stored, so replay
  selects the same branch.
- `allAtMost 4 items body` runs `body` for each item, four at a time.

Branches take turns inside one actor. Activities run outside it, so their I/O is concurrent.

Every branch's results are stored in the instance's one journal. Recovery replays all of it, so a
fan-out over thousands of items makes recovery slow. Start a child workflow per item or per chunk
for that.

## Start other workflows

```fsharp
let parent =
    Workflow.define "parent" (fun (order: string) -> workflow {
        let! detached = startChild enrich order
        let! points, at = child enrich (order + "-again")
        return detached.Id, points, at
    })
```

<div class="cs-alt"></div>

<!-- sample: workflows/csharp Concepts.cs parent -->
```csharp
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
```

- `child` starts another workflow and waits for its result. A child that faults fails the step with
  `ChildFaulted`.
- `startChild` gives a handle as soon as the child's start is stored. A child that is never awaited
  outlives its parent.
- A child's ID is the parent's ID, a colon, and the step's position. A parent that restarts in the
  middle of `startChild` reaches the same child and does not start a second one.

A workflow keeps no snapshot: its position is the rest of its body, which cannot be stored. A
workflow that loops for months should start a detached child with its remaining work and return,
which begins a new journal.

## Send commands to aggregates

```fsharp
let pay (accounts: AggregateHandle<AccountCommand, AccountEvent>) =
    workflow {
        let! reply = send accounts "alice" (Withdraw 30m)
        match reply with
        | Withdrawn amount -> return $"took {amount}"
        | other -> return $"refused: {other}"
    }
```

<div class="cs-alt"></div>

<!-- sample: workflows/csharp Concepts.cs pay -->
```csharp
static async WorkflowTask<string> Pay(
    WorkflowContext ctx, AggregateFactory accounts)
{
    var reply = await ctx.Send<AccountCommand, AccountEvent>(
        accounts, "alice", new Withdraw(30m));
    return reply is Withdrawn taken
        ? $"took {taken.Amount}"
        : $"refused: {reply}";
}
```

`send` subscribes to the aggregate's replies and then sends, so the reply cannot be missed. It gives
the first event the aggregate replies with.

The subscription lives in memory. After a restart, a command whose reply was not stored is sent
again with the same correlation ID. The aggregate must answer a repeated command with the verdict
it gave the first time, and must check for the repeat before it checks anything the first command
changed. A withdrawal that is repeated must not be refused for lack of funds because the first one
took the money.

With no reply before the step's deadline, the step fails as `OutcomeUnknown`. That does not mean the
command was refused: it may have been applied.

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
            let! _ = activity "refund" Retry.none (fun attempt ->
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
        await ctx.Activity("refund", Retry.none, attempt =>
            Charge(attempt.IdempotencyKey, -30m));
        throw;
    }
}
```

A body that lets the cancellation pass ends as cancelled. A body that catches it and returns a value
ends as finished. A child the body was waiting for is cancelled with it.

- Put cleanup around a parallel block, not inside one branch. When one branch of `all` fails, the
  other branches are abandoned, and a cleanup step a branch had begun is abandoned with it.
- A second cancel request while cleanup runs changes nothing.
- A child that was started with `startChild` and is not being awaited keeps running.

## Change a workflow while instances are running

The journal stores each step's name, kind, and position. On replay, the body must ask for the same
step at the same position.

- **Safe:** changing an activity's implementation, changing steps no running instance has reached,
  and changing a workflow that has no running instances.
- **Not safe:** adding, removing, reordering, or renaming steps that a running instance has passed.

`version` records which code an instance runs:

```fsharp
let evolving =
    workflow {
        let! points = activity "score" (Retry.times 3) (fun attempt ->
                          score attempt.IdempotencyKey "A")
        let! change = version "add-charge" 2
        if change >= 2 then
            let! _ = activity "charge" Retry.none (fun attempt ->
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
    var points = await ctx.Activity("score", Retry.times(3), attempt =>
        Score(attempt.IdempotencyKey, "A"));
    var change = await ctx.Version("add-charge", 2);
    if (change >= 2)
        await ctx.Activity("charge", Retry.none, attempt =>
            Charge(attempt.IdempotencyKey, 5m));
    return points;
}
```

A new instance stores 2 and takes the new branch. An instance that had already passed this point
gets 1 and keeps its old steps.

FCQRS decides "had passed" from the journal: a step after the `version` step is stored. Two cases
follow from that:

- Put a `version` step before the steps it guards and before at least one step the old code already
  had. A `version` step added as the last step of a workflow gives 2 to every running instance.
- In a loop, an instance that is part-way through gets 1 for the iterations it has done and 2 for
  the ones it has not. For a large change, register the new code under a new workflow name
and keep the old definition until its instances have ended.

An instance whose journal does not match its code is **parked**. It runs nothing, logs an error,
reports `Parked` from `handle.Status`, and resumes when code that matches its journal is deployed.
FCQRS does not stop the process for this, because every running instance restarts with the process
and one mismatched instance would stop all the others on every start. A parked instance can be
cancelled; it then ends without cleanup.

## Feed workflows from a stream

```fsharp
let outcomes (handle: WorkflowHandle<string, int * DateTime>) =
    WorkflowFlow.run handle (fun order -> $"enrich-{order}") 8 (TimeSpan.FromMinutes 5.0)
```

<div class="cs-alt"></div>

<!-- sample: workflows/csharp Concepts.cs stream -->
```csharp
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
```

`WorkflowFlow.run` is an Akka.Streams flow that starts one workflow per element and emits each
outcome in input order, with at most eight workflows between start and outcome. `WorkflowFlow.start`
emits as soon as each start is stored.

`WorkflowFlow` takes an F# handle. A C# program builds the same flow from its client, as shown.
`ResultAsync` throws for a faulted or cancelled workflow, so that element fails the C# stream, where
the F# flow emits the outcome.

The stream is not durable. If the process stops, the workflows resume and the stream does not. The
source must replay from an offset of its own, and the workflow ID must be computed from the element,
so that a replayed element reaches the instance it started before.

## What the application must still do

- Make external calls safe to repeat, using the idempotency key.
- Keep the body deterministic.
- Make aggregates answer a repeated command with their first verdict.
- Give every `Retry.forever` a deadline the business can live with, and handle
  `WorkflowStepFailedException` where a failure has a business meaning.
- Decide what `OutcomeUnknown` means for each command.
- Watch for parked and faulted instances. A faulted instance is final: nothing restarts it.
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
| `startChild`, `awaitChild`, `child` | `ctx.StartChild`, `ctx.AwaitChild`, `ctx.Child`, by workflow name |
| `send` | `ctx.Send<TCommand, TEvent>(factory, id, command)` |
| `version` | `ctx.Version(changeId, latest)` |
| `try ... with` for cleanup | `try`/`catch`, or `await` in a `finally` block |

Inside a method that returns `WorkflowTask<T>`, `await` accepts workflow steps. Awaiting an
unfinished `Task`, such as `Task.Delay` or an `HttpClient` call, throws and faults the instance,
because its result could not be stored or replayed. An `await` of a task that has already finished
is not detected.

Next: [Write a workflow](../how-to/write-a-workflow.html) builds and runs one in both languages.
