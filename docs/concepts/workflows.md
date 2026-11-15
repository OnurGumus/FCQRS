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
step; this page explains the model behind it.

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

`and!` runs a fixed set of steps together when their results have different types. In C#,
`ctx.Both` runs two steps and gives a tuple:

```fsharp
let pointsAndReceipt =
    workflow {
        let! points =
            activity "score" (Retry.times 3) (fun attempt ->
                score attempt.IdempotencyKey "A")
        and! receipt =
            activity "charge" Retry.none (fun attempt ->
                charge attempt.IdempotencyKey 30m)
        return points, receipt
    }
```

<div class="cs-alt"></div>

<!-- sample: workflows/csharp Concepts.cs pair -->
```csharp
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
```

The body continues when both steps have finished, as with `all`. Each result is stored. If one
step fails, the other is abandoned and the failure is raised at the `let!`, or at the `await` in
C#. A third `and!` adds a third step. In C#, pass a `ctx.Both` as one of the two steps.

Branches take turns inside one actor. Activities run outside it, so their I/O is concurrent.

Every branch's results are stored in the instance's one journal. Recovery replays all of it, so a
fan-out over thousands of items makes recovery slow. Start a child workflow per item or per chunk
for that.

## Repeat steps

A body can repeat steps with `while`, with `for`, or by calling itself. This one checks an order
every five minutes until it has shipped:

```fsharp
let rec waitUntilShipped checks =
    workflow {
        let! shipped =
            activity "check" (Retry.times 3) (fun attempt ->
                isShipped attempt.IdempotencyKey "A")
        if shipped then
            return checks
        else
            do! sleep (TimeSpan.FromMinutes 5.0)
            return! waitUntilShipped (checks + 1)
    }
```

<div class="cs-alt"></div>

<!-- sample: workflows/csharp Concepts.cs repeat -->
```csharp
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
```

Steps are numbered in the order the body reaches them, so every round's `check` has its own number
and its own stored result. Replay gives each round the result it had. The stack stays the same size
at any depth, also when the body does more work after the call returns.

A workflow has no snapshot. Every round stays in the instance's journal, and recovery replays all of
it: checking every five minutes for a month leaves about 17,000 steps to replay. Give a loop a
bound, and start a new instance for each period of work that has no end.

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
  `ChildFaulted`. A result that the step's type cannot read fails it with `NotRetryable`.
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
an event that its own command caused. An event that another command caused is not taken, also when
it carries the same correlation ID.

The subscription lives in memory. After a restart, a command whose reply was not stored is sent
again. Every send of one step carries the same correlation ID and the same command ID, so a reply
that arrives late for one send still answers the step, and the aggregate can recognise a repeat by
the command's ID. The aggregate must answer a repeated command with the verdict it gave the first
time, and must check for the repeat before it checks anything the first command changed. A
withdrawal that is repeated must not be refused for lack of funds because the first one took the
money.

With no reply before the step's deadline, the step fails as `OutcomeUnknown`. That does not mean the
command was refused: it may have been applied.

One send waits for its reply for `akka.fcqrs.command-timeout`, and then the command is sent again. A
policy's `AttemptTimeout` replaces that time for its step.

A restart can take longer than the deadline. The command is still sent again, because a repeat is
safe and the first outcome is unknown. After a restart each attempt runs to its end, and the
deadline ends the step only when an attempt has failed after it. `startChild` follows the same rule.

## A command whose event starts a saga

A stored event can start a [saga](sagas.html). When a workflow's command makes an aggregate store
such an event, the event is the step's reply and the saga starts. The workflow goes on to its next
step. It does not wait for the saga.

To go on only when the saga has finished, wait for a signal after the command. In the bank,
`TransferSent` starts the transfer saga:

```fsharp
let transferAndWait (accounts: AggregateHandle<AccountCommand, AccountEvent>) =
    workflow {
        let! reply = send accounts "alice" (SendTransfer("t-1", "bob", 30m))
        match reply with
        | TransferSent _ ->
            // The transfer saga has started and runs on its own.
            let! delivered =
                waitSignalFor<bool> "transfer-finished" (TimeSpan.FromMinutes 10.0)
            return defaultArg delivered false
        | _ -> return false
    }
```

<div class="cs-alt"></div>

<!-- sample: workflows/csharp Concepts.cs saga -->
```csharp
static async WorkflowTask<bool> TransferAndWait(
    WorkflowContext ctx, AggregateFactory accounts)
{
    var reply = await ctx.Send<AccountCommand, AccountEvent>(
        accounts, "alice", new SendTransfer("t-1", "bob", 30m));
    if (reply is not TransferSent)
        return false;
    // The transfer saga has started and runs on its own.
    var delivered = await ctx.WaitSignal<bool>(
        "transfer-finished", TimeSpan.FromMinutes(10));
    return delivered.Received && delivered.Value;
}
```

A projection that handles the saga's last event sends the signal with `Workflows.signal`. Its
handler needs the workflow's ID: carry the ID in the command and its events, or use it as the
aggregate's ID. The signal is stored, so the workflow gets it after a restart, and a signal that
arrives before the body reaches its wait is kept. Two cases need care in the handler:

- A workflow that has ended refuses a signal with `InvalidOperationException`. A projection started
  with `Fcqrs.projection` terminates the process when its handler throws, so the handler must catch
  that exception.
- A projection can hand an event to its handler again. The second signal stays stored for a later
  wait on the same name.

The rule for a repeated command decides what a lost reply costs here:

- An aggregate that answers the repeat with a deferred copy of its first event completes the step. A
  deferred event starts no saga, so the saga that is running is left alone.
- An aggregate that stores the event again is refused. A saga starts once per correlation ID and
  aggregate, so nothing is stored, and the step fails with `SagaAlreadyStartedException` although
  the first command was applied.

A saga's commands carry the correlation ID of the command that started it. When they come back to
the same aggregate, their events are published under the ID the step waits on. `send` does not take
them: it gives only the reply to its own command.

`sendWithFilter` does. It gives the first event its filter accepts, among the events that the
aggregate it sends to publishes under the step's correlation ID. Here a reservation stores
`Reserved`, which starts a saga, and the saga's last command makes the same reservation store
`Confirmed`:

```fsharp
let reserveAndWait
    (reservations: AggregateHandle<ReservationCommand, ReservationEvent>)
    (id: string) =
    workflow {
        let isConfirmed reply =
            match reply with
            | Confirmed _ -> true
            | Reserved _ -> false
        // One send, and five minutes to wait for the saga.
        let patient =
            { Retry.command with AttemptTimeout = Some(TimeSpan.FromMinutes 5.0) }
        return! sendWithFilter patient isConfirmed reservations id (Reserve id)
    }
```

<div class="cs-alt"></div>

<!-- sample: workflows/csharp Concepts.cs filter -->
```csharp
static async WorkflowTask<ReservationEvent> ReserveAndWait(
    WorkflowContext ctx, AggregateFactory reservations, string id)
{
    // One send, and five minutes to wait for the saga.
    var patient = Retry.command
        .WithAttemptTimeout(TimeSpan.FromMinutes(5));
    return await ctx.Send<ReservationCommand, ReservationEvent>(
        patient, reservations, id, new Reserve(id),
        reply => reply is Confirmed);
}
```

This wait is in memory, which sets its limits:

- An event published while no attempt listens is not seen. That happens during a restart and between
  two attempts.
- Each attempt listens for the policy's `AttemptTimeout`, and then the command is sent again. A
  policy without one, such as `Retry.command`, listens for `akka.fcqrs.command-timeout`, which is
  30 seconds, so a wait for a saga sends the command again and again. With a long `AttemptTimeout`
  the step sends once and waits. A command that never reached the aggregate is then sent again only
  when that time has passed.
- The aggregate must answer a repeated command with an event the filter accepts once the outcome is
  known. An aggregate that answers with its first event leaves the step waiting, and the step fails
  as `OutcomeUnknown` although the saga finished.
- The policy's deadline bounds the whole wait. `Retry.command`, the policy of `send`, allows five
  minutes.
- Only events of the aggregate the command was sent to are heard. An event that the saga makes
  another aggregate store is not.

Wait for a signal when the event must not be missed. `send` and `sendWithFilter` store the same
step, so one can replace the other in a workflow that is running.

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
- **Not safe:** adding, removing, reordering, or renaming steps that a running instance has passed,
  and replacing a step with a parallel block or a block with a step.

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

The first argument, `"add-charge"`, names the change. FCQRS stores the number an instance got
under the step's position and that name, so the name decides which stored number a `version` step
reads back:

- One name can be used at different positions. A `version` step inside a loop or a recursive
  workflow stores one marker per round, each at its own position.
- Different names can share a position. Two `version` steps in a row, for two changes, keep two
  markers.
- The same name twice at one position is not allowed: both steps would read and write one marker.
- A later change at the same point keeps the name and raises the number, for example
  `version "add-charge" 3`. An instance that stored 2 keeps 2.
- The name must not change while instances that ran it exist. After a rename, an instance that
  stored 2 finds no marker under the new name, has later steps stored, and so gets 1: it switches
  to the old steps on its next replay.

An instance whose journal does not match its code is **parked**. It runs nothing, logs an error,
reports `Parked` from `handle.Status`, and resumes when code that matches its journal is deployed.
FCQRS does not stop the process for this, because every running instance restarts with the process
and one mismatched instance would stop all the others on every start. A parked instance can be
cancelled; it then ends without cleanup.

## Run workflows on a schedule

A schedule starts a registered workflow at each of its due times. Ada wants a reminder every day at
09:00, Oslo time. The reminder is a workflow whose input is a `ScheduledRun`, and a schedule starts
it:

```fsharp
let remind =
    Workflow.define "remind" (fun (run: ScheduledRun<string>) -> workflow {
        return!
            activity "send" (Retry.times 3) (fun attempt ->
                sendReminder attempt.IdempotencyKey run.Input)
    })

let startReminders (api: IActor) =
    async {
        let reminders = Workflows.register api remind
        let schedules =
            Schedules.register api [ ScheduledWorkflow.ofHandle reminders ]
        // Schedule commands raise until the starters are wired.
        Fcqrs.wireSagaStarters api []
        do!
            Schedules.createLatest schedules "ada-daily" "remind" "Drink water"
                (ScheduleRule.Daily(TimeOnly(9, 0), "Europe/Oslo"))
        return schedules
    }
```

<div class="cs-alt"></div>

<!-- sample: workflows/csharp Concepts.cs schedule -->
```csharp
public sealed class Remind()
    : Workflow<ScheduledRun<string>, string>("remind")
{
    public override async WorkflowTask<string> Run(
        WorkflowContext ctx, ScheduledRun<string> run) =>
        await ctx.Activity("send", Retry.times(3), attempt =>
            SendReminder(attempt.IdempotencyKey, run.Input));
}

public static class Reminders
{
    public static async Task<ScheduleClient> Start(IActor actor)
    {
        var reminders = WorkflowHost.Register(actor, new Remind());
        var schedules = ScheduleHost.Register(
            actor, ScheduleHost.Workflow(reminders));
        // Schedule commands throw until the starters are wired.
        ActorWiring.InitSagaStarterEmpty(actor);
        await schedules.CreateAsync("ada-daily", "remind", "Drink water",
            ScheduleRule.NewDaily(new TimeOnly(9, 0), "Europe/Oslo"));
        return schedules;
    }
}
```

`Schedules.register` registers the schedule aggregate and starts the actor system's one
**dispatcher**, which may start only the workflows in its list. The list takes each workflow's
handle, so the workflows are registered first. Each schedule is an instance of the aggregate
`fcqrs-schedule`. Like every aggregate, it waits for the actor system's saga starters before it
stores an event, and terminates the process when they are not wired within
`akka.fcqrs.saga-start-timeout`. Call `Fcqrs.wireSagaStarters` once, after every aggregate and saga
is registered, as the [aggregate guide](../how-to/define-an-aggregate.html) describes; an
application without sagas passes an empty list. Until the starters are wired, `create`, `change`,
`pause`, `resume`, and `delete` raise `InvalidOperationException` without sending anything, and the
dispatcher fires nothing.

A C# host built with `AddFcqrs` wires the saga starters when the host starts. Register the
schedules after that, for example from a singleton resolved once the host has started:

```csharp
builder.Services.AddFcqrs(connectionString, "reminders");
builder.Services.AddWorkflow<Remind, ScheduledRun<string>, string>();
builder.Services.AddSingleton(services => ScheduleHost.Register(
    services.GetRequiredService<IActor>(),
    ScheduleHost.Workflow(services.GetRequiredService<
        IWorkflowClient<ScheduledRun<string>, string>>())));

using var host = builder.Build();
await host.StartAsync();
var schedules = host.Services.GetRequiredService<ScheduleClient>();
await schedules.CreateAsync("ada-daily", "remind", "Drink water",
    ScheduleRule.NewDaily(new TimeOnly(9, 0), "Europe/Oslo"));
```

`Schedules.createLatest` is `Schedules.create` with the catch-up policy `CatchUp.Latest`, the
default, which [Due times, downtime, and changes](#Due-times-downtime-and-changes) explains.
`Schedules.create` takes the policy as its last argument. In C#, `CreateAsync` without a policy uses
`CatchUp.Latest`, and an overload takes the policy.

`Schedules.create` returns once the schedule is stored. A second `create` with the same content
stores nothing; the days of a weekly rule are sorted and their repeats removed first, so
`[Thursday; Monday]` and `[Monday; Thursday]` are the same content. A different content for an
existing ID raises `InvalidOperationException`, and so do a workflow the dispatcher does not list
and an input whose type differs from the workflow's.

`create` and `change` check their arguments before they send anything and raise
`ArgumentException` for a rule that `ScheduleRule.validate` rejects: a time zone the system cannot
read, a weekly rule without days or with a value that is not a day of the week, or an `Every`
interval shorter than one second or longer than 366 days. A missing rule or policy raises
`ArgumentNullException`. A schedule ID that is blank, contains `~`, or is longer than
`Schedules.MaxIdLength` (200) characters raises `ArgumentException`. A run's persistence ID, the
escaped workflow name, `/default-shard/`, and the escaped run ID, must fit in the journal's
255-character column. Akka escapes both names as `Uri.EscapeDataString` does, and a run ID adds 35
escaped characters to the schedule ID, so the escaped workflow name and the escaped schedule ID may
take 205 characters together; more raises `ArgumentException`. A character outside ASCII escapes
to 6 to 12 characters, so with the workflow `remind` an ID of 34 such characters is already too
long.

### A run starts at least once per due time

The dispatcher keeps an index of every schedule and sleeps until the earliest due time. For each
schedule that is due, it takes three steps:

1. It asks the schedule to fire for the due time. The schedule aggregate decides whether that due
   time runs and stores the workflow and the input the run gets. The index is only a hint. The
   schedule decides from its own state, so an index that has not seen a pause, change, or delete
   cannot start a run for a paused or deleted schedule, or for a due time that a change removed.
2. It starts the run. Its workflow ID is the schedule's ID, `@`, and the due time in UTC in ISO 8601
   form, such as `ada-daily@2026-11-15T08:00:00.0000000Z`. Its input is
   `ScheduledRun { Schedule; Due; Input }`, because a workflow body cannot read its own ID.
3. It reports the start, and the schedule stores it.

The schedule decides before the run starts, so a pause stored before step 1 stops the run. A crash
after step 1 leaves a decided run with no reported start. After the restart the dispatcher starts
it again with the same ID, and a second start of an existing workflow stores nothing. A run
therefore starts at least once per accepted due time, and one workflow instance serves it.

A run is an ordinary workflow instance. A run that faults ends only that instance, and the schedule
goes on. A run that is still going at the next due time runs alongside the next one.

### Due times, downtime, and changes

- `ScheduleRule.Daily(time, zone)` and `ScheduleRule.Weekly(days, time, zone)` take a local time
  and a time-zone ID. Due times are computed in the schedule's zone and stored in UTC, so Ada's
  reminder runs at 09:00 Oslo time in winter (08:00 UTC) and in summer (07:00 UTC). A local time that does not exist on a day, because
  clocks move forward, runs at the next local time that does. A local time that occurs twice,
  because clocks move back, runs once, at the first occurrence.
- `ScheduleRule.Every interval` counts from the creation or the last change.
- `CatchUp` decides which missed due times run when the dispatcher could not fire on time, for
  example while the process was stopped. `CatchUp.Latest`, the default, starts one run for the most
  recent one. `CatchUp.All` starts one for each, oldest first, but at most for the 1000 most recent.
  `CatchUp.None` starts none and waits for the next due time; a fire up to one minute late still
  counts as on time.
- `Schedules.pause` stops runs. `Schedules.resume` continues from the first due time after the
  resume: a due time inside the pause never runs, whatever the catch-up policy.
- `Schedules.change` replaces the workflow, input, rule, and catch-up policy. No due time at or
  before the change runs.
- `Schedules.delete` ends the schedule. Its ID cannot be used for a new schedule, so a run ID never
  belongs to two schedules.
- A run decided before a change, pause, or delete still starts, on the workflow and with the input
  stored when it was decided.
- A fire more than one second before its due time is refused, so a fault in the dispatcher cannot
  start a run more than a second early.

A deploy can stop listing a workflow that stored schedules name. The dispatcher then sends no fire
for those schedules, logs an error at each due time, and waits. When a later start lists the
workflow again, the catch-up policy decides which missed due times run. A decided run whose workflow
is not listed waits the same way.

`Schedules.state` reads the dispatcher's index, not the aggregate. The index follows the schedule's
events through a projection, so it lags by one projection pass: a `pause` that has just returned may
not show yet. While fewer than 16 starts are running, the dispatcher wakes at least every
`akka.fcqrs.schedule-check-interval`, one minute by default, so a clock change or a missed timer
delays a run by at most that long. While 16 are running, it reads nothing and waits for one of them
to end. No other run could start before then, but the error for a schedule whose workflow is not
registered is logged when a start ends, not at its due time. A run whose start fails, or hangs until
its ask times out, is tried again after the shorter of the check interval and five seconds, counted
from the failure. The dispatcher does not wait for it, so it delays no other schedule while fewer
than 16 starts hang at once. The [configuration reference](../configuration.html) lists the setting.

A projection without entity names reads the schedule aggregate's events too, as
`Event<ScheduleEvent>`. A handler given to `Fcqrs.projection` that raises for an event it does not
know, such as a catch-all `failwith`, terminates the process. Let such a handler ignore other events,
or name the entities it reads, as [Add a projection](../how-to/add-a-projection.html#Read-only-some-entities)
shows.

### The index is stored in the journal's database

The dispatcher's projection, `fcqrs-schedule-index`, keeps the index in the table
`fcqrs_schedule_index`, in the journal's database and schema. `Schedules.register` raises
`ArgumentException` when `akka.persistence.journal.sql` and `akka.persistence.query.journal.sql`
differ in connection string, schema, table, or columns, and registers nothing then. The table has one
row per schedule. Each row is written in the transaction that
stores the projection's read position for that schedule, so a row never runs ahead of or behind its
position. At startup the dispatcher reads every row, then the schedule events stored after those
positions, and fires nothing until it has read both. A restart therefore reads one row per schedule
and the events written since the last stop, however many runs the schedules have had. A deleted
schedule's row is removed once no run decided before the delete waits to start.

The schedule events are the source of the index, and the table is derived data. When the table is
missing, the dispatcher creates it, deletes the projection's read positions, and reads the whole
schedule history to fill it. When a row cannot be read, for example a row written by a newer version
of FCQRS.Workflows, the dispatcher logs a warning that names the schedule, deletes every row and the
read positions, and does the same. After a rebuild the dispatcher can ask again for a run decided
before the stop; the schedule answers with its first verdict, and a second start of the run stores
nothing.

The dispatcher creates three tables in the journal's schema on first use:
`fcqrs_projection_progress` and `fcqrs_projection_progress_lock` for the read positions, and
`fcqrs_schedule_index` for the rows. It then reads and writes them, so the journal's database user
needs the right to create tables in that schema, and afterwards to read and write these three. To
create the tables up front instead, match what FCQRS creates; the index table is
`fcqrs_schedule_index (schedule_id TEXT NOT NULL PRIMARY KEY, entry TEXT NOT NULL)`. Without these
rights the dispatcher cannot load the index: it logs an error every second and no schedule fires.
FCQRS.Workflows 7.6.0 only read the journal.

A journal written by FCQRS.Workflows 7.6.0 has no table, so the first start on it reads every
schedule event once, two for each run, in one transaction per event, and no schedule fires until it
has finished. On SQLite this took about 0.5 ms per schedule event, roughly 8 minutes per million
events; PostgreSQL was not measured. Later starts read only new events. When the first start takes
longer than the projection's `CatchUpTimeout` of 30 seconds, the dispatcher logs a warning and keeps
waiting.

The table's name, its columns `schedule_id` and `entry`, the text stored in `entry`, and the
projection's name are persistent contracts. Nothing but the dispatcher should write to the table.

### What a schedule does not guarantee

- **Exactly-once side effects.** A run's steps follow the workflow rules on this page: an activity
  runs at least once. Its idempotency key includes the run's ID, so it differs per due time and
  stays the same across the attempts of one run. The external system must honour it.
- **Precision below a second.** A run starts after its due time, once the dispatcher has woken, the
  schedule has stored its decision, and the run's start is stored.
- **More than one node.** The dispatcher assumes it is the only one, and FCQRS runs on one node. A
  second `Schedules.register` on one actor system raises `InvalidOperationException`.

The application chooses each user's time zone, keeps run steps safe to repeat, and treats the
workflow's name and the input's stored form as persistent contracts: every schedule stores both.
FCQRS stores the schedule aggregate's name `fcqrs-schedule`, `ScheduleEvent`, `ScheduleRule`,
`CatchUp`, `ScheduleSpec`, the snapshot state `ScheduleState` and `AcceptedDue`, run IDs, every
run's input, `ScheduledRun { Schedule; Due; Input }`, and the index table `fcqrs_schedule_index`
with its rows, so these are persistent contracts too.

[Run scheduled jobs](../how-to/run-scheduled-jobs.html) builds a nightly backup and a weekly invoice
as runnable F# and C# programs.

## Feed workflows from a stream

```fsharp
let outcomes (handle: WorkflowHandle<string, int * DateTime>) =
    WorkflowFlow.run handle (fun order -> $"enrich-{order}") 8 (TimeSpan.FromMinutes 5.0)
```

<div class="cs-alt"></div>

<!-- sample: workflows/csharp Concepts.cs stream -->
```csharp
static Flow<string, WorkflowEnded<Enriched>, NotUsed> Outcomes(
    IWorkflowClient<string, Enriched> client) =>
    WorkflowFlow.Run(
        client, order => $"enrich-{order}", 8, TimeSpan.FromMinutes(5));
```

`WorkflowFlow.run` is an Akka.Streams flow that starts one workflow per element and emits each
outcome in input order, with at most eight workflows between start and outcome. A faulted or
cancelled workflow is an element of the stream. A workflow that does not end within the timeout
fails the stream with a `TimeoutException`, and the workflow carries on. `WorkflowFlow.start` emits
as soon as each start is stored.

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
| `let! a = ... and! b = ...` | `var (a, b) = await ctx.Both(..., ...)` |
| `startChild`, `awaitChild`, `child` | `ctx.StartChild`, `ctx.AwaitChild`, `ctx.Child`, by workflow name |
| `send` | `ctx.Send<TCommand, TEvent>(factory, id, command)` |
| `sendWith policy` | `ctx.Send<TCommand, TEvent>(policy, factory, id, command)` |
| `sendWithFilter policy filter` | `ctx.Send<TCommand, TEvent>(policy, factory, id, command, filter)` |
| `version` | `ctx.Version(changeId, latest)` |
| `try ... with` for cleanup | `try`/`catch`, or `await` in a `finally` block |
| `WorkflowFlow.run` | `WorkflowFlow.Run(client, idOf, parallelism, timeout)` |
| `WorkflowFlow.start` | `WorkflowFlow.Start(client, idOf, parallelism)` |
| `Schedules.register api [ ScheduledWorkflow.ofHandle handle ]` | `ScheduleHost.Register(actor, ScheduleHost.Workflow(client))` |
| `Schedules.createLatest`, with the default `CatchUp.Latest` | `schedules.CreateAsync(id, workflow, input, rule)` |
| `Schedules.create`, `Schedules.change` | `schedules.CreateAsync`, `schedules.ChangeAsync`, with a `CatchUp` |
| `Schedules.pause`, `resume`, `delete` | `schedules.PauseAsync`, `ResumeAsync`, `DeleteAsync` |
| `Schedules.state`, `None` when the index has not read it | `schedules.State(id)`, `null` when the index has not read it |
| `ScheduleRule.Daily(time, zone)` | `ScheduleRule.NewDaily(time, zone)`, and `NewWeekly`, `NewEvery` |

Inside a method that returns `WorkflowTask<T>`, `await` accepts workflow steps. Awaiting an
unfinished `Task`, such as `Task.Delay` or an `HttpClient` call, throws and faults the instance,
because its result could not be stored or replayed. An `await` of a task that has already finished
is not detected.

Next: [Write a workflow](../how-to/write-a-workflow.html) builds and runs one in both languages.
