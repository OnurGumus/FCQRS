# FCQRS.Workflows Part 2 (Signals, Commands, Parallelism) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A workflow waits for named signals, sends commands to aggregates and receives their reply events, and runs branches in parallel with `all`, `race`, and `allAtMost`.

**Architecture:** A step becomes a node: a leaf, a set of branches that all finish, or a set of branches of which the first to finish wins. The machine runs each branch as a fiber with its own path prefix. A command step reuses the activity attempt machinery, with core's command subscription as the call.

**Tech Stack:** As part 1.

**Spec:** `design/specs/2026-11-10-fcqrs-workflows-design.md`

## Global Constraints

As part 1, and:

- A branch's steps have paths `<block path>/<branch index>/<n>`, so replay does not depend on the order in which parallel results arrived.
- A race stores its winner. Replay runs only the stored winner.
- A signal is stored before it is acknowledged.
- A command step subscribes before it sends, and uses one correlation ID for every resend.

## Deviations from the spec

- A command step's resend carries the same correlation ID and a new command ID. Core's `CreateCommandSubscription` creates the command ID, and the aggregate's repeat-verdict rule is a domain rule either way.
- `send` takes an `AggregateHandle` and returns the reply's event details.
- A signal wait's `StepCompleted` holds the signal's payload. Replay finds which signal a wait consumed by the rule "oldest stored signal with that name", applied in journal order, so no sequence number is stored.

## Review Focus

1. Two parallel branches that fail close together: `all` must fail once, and the other branch's late outcome must be ignored.
2. A race whose winner was decided but not stored when the process stopped: replay must decide again without contradiction.
3. A signal that arrives while its wait's timeout record is being stored: the signal must stay for the next wait.
4. `allAtMost` over a list longer than the limit, replayed from any prefix: no branch may start that the first run had not started.
5. A command whose reply is lost: the step must end as `OutcomeUnknown` at its deadline, not hang.

---

## Task 1: Nodes and fibers in the machine

**Files:** `Workflow.fs`, `Machine.fs`, `WorkflowMachineTests.fs`.

**Produces:**

```fsharp
val all: Workflow<'T> list -> Workflow<'T list>
val race: Workflow<'T> list -> Workflow<'T>            // the first branch to finish, value or exception
val allAtMost: limit: int -> items: 'A list -> ('A -> Workflow<'T>) -> Workflow<'T list>
member WorkflowBuilder.MergeSources: Workflow<'A> * Workflow<'B> -> Workflow<'A * 'B>   // and!
type Emit = Store of WorkflowRecord | Begin of PendingStep | Abandon of path: string
type History() = member Steps; member Signals; member Apply: WorkflowRecord -> unit
```

- [ ] Tests: `all` of three activities completed in every one of the six orders gives the results in branch order; replay from every prefix of each journal reaches the same result and begins no stored step; a failing branch fails `all` once and abandons the others; `race` stores `StepCompleted(path, "race", Race, index)` and replay runs only the winner; `allAtMost 2` over five items never has more than two begun and unfinished; `and!` works.
- [ ] Fail, implement, pass, commit.

## Task 2: Signals

**Files:** `Workflow.fs`, `Machine.fs`, `Messages.fs`, `WorkflowActor.fs`, `Api.fs`, tests.

**Produces:**

```fsharp
val waitSignal<'T> : name: string -> Workflow<'T>
val waitSignalFor<'T> : name: string -> TimeSpan -> Workflow<'T option>
WorkflowHandle.Signal: string -> string -> Payload -> Async<unit>      // id, signal name, payload
val Workflows.signal: WorkflowHandle<_, _> -> id: string -> name: string -> 'S -> Async<unit>
```

- [ ] Tests: a signal sent before the wait is consumed by it; two signals with one name are consumed oldest first; `waitSignalFor` returns `None` at its timeout and leaves a later signal for the next wait; a signal to an unknown or finished workflow raises `InvalidOperationException`; a stored, unconsumed signal is still there after a restart.
- [ ] Fail, implement, pass, commit.

## Task 3: Aggregate commands

**Files:** `Workflow.fs`, `WorkflowActor.fs`, `Api.fs`, tests.

**Produces:**

```fsharp
val send: AggregateHandle<'Command, 'Event> -> aggregateId: string -> 'Command -> Workflow<'Event>
val sendWith: RetryPolicy -> AggregateHandle<'Command, 'Event> -> aggregateId: string -> 'Command -> Workflow<'Event>
```

- The default policy resends on `TimeoutException` with backoff and has a five-minute deadline. Any other exception fails the step as `NotRetryable`. Running out of attempts or time is `OutcomeUnknown`.
- The correlation ID is a GUID made from the SHA-256 hash of the idempotency key.

- [ ] Tests, using step 5's `Account`: a workflow deposits and withdraws and returns the events; two parallel commands to one account each receive their own reply; a stored command is not sent again after a restart; a command to an aggregate that never replies ends as `OutcomeUnknown`.
- [ ] Fail, implement, pass, run the Release suite, commit.
