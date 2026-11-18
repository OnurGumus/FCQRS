---
title: Workflow steps
category: Understand
categoryindex: 4
index: 12
---

# Workflow steps: signals, parallel steps, children, and commands

[Workflows](workflows.html) explains the model with activities and timers: each step's result is
stored, and recovery replays the stored results. This page covers the other steps a body can await.
Each one stores its outcome the same way, so the recovery rules on that page apply to all of them.

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

`handle.Status` names the steps an instance waits on, and `IsWaitingFor "approval"` checks for
one. To list every instance that waits, project the workflow's journal: [Find workflows that wait
for a signal](../how-to/find-waiting-workflows.html).

A timer stores its due time when the body first reaches it. A restart arms it again with the time
that remains.

## Run steps together

```fsharp
let both =
    workflow {
        let! points =
            all [
                for order in [ "A"; "B"; "C" ] ->
                    activity "score" (Retry.times 3 (TimeSpan.FromSeconds 30.0)) (fun attempt ->
                        score attempt.IdempotencyKey order)
            ]
        let! first =
            race [
                workflow {
                    let! paid =
                        activity "charge" (Retry.once (TimeSpan.FromSeconds 30.0)) (fun attempt ->
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
        ctx.Activity(
            "score", Retry.times(3, TimeSpan.FromSeconds(30)),
            attempt => Score(attempt.IdempotencyKey, order))).ToArray());
    var first = await ctx.Race(
        ctx.Run(() => Charged(ctx)),
        ctx.Run(() => TimedOut(ctx)));
    return (points, first);
}

static async WorkflowTask<string?> Charged(WorkflowContext ctx) =>
    await ctx.Activity(
        "charge", Retry.once(TimeSpan.FromSeconds(30)),
        attempt => Charge(attempt.IdempotencyKey, 30m));

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
            activity "score" (Retry.times 3 (TimeSpan.FromSeconds 30.0)) (fun attempt ->
                score attempt.IdempotencyKey "A")
        and! receipt =
            activity "charge" (Retry.once (TimeSpan.FromSeconds 30.0)) (fun attempt ->
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
        ctx.Activity(
            "score", Retry.times(3, TimeSpan.FromSeconds(30)),
            attempt => Score(attempt.IdempotencyKey, "A")),
        ctx.Activity(
            "charge", Retry.once(TimeSpan.FromSeconds(30)),
            attempt => Charge(attempt.IdempotencyKey, 30m)));
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
            activity "check" (Retry.times 3 (TimeSpan.FromSeconds 30.0)) (fun attempt ->
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
    var shipped = await ctx.Activity(
        "check", Retry.times(3, TimeSpan.FromSeconds(30)),
        attempt => IsShipped(attempt.IdempotencyKey, "A"));
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

`parent` starts `enrich`, the workflow from
[Workflows](workflows.html#Start-with-a-workflow-that-calls-an-external-system), as children:

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
- Do not start an instance of a child's workflow with an ID of that form yourself. A parent whose
  child would get that ID takes the instance for its child, waits for it, and cancels it.
- The ID leaves out the parent's workflow name. Two parent workflows whose instances share an ID
  share one child when they start the same child workflow at the same position.

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

## C# equivalents

The [table on the Workflows page](workflows.html#C-equivalents) gives the C# name of every step on
this page.

Next: [Schedules](schedules.html) starts workflows at due times.
