# FCQRS.Workflows Part 1 (Core) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A workflow written as `workflow { }` starts by ID, runs activities with retry, timers and stored values, survives a process restart without recomputing a stored step, and reports status and result.

**Architecture:** `workflow { }` builds a description (`Workflow<'T>`). A pure `Machine` interprets it against a table of journal records keyed by step path and emits the records to store and the steps to start. A persistent sharded actor stores those records, runs activities off the actor, and feeds each stored outcome back to the machine.

**Tech Stack:** F# on net10.0, Akkling persistence and cluster sharding (through core's internal helpers), FSharp.SystemTextJson through `FCQRS.Serialization`, Expecto.

**Spec:** `design/specs/2026-11-10-fcqrs-workflows-design.md`

## Global Constraints

- Target `net10.0`. No Temporal, Polly, or OpenTelemetry package reference.
- A stored step result is never recomputed.
- Every message that can cross nodes implements `FCQRS.Common.ISerializable`; a message that carries a function, task result, or exception implements `INoSerializationVerificationNeeded`.
- Time comes from `system.Scheduler.Now`, so `ShiftedScheduler` moves it.
- A workflow ID that contains `~` is refused.
- A failed step raises `WorkflowStepFailedException` in the body, live and on replay.
- Inputs, results, and payloads never go into span attributes.

## Deviations from the spec, decided while reading core

- Journal records are one non-generic union, `WorkflowRecord`, whose payloads are `{ Type; Json }`. Core's generic manifest tag list is therefore unchanged. `WorkflowRecord` is registered with `JournalTypes` as `fcqrs.workflow.record`.
- Core gains `InternalsVisibleTo("FCQRS.Workflows")`, because the sharding helpers, `StopEntity`, and `JournalTypes.TryGetName` are internal.
- An inline workflow shares its caller's step sequence. It does not get a nested path. Only parallel branches nest.
- F# does not allow `let!` in `finally`, so cleanup steps go in `try ... with`. This matters in part 3.

## Review Focus

1. A step result that does not survive a JSON round trip: the body must see the round-tripped value live, the same value replay will give it.
2. A history thousands of steps long: replay must not overflow the stack.
3. An activity that ignores its cancellation token and never returns: the attempt timeout must still move the step on.
4. An attempt outcome that arrives after its step already ended (deadline, restart): it must be ignored.
5. `Start` racing with a second `Start` for the same ID: one stores `Started`, the other gets `AlreadyStarted`.

---

## File structure

| File | Responsibility |
|---|---|
| `src/FCQRS/AssemblyInfo.fs` | `InternalsVisibleTo("FCQRS.Workflows")` |
| `src/FCQRS/Common.fs` | `Telemetry.WorkflowActivitySourceName`, added to `AllActivitySources` |
| `src/FCQRS.Workflows/Journal.fs` | `Payload`, `StepKind`, `StepError`, `WorkflowRecord`, payload codec |
| `src/FCQRS.Workflows/Workflow.fs` | `Workflow<'T>`, operations, `RetryPolicy`, the `workflow` builder, step functions |
| `src/FCQRS.Workflows/Machine.fs` | The pure interpreter |
| `src/FCQRS.Workflows/Messages.fs` | Actor protocol and public status types |
| `src/FCQRS.Workflows/WorkflowActor.fs` | The persistent actor: storing, activity execution, retry, timers, tracing |
| `src/FCQRS.Workflows/Api.fs` | `Workflow.define`, `Workflows.register`, `WorkflowHandle` |
| `test/Facade.Tests/WorkflowMachineTests.fs` | Pure replay tests |
| `test/Facade.Tests/WorkflowTests.fs` | Actor tests, including process restart |

## Task 1: Project, journal records, payload codec

**Files:** create `src/FCQRS/AssemblyInfo.fs`, `src/FCQRS.Workflows/FCQRS.Workflows.fsproj`, `paket.references`, `Journal.fs`; modify `src/FCQRS/FCQRS.fsproj`, `src/FCQRS/Common.fs`, `FCQRS.sln`, `test/Facade.Tests/Facade.Tests.fsproj`; test `test/Facade.Tests/WorkflowMachineTests.fs`.

**Produces:**

```fsharp
namespace FCQRS.Workflows
type Payload = { Type: string; Json: string }
type StepKind = Activity | Timer | Value | Signal | Command | Child | Race | Version
type ErrorKind = Exhausted | NotRetryable | DeadlineExceeded | OutcomeUnknown | ChildFaulted | Cancelled | BodyFailed
type StepError = { Kind: ErrorKind; ExceptionType: string; Message: string }
type WorkflowRecord =
    | Started of input: Payload * at: DateTime * traceparent: string option * parent: string option
    | StepScheduled of path: string * name: string * kind: StepKind * at: DateTime * detail: string option
    | StepCompleted of path: string * name: string * kind: StepKind * result: Payload * at: DateTime
    | StepFailed of path: string * name: string * kind: StepKind * error: StepError * at: DateTime
    | SignalReceived of name: string * payload: Payload * at: DateTime * traceparent: string option
    | CancelRequested of at: DateTime
    | Finished of result: Payload * at: DateTime
    | Faulted of error: StepError * at: DateTime
    | Cancelled of at: DateTime
    interface FCQRS.Common.ISerializable
exception WorkflowStepFailedException of path: string * name: string * error: StepError
module Payload =
    val encode: Type -> obj -> Payload      // unit encodes as { Type = "unit"; Json = "null" }
    val decode: Type -> Payload -> obj
```

- [ ] Test: `Payload.decode t (Payload.encode t v) = v` for `int`, `string`, `unit`, an F# record, an F# union, `DateTime`, and `option`; `WorkflowRecord` survives `Serialization.encodeToBytes` and `decodeFromBytes`.
- [ ] Run, see it fail to compile.
- [ ] Implement. `Payload.Type` is the `JournalTypes` name when registered, else the type's full name.
- [ ] `dotnet build FCQRS.sln`, run the tests, commit.

## Task 2: The builder and the machine

**Files:** create `Workflow.fs`, `Machine.fs`; test `WorkflowMachineTests.fs`.

**Produces:**

```fsharp
type RetryPolicy =
    { Schedule: FCQRS.Common.RetrySchedule; MaxAttempts: int option; Deadline: TimeSpan option
      AttemptTimeout: TimeSpan option; ShouldRetry: exn -> bool }
module Retry =
    val none: RetryPolicy                      // one attempt
    val times: int -> RetryPolicy              // backoff 1s x2 up to 1min
    val forever: TimeSpan -> RetryPolicy       // backoff, no attempt limit, required deadline
type ActivityContext = { IdempotencyKey: string; Attempt: int; Cancellation: CancellationToken }
type Workflow<'T>                              // internal cases: Return | Fail | Lazy | Op
val workflow: WorkflowBuilder                  // Bind, Return, ReturnFrom, Zero, Combine, Delay, Run,
                                               // TryWith, TryFinally (unit -> unit), While, For, Using
val activity: name: string -> RetryPolicy -> (ActivityContext -> Task<'T>) -> Workflow<'T>
val sleep: TimeSpan -> Workflow<unit>
val until: DateTime -> Workflow<unit>
val now: Workflow<DateTime>
val newGuid: Workflow<Guid>
val random: Workflow<float>

// Machine.fs, internal
type StepState =
    | Scheduled of name: string * kind: StepKind * at: DateTime * detail: string option
    | Completed of name: string * kind: StepKind * result: Payload
    | Failed of name: string * kind: StepKind * error: StepError
type Emit = Store of WorkflowRecord | Begin of path: string * LeafOp * scheduledAt: DateTime
exception WorkflowDivergedException of path: string * expected: string * found: string
type Machine(history: IDictionary<string, StepState>, clock: unit -> DateTime) =
    member Run: Workflow<obj> -> Emit list            // from the top; replays, then stops at pending steps
    member Apply: WorkflowRecord -> Emit list         // a record stored live
    member Pending: IReadOnlyDictionary<string, PendingStep>
    member Outcome: Result<obj, exn> option
```

Rules the tests pin:

- Top-level paths are `"0"`, `"1"`, ... in the order the body reaches them.
- A leaf step with no history emits `Store(StepScheduled ...)`; when that record is applied it emits `Begin`. A `Scheduled` step found during `Run` emits `Begin` with the stored time. Stored-value steps emit `Store(StepCompleted ...)` directly.
- A step resumes only when its `StepCompleted` or `StepFailed` is applied, with the payload decoded from the record.
- A name or kind that differs from the stored one raises `WorkflowDivergedException`.
- Continuations run from a work queue, not by recursion.
- When the body returns, the machine emits `Store(Finished ...)`; an unhandled exception emits `Store(Faulted ...)` with kind `BodyFailed`.

- [ ] Tests: a three-step body run live record by record; the same body replayed from every prefix of its history gives the same pending set and never emits `Begin` for a completed path; `try ... with` catches a failed step live and on replay; a renamed step diverges; a 20,000-step loop replays; `now` returns the stored value on replay.
- [ ] Run, fail, implement, pass, commit.

## Task 3: The actor, start, status, result

**Files:** create `Messages.fs`, `WorkflowActor.fs`, `Api.fs`; test `WorkflowTests.fs`.

**Produces:**

```fsharp
type WorkflowStatus =
    | NotStarted | Running of pending: string list | Parked of reason: string
    | Completed | Failed of StepError | Cancelled
type StartResult = Started | AlreadyStarted of WorkflowStatus
type WorkflowOutcome<'R> = Finished of 'R | Faulted of StepError | Cancelled
type WorkflowDefinition<'I, 'R> = { Name: string; Body: 'I -> Workflow<'R> }
type WorkflowHandle<'I, 'R> =
    { Name: string
      Start: string -> 'I -> Async<StartResult>
      Status: string -> Async<WorkflowStatus>
      Result: string -> TimeSpan -> Async<WorkflowOutcome<'R>> }
module Workflow =
    val define: name: string -> ('I -> Workflow<'R>) -> WorkflowDefinition<'I, 'R>
module Workflows =
    val register: FCQRS.Common.IActor -> WorkflowDefinition<'I, 'R> -> WorkflowHandle<'I, 'R>
```

- The shard region is named by the definition name, with remember-entities on and passivation `Never`.
- A final instance, and one that was never started, passivates after it answers a message.
- `Start` puts `Activity.Current`'s traceparent into the message; it is stored in `Started`.

- [ ] Tests: a workflow of stored values finishes and `Result` returns its value; a second `Start` returns `AlreadyStarted`; `Status` of an unknown ID is `NotStarted`; an ID containing `~` raises `ArgumentException`; a body that throws gives `Faulted` with kind `BodyFailed`.
- [ ] Run, fail, implement, pass, commit.

## Task 4: Activities and retry

**Files:** modify `WorkflowActor.fs`; test `WorkflowTests.fs`.

- An attempt runs on the thread pool. Its outcome returns as an in-memory message that names the path and attempt number; an outcome for a path that is no longer pending, or for an old attempt, is ignored.
- The idempotency key is `"<workflow name>/<workflow id>/<path>"`.
- `AttemptTimeout` uses `Task.WhenAny` with a delay, and also cancels the token.
- The retry delay uses the schedule with core's 1.0 to 1.25 jitter. The deadline is measured from the stored `StepScheduled` time.

- [ ] Tests: an activity that fails twice then succeeds completes on attempt 3; `Retry.times 2` on an always-failing activity raises `WorkflowStepFailedException` with `Exhausted`; a retry filter returning `false` gives `NotRetryable` after one attempt; an activity that ignores its token ends as `DeadlineExceeded`; the context carries the expected idempotency key.
- [ ] Run, fail, implement, pass, commit.

## Task 5: Timers and restart

**Files:** modify `WorkflowActor.fs`; test `WorkflowTests.fs`.

- [ ] Tests, each stopping the actor system and starting a new one on the same SQLite file: a workflow blocked in its second activity resumes without any message, runs the second activity again, and does not run the first again; a `sleep` of one day completes after the restart when `ShiftedScheduler.Shift` is moved a day forward; a finished workflow answers `Result` from its journal.
- [ ] Run, fail, implement, pass, commit.

## Task 6: Tracing

**Files:** modify `WorkflowActor.fs`; test `WorkflowTests.fs`.

- Source `FCQRS.Workflow`. Span names: `Workflow.Start:<name>`, `Step:<step name>`, `Attempt:<step name>`, `Workflow.End:<name>`.
- Tags: `fcqrs.workflow.name`, `fcqrs.workflow.id`, `fcqrs.workflow.step.path`, `fcqrs.workflow.step.name`, `fcqrs.workflow.attempt`, `fcqrs.workflow.waited_ms`.

- [ ] Tests with an `ActivityListener`: every span of a run has the trace ID of the `Start` caller; an attempt span is `Activity.Current` inside the activity; a restart emits no span for a step completed before it.
- [ ] Run, fail, implement, pass, run the whole suite, commit.
