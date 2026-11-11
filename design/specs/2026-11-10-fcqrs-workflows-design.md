# FCQRS.Workflows design

Date: 2026-11-10. Status: draft for review.

## Goal

A workflow is a durable process written as straight-line code. It takes input data, processes it,
calls external systems, and resumes after a crash at the step it had reached. It retries failed
steps and continues.

`FCQRS.Workflows` provides this on FCQRS's own Akka.NET persistence. It does not use a Temporal
server or SDK. Temporal was the reference for the behaviour only.

Success means:

- a workflow starts from input data, with no aggregate required to trigger it;
- a stored step result is never recomputed, across any number of restarts;
- a workflow can call external systems, wait for timers and later-arriving signals, send commands to
  aggregates, and start other workflows;
- F# and C# can both define workflows, and both write the same journal records;
- traces, metrics, and logs follow the conventions core already uses.

## Non-goals

- Multi-node operation. FCQRS runs on one node, and so do workflows.
- Exactly-once external calls. A step runs at least once.
- Replacing sagas. Sagas stay as they are and keep their journal format.
- An operator "resume from step N" for a faulted workflow.
- A user-facing in-memory test kit with fake activities.
- A durable outcome stream source. Projections already receive workflow journal records.

## Decisions taken

| Topic | Decision |
|---|---|
| Authoring style | Straight-line code; each step result is journaled and replayed on recovery |
| Step kinds | Activity, timer, signal, aggregate command, child workflow, stored value |
| Composition | Inline, awaited child, and detached child. No separate "jump" form |
| Parallelism | `all`, `race`, and a capped fan-out, in the first release |
| Start | Direct, by workflow ID. A stored aggregate event is not required |
| Relation to sagas | Coexist. Separate package, separate journal records |
| Languages | F# builder and a C# step API in the first release |
| Retry | A framework policy built on core's `RetryEvery`. No Polly dependency |
| Telemetry | `System.Diagnostics` sources and meters. No OpenTelemetry package reference |
| Compatibility | Core internals and public APIs may change where this design needs it |

## Architecture

### Package

`src/FCQRS.Workflows/`, targeting `net10.0`, depending on `FCQRS`. Akkling.Streams is already a core
dependency.

Core changes this design needs:

- new manifest tags for the workflow records in the fixed list in `Serialization.fs`;
- `FCQRS.Workflow` added to `Telemetry.AllActivitySources`;
- nothing in the aggregate reply path. Workflows receive aggregate events through the existing
  correlation-topic publish.

### Actor

One persistent actor per workflow instance, built like sagas with `propsPersist` through
`entityFactoryFor`. Each workflow definition has its own shard region, named by the workflow name.

- The entity ID is the workflow ID the caller supplies. An ID that contains `~` is refused, because
  core treats a name that contains `~Saga~` as a saga and uses `~` as a topic separator.
- Remember-entities is on. An in-flight workflow restarts with the process and does not wait for a
  message.
- A running workflow does not passivate. A passivated entity leaves the remembered set, and its
  timers would then not fire. The cost is one actor in memory per running workflow.
- A workflow that reached a final record stops. A later `Status` or `Result` call recovers it, answers
  from the journal, and stops it again.

### The workflow body is a description

`workflow { }` builds a value: either a finished result, or a step with a continuation. The actor
interprets that value. Every `let!` passes through the actor, so the actor decides for each step
whether to run it or to feed in a stored result. F# needs no custom task scheduler for this.

### Step paths

A step is identified by its path, not by a single counter. The body's top-level steps are `0`, `1`,
`2`. A parallel block at `3` gives its branches `3/0` and `3/1`, and the first step inside branch 1 is
`3/1/0`. An inline workflow nests its steps under the path of the `let!` that called it.

Paths make replay independent of the order in which parallel results arrived.

### Journal records

| Record | Stored when |
|---|---|
| `Started { Input; At; Traceparent; Parent }` | The start is accepted. Always sequence number 1 |
| `StepScheduled { Path; Name; Kind; At; Detail }` | A timer, wait, activity, command, or child step begins |
| `StepCompleted { Path; Name; Kind; Result; At }` | A step produced a value |
| `StepFailed { Path; Name; Kind; Error; At }` | A step ended without a value |
| `SignalReceived { Name; Payload; At; Traceparent }` | A signal arrives from outside |
| `CancelRequested { At }` | `Cancel` is accepted |
| `Finished { Result; At }` | The body returned |
| `Faulted { Error; At }` | The body threw an exception it did not handle |
| `Cancelled { At }` | The body ended after a cancel request |

`StepScheduled` stores the instant from which a deadline is measured. For a timer, `Detail` holds the
due time. For a child, it holds the child's ID.

`Error` holds a kind (`Exhausted`, `NotRetryable`, `DeadlineExceeded`, `OutcomeUnknown`,
`ChildFaulted`, `Cancelled`), the exception type name, and its message. It does not hold a stack
trace.

Workflows take no snapshots. A workflow's position is a continuation, which cannot be stored, so
recovery replays the whole journal. A workflow that loops for a long time should hand over to a
detached child and return, which starts a new journal.

### Replay

On recovery the actor reads every record into a table keyed by path, then runs the body from the top.
At each step:

1. If the table holds `StepCompleted` for the path, the actor checks the name and kind, feeds the
   result to the continuation, and runs nothing.
2. If it holds `StepFailed`, the actor checks the name and kind and raises
   `WorkflowStepFailedException` in the body.
3. If it holds only `StepScheduled`, the step was in progress. The actor resumes it using the stored
   start time.
4. If it holds nothing, the actor stores `StepScheduled` where the step kind needs it and runs the
   step.

A live step that fails raises the same `WorkflowStepFailedException` that replay raises, never the
original exception. The body therefore takes the same branch in both cases.

### Start

A start message stores `Started` and replies. A second start with the same ID finds sequence number
1, stores nothing, and replies with the instance's current status.

## Step kinds

### Activity

An activity is a function that calls an external system. It runs off the actor, on the thread pool,
and its outcome returns to the actor as an in-memory message marked
`INoSerializationVerificationNeeded`.

- It runs at least once. A crash after the external call and before `StepCompleted` is stored repeats
  the call.
- It receives a context with a stable idempotency key (workflow name, workflow ID, and step path), a
  cancellation token, and the attempt number. The external system has to honour the key.
- The framework catches every exception the function throws and applies the retry policy. This
  differs from `RunAsync`, where an escaping exception terminates the process.
- The result type must be serializable and should have a journal name.

### Retry policy

| Part | Meaning |
|---|---|
| Schedule | Core's `RetryEvery`: `FixedInterval` or `Backoff` with jitter |
| Limit | Maximum attempts, an overall deadline, or both |
| Attempt timeout | Cancels one attempt that does not return |
| Retry filter | A function from exception to bool. `false` fails the step at once as `NotRetryable` |

`Retry.forever`, `Retry.times n`, and `Retry.none` cover common cases. A policy with no attempt limit
requires a deadline.

Attempts are not stored. After a crash the attempt count and the backoff start again. The deadline
is measured from the stored `StepScheduled` time and does not move.

Polly remains usable inside an activity for short in-call resilience. The workflow sees that whole
call as one attempt.

### Timer

`sleep duration` and `until instant` store the due time in `StepScheduled`. Recovery arms the timer
again with the remaining time, so a restart cannot postpone it. The current time comes from the
scheduler, so `ShiftedScheduler` moves it in tests.

### Stored values

`now`, `newGuid`, and `random` store their value as a completed step. Reading `DateTime.Now`,
`Guid.NewGuid`, or `Random` directly in the body gives a different value on replay, and the replay
check cannot detect it.

### Signal

`waitSignal<'T> name` waits for a named message from outside. `waitSignalFor<'T> name duration`
returns `None` when the duration passes first.

- `Signal id name payload` stores `SignalReceived` before it replies. A signal that arrives before the
  body reaches its wait is kept.
- A wait consumes the oldest unconsumed signal with its name. `StepCompleted` for the wait stores the
  sequence number of the signal it consumed, so replay assigns the same signal to the same wait.
- A signal to an instance that has not started or has reached a final record is refused.

### Aggregate command

`send factory aggregateId command` sends a command and completes with the aggregate's reply event.

- The workflow subscribes to the aggregate's correlation topic and sends after the subscription is
  acknowledged. Subscribing after sending could miss the event.
- Each command step uses its own correlation ID and command ID, both derived from the workflow name,
  workflow ID, and step path. Parallel commands to one aggregate therefore do not receive each
  other's events. The command carries the step's trace context in the `traceparent` metadata key.
- The subscription is in memory. On recovery the step subscribes and sends again with the same IDs.
  The aggregate must answer a repeated command with the same verdict, which is the rule sagas already
  impose on commands they issue.
- A persisted reply event can still start a saga. A workflow does not change that.
- The step has a resend schedule and a deadline. A deadline that passes with no reply is stored as
  `OutcomeUnknown`. It does not mean the command was rejected.

### Child workflow

- `startChild definition input` stores the child's ID, starts the child, and returns a handle once the
  child's `Started` is stored.
- `awaitChild handle` completes with the child's result, or fails with `ChildFaulted`.
- `child definition input` is `startChild` followed by `awaitChild`.
- The child's ID is derived from the parent's ID and the step path. A repeated `startChild` reaches
  the same instance and does not start a second one.
- The child stores its parent in `Started` and tells the parent when it reaches a final record. On
  recovery, `awaitChild` asks the child for its status again, and a finished child answers from its
  journal.
- A child that is never awaited is detached and outlives its parent.

### Inline workflow

`let! x = other input` runs another workflow's body inside the caller. Its steps go into the caller's
journal under a nested path.

## Parallelism

- `all [a; b; c]`, and `and!` in F#, run branches together and complete with every result.
- `race [a; b]` completes with the first branch to finish and cancels the rest. The winning branch is
  stored, so replay selects the same one.
- `allAtMost n items f` is a fan-out with a concurrency limit.

Branches interleave inside one actor. External calls run off the actor, so their I/O is concurrent.
Workflow logic is not multi-threaded.

Every branch result is stored in one journal. A fan-out over thousands of items makes recovery slow.
Use one child workflow per item or per chunk for that.

## Telemetry

### Tracing

- A new `FCQRS.Workflow` activity source.
- `Started` stores the caller's trace context. Spans emitted after a restart use it as their parent,
  so a resumed workflow continues the same trace.
- Each executed step emits one span named after the step. Each activity attempt is a child span with
  its attempt number.
- A replayed step emits no span.
- No span stays open across a wait. A timer, signal, or child wait emits a short span when it
  completes, with the waited duration as an attribute.
- An activity runs with its attempt span as `Activity.Current`.
- An awaited child joins the parent's trace. A detached child starts its own trace with a link to the
  parent step. A signal's sender context is recorded as a span link.

### Metrics

A `FCQRS.Workflow` meter. Core has no meters today.

| Instrument | Purpose |
|---|---|
| Started, finished, faulted, and cancelled counters | Throughput and failure rate per workflow name |
| Running and parked workflow counts | Current load, and instances that need a code fix |
| Step duration histogram | Slow external systems |
| Activity retry counter | Unreliable external systems |
| Recovery replay duration and journal length | Workflows whose history has grown too long |

### Attributes and logs

Spans and log scopes carry the workflow name, workflow ID, step path, and step name. Inputs, results,
and signal payloads are never written to attributes.

## API

All names are proposals.

### F#

```fsharp
let enrichOrder (risk: IRiskApi) (accounts: AccountFactory) =
    Workflow.define "enrich-order" (fun (order: Order) -> workflow {
        let! score =
            activity "score" Retry.forever (fun ctx ->
                risk.Score(ctx.IdempotencyKey, order, ctx.Cancellation))
        let! answer = waitSignalFor<Answer> "answer" (TimeSpan.FromDays 2.)
        let! debited = send accounts order.AccountId (Debit order.Amount)
        let! _ = startChild notifyCustomer order
        return decide score answer debited
    })

let orders = Fcqrs.workflow api (enrichOrder risk accounts)
```

### C#

```csharp
public sealed class EnrichOrder(IRiskApi risk) : Workflow<Order, Decision>("enrich-order")
{
    public override async WorkflowTask<Decision> Run(IWorkflowContext ctx, Order order)
    {
        var score = await ctx.Activity("score", Retry.Forever,
            a => risk.Score(a.IdempotencyKey, order, a.Cancellation));
        var answer = await ctx.WaitSignal<Answer>("answer", TimeSpan.FromDays(2));
        var debited = await ctx.Send(accounts, order.AccountId, new Debit(order.Amount));
        await ctx.StartChild<NotifyCustomer, Order>(order);
        return Decide(score, answer, debited);
    }
}
```

`WorkflowTask<T>` has its own async method builder. Awaiting anything that is not a workflow step,
such as `Task.Delay` or a direct `HttpClient` call, throws at that await. A determinism mistake then
fails on its first run.

Hosting: `builder.AddWorkflow<EnrichOrder>()`, with `IWorkflowClient<Order, Decision>` injected.

Both languages produce the same journal records for the same sequence of steps.

### Outside access

| Call | Behaviour |
|---|---|
| `Start id input` | Returns once `Started` is stored, or reports the existing instance |
| `Signal id name payload` | Returns once the signal is stored |
| `Status id` | Not started, running with its pending steps, parked, finished, faulted, or cancelled |
| `Result id` | Waits for the final record. The wait is in memory. The workflow continues if the caller stops waiting |
| `Cancel id` | Returns once `CancelRequested` is stored |

### Persistent contracts

The workflow name, step names, step kinds, and the input, result, step-result, and signal types are
stored. Types are registered through `Fcqrs.journalTypes`.

## Akka.Streams

- `WorkflowFlow.start handle idOf parallelism` starts one workflow per element and applies
  backpressure until the start is stored.
- `WorkflowFlow.run handle idOf parallelism` starts a workflow per element and emits its outcome.
  `parallelism` limits how many run at once.
- The workflow ID must be computed from the element. A restarted stream then reaches the existing
  instances and does not start duplicates.
- The stream is not durable. If the process stops, the workflows resume and the stream does not. The
  upstream source must replay from its own offset, for example by committing only after the outcome
  is emitted.
- A stream inside a workflow runs as an activity, to completion. A long stream must store its own
  progress, or be divided into child workflows.

## Changing workflow code

During replay, each stored record's step name and kind must match what the body requests at that
path.

- Safe: changing an activity's implementation, changing steps no running instance has reached, and
  changing a workflow that has no running instances.
- Not safe: inserting, removing, reordering, or renaming steps a running instance has passed.
- `version "add-fraud-check" 2` stores the version an instance first reached. Old instances replay the
  old branch, and new instances take the new one.
- For a large change, register the new code under a new workflow name and keep the old definition
  until its instances finish.
- Stored types follow the event rules: stable journal names, and an upcast when a payload changes.

### A mismatch parks the instance

A mismatched instance runs nothing further. It logs an error, reports `Parked` from `Status`, and
counts in the parked metric. It stores nothing, so it resumes when code that matches its journal is
deployed.

This differs from the process-crash rule for integrity errors, on purpose. Remember-entities
restarts every running workflow at startup. A crash on mismatch would repeat at every start and stop
every other workflow with it.

An unreadable journal record and a failed journal write still terminate the process, as in core.

## Cancellation and failure

- `Cancel id` stores `CancelRequested`. Running activities have their token cancelled, pending waits
  end, and the body receives a cancellation exception at each pending step.
- Cleanup in `try ... finally` runs as ordinary journaled steps. The instance then stores
  `Cancelled`.
- Awaited children that are still running are cancelled with the parent. Detached children are not.
- An exception the body does not handle stores `Faulted`. That record is final, and nothing restarts
  the instance.
- A step result that cannot be serialized faults the instance.

## Testing

In `test/Facade.Tests/`, with `serialize-messages` on:

- stop and recover the actor after every stored record; the result is the same and no stored step
  runs twice;
- parallel branches completing in every order, and `race` selecting the stored winner on replay;
- a signal that arrives before its wait, and a timer across a restart with `ShiftedScheduler`;
- retry schedule, attempt timeout, retry filter, and a deadline that holds across a restart;
- a re-sent aggregate command receiving the same verdict, and `OutcomeUnknown` at the deadline;
- child re-attachment, a child fault raised in the parent, and cancellation reaching awaited children;
- parking on a mismatch, and resuming with matching code;
- the C# foreign-await error, and equal journal records from equivalent F# and C# workflows;
- a process-kill test in the style of `BankInvariantTests`: the transfer written as a workflow, with
  the total balance unchanged, on SQLite and on PostgreSQL when `FCQRS_TEST_POSTGRES` is set.

## Documentation

- A concept page: the workflow model, its guarantees and limits, and when to use a saga or a workflow.
- A how-to page for a workflow that calls an external system.
- F# and C# samples under `samples/accounts/`.
- A workflow section in the observability how-to and in the configuration reference.
- XML comments on the public API.

## Delivery

One spec, five implementation plans, in this order:

1. Package, actor, journal records and manifest tags, replay, the F# builder, direct start, activity
   with retry, timers, stored values, `Status` and `Result`, and tracing.
2. Signals, aggregate commands, `all`, `race`, and `allAtMost`.
3. Child workflows, cancellation, the `version` step, and parking.
4. The C# API and hosting registration.
5. Akka.Streams flows, metrics, documentation, samples, and the process-kill test.

## To confirm during planning

- The exact derivation of a step's correlation ID, checked against the `CID` validation in
  `FCQRS.Model` and against `sameTrace`.
- Whether `FCQRS.Workflows` versions with core or independently.

## As built (2026-11-11)

The implementation differs from the design above in these points. The code and the Workflows
concept page are authoritative.

- **Journal records** are one non-generic `WorkflowRecord` union whose payloads carry a type name and
  JSON. Core's manifest tag list is unchanged; core exposes its internals to `FCQRS.Workflows`.
- **Inline workflows** share their caller's step sequence. Only parallel branches have nested paths.
- **`ActivityContext` is `AttemptContext`**, to avoid `System.Diagnostics.ActivityContext`.
- **A signal wait stores the signal's payload**, not its sequence number. Replay assigns signals to
  waits by the rule "oldest stored signal with that name", in journal order.
- **A command resend** carries the same correlation ID and a new command ID. `send` takes an
  aggregate handle and gives the reply's event details.
- **`awaitChild` asks the child for its result** and asks again after a timeout or a restart. The
  child does not notify its parent. Every child joins its parent's trace.
- **A child started but not yet awaited** keeps running when its parent is cancelled.
- **Cancellation** is one write: the request and a `Cancelled` failure for every pending step. F#
  cleanup uses `try ... with`, because `let!` is not allowed in `finally`. C# cleanup can `await` in
  `finally`.
- **`version`** is stored beside the step sequence under `<next path>@<change id>`.
- **C#** uses `async` methods with a custom method builder instead of an explicit context-only API.
  Awaiting an unfinished non-step throws. An already finished task is not detected.
- **The C# registration class** is `WorkflowHost`.
- **Samples** are under `samples/workflows/` on .NET 10 with project references, because the package
  is not published.
- **Not built:** a check that a body which drops trailing steps diverges; a test that F# and C#
  write byte-equal journals.
