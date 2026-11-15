# Continue-as-new for workflows: design

Status: approved by the owner on 2026-11-15, who accepted all nine recommendations listed under
"Decisions that need the owner" below (storage in one journal with a snapshot at each run boundary;
run-prefixed step paths from run 2; a typed `ContinueAsNew | Complete` ending; unconsumed signals carried
over; cancel wins over continue; an unreadable input parks; a new trace per run linked to the last; one
release, rollback unsupported once an instance has continued; a run that continues without storing a step
faults). Build after the child-workflow fixes and the monthly schedule rules have landed, because all
three change Machine.fs, WorkflowActor.fs and Api.fs. Verify first: how Akka.NET 1.5.59 treats a snapshot
it cannot read, and the SQL plugin's delete behaviour.

The proposal follows as drafted.


Status: draft for the owner, 2026-11-15. Line numbers refer to the working tree on that date, which
other agents were editing (`WorkflowActor.fs`, `Api.fs`, `Children.fs`, `Messages.fs`), so a few may
have moved.

## Summary of the recommended design

A workflow that may continue as new is defined with `Workflow.defineContinuing`, whose body returns
`RunEnd<'Input, 'Result>`: either `ContinueAsNew nextInput` or `Complete result`. The C# equivalent
is a `ContinuingWorkflow<TInput, TResult>` base class with protected `ContinueAsNew(next)` and
`Complete(result)` helpers. Both are typed by the definition's own input, and both decide only at the
body's top level, so no `try`/`catch` and no parallel branch can intercept or half-perform the switch.
The instance keeps one persistence ID. The ending run stores one new record,
`WorkflowRecord.ContinuedAsNew(input, run, at, traceparent)`. That record ends run N and starts run
N+1 in the same write, so there is no moment where the old run has ended and the new one has not
started. Right after it is stored, the actor saves an Akka snapshot of the new run's starting state
(run number, input, trace, and the stored signals no wait consumed). This is the one point where a
workflow's state is serializable, because the next input carries all of it. Recovery loads that
snapshot and replays only the current run's records. Without the snapshot it replays the whole
journal and resets at each `ContinuedAsNew`, so the snapshot only shortens replay and never replaces
the journal. No rows are deleted, so projections over the journal and audit stay intact. Runs after
the first prefix their step paths with the run number (`2.0`, `2.3/1/0`). Idempotency keys, command
correlation and command IDs, and child IDs all derive from the path, so they become unique per run,
and run 1's formats stay unchanged for existing instances. Stored signals that run N did not consume
carry over to run N+1 in order. Signals and cancels that arrive during the switch are handled by the
new run, because the actor processes nothing else between storing the record and starting the new
run. `Result` callers and `awaitChild` parents keep waiting on the same actor and receive the final
run's outcome. `Status` reports the current run.

---

## 1. API shape and type safety

### What the code imposes

- `WorkflowDefinition<'Input, 'Result>` is a public record `{ Name; Body: 'Input -> Workflow<'Result> }`
  (`Api.fs:12-14`). Children (`Children.fs:60`), schedules (`Schedules.fs:461`), streams and the C#
  host all consume it.
- The C# `WorkflowContext` is not generic (`CSharp.fs:233`), so `ctx.ContinueAsNew(next)` cannot
  check `next` against the workflow's input type. The C# base class `Workflow<'TInput, 'TResult>`
  (`CSharp.fs:342-347`) does know `TInput`.
- The body's `try ... with` is ordinary data in the `Workflow` union (`Workflow.fs:183-187`). A
  terminal operation implemented as an exception can be caught by user code.

### Option A: a typed result at the top of the body (recommended)

```fsharp
type RunEnd<'Input, 'Result> =
    | ContinueAsNew of input: 'Input
    | Complete of result: 'Result

module Workflow =
    val defineContinuing :
        name: string -> body: ('Input -> Workflow<RunEnd<'Input, 'Result>>) -> WorkflowDefinition<'Input, 'Result>
```

The example the documentation would use: a subscription that bills monthly and takes a `cancel`
signal at any time.

```fsharp
type Billing = { Customer: string; MonthsPaid: int; NextDue: DateTime }

let subscription =
    Workflow.defineContinuing "subscription" (fun (billing: Billing) -> workflow {
        let! cancelled =
            race [ workflow { do! until billing.NextDue
                              return false }
                   workflow { do! waitSignal<unit> "cancel"
                              return true } ]
        if cancelled then
            return Complete billing.MonthsPaid
        else
            do! activity "charge" (Retry.forever (TimeSpan.FromDays 3.0)) (fun attempt ->
                    charge attempt.IdempotencyKey billing.Customer)
            return ContinueAsNew { billing with MonthsPaid = billing.MonthsPaid + 1
                                                NextDue = billing.NextDue.AddMonths 1 }
    })
```

```csharp
public sealed class Subscription() : ContinuingWorkflow<Billing, int>("subscription")
{
    public override async WorkflowTask<RunEnd<Billing, int>> Run(WorkflowContext ctx, Billing billing)
    {
        var cancelled = await ctx.Race(
            ctx.Run(async () => { await ctx.Until(billing.NextDue); return false; }),
            ctx.Run(async () => { await ctx.WaitSignal<Unit>("cancel"); return true; }));
        if (cancelled)
            return Complete(billing.MonthsPaid);
        await ctx.Activity("charge", Retry.forever(TimeSpan.FromDays(3)), attempt =>
            Charge(attempt.IdempotencyKey, billing.Customer));
        return ContinueAsNew(billing with { MonthsPaid = billing.MonthsPaid + 1,
                                            NextDue = billing.NextDue.AddMonths(1) });
    }
}
```

(`ctx.Run` in the C# sketch must return `WorkflowTask<bool>`; the exact lambda shape follows the
existing `Run` helper at `CSharp.fs:311`.)

- Type safety: `ContinueAsNew` takes the definition's `'Input`; the compiler rejects anything else.
- The decision can only be the body's return value. A helper deep in the body returns a value to the
  top, which keeps the switch visible. No branch of `all` or `race` can continue as new, so no rule is
  needed for steps still running in sibling branches.
- `defineContinuing` returns an ordinary `WorkflowDefinition<'Input, 'Result>`. Handles, `child`,
  `ScheduledWorkflow.ofHandle`, and `WorkflowFlow` work unchanged and see only `'Result`.
- Implementation: `defineContinuing` builds `Body = fun input -> bind (body input) (function
  Complete r -> Return r | ContinueAsNew next -> <terminal> (Payload.ofValue next))`. The terminal is
  created after all user code has run, so no user handler stands between it and the machine. Make the
  terminal an internal `Workflow` case (for example `Restart of workflow: string * input: Payload`)
  that `Continuation.Advance` (`Workflow.fs:155-161`) passes through without calling any handler,
  rather than a sentinel exception. The one way user code could meet it is by calling a continuing
  definition's `Body` inside another workflow's body. The machine then compares the workflow name in
  the terminal with the running definition's name and faults with a clear message ("start it as a
  child") on a mismatch.
- C#: `ContinuingWorkflow<TInput, TResult>` with `abstract Run: WorkflowContext * TInput ->
  WorkflowTask<RunEnd<TInput, TResult>>` and protected `ContinueAsNew(TInput)` / `Complete(TResult)`
  helpers that build the union, so C# never calls `RunEnd.NewContinueAsNew`. It needs overloads of
  `WorkflowHost.Define`, `WorkflowHost.Register` and `AddWorkflow` (`CSharp.fs:385-393, 537-559`).

Cost: a body that wants to continue from the middle of a loop has to be written as a recursive
function that returns `RunEnd`. Those who know Temporal will expect the switch to be a call that
works from anywhere.

### Option B: a terminal step callable anywhere (Temporal-style)

`return! continueAsNew next` (F#) or `return await ContinueAsNew(next)` (a protected method on the
C# base class, which knows `TInput`).

- F# type safety needs typed access to the definition's input. That means a recursive
  `let rec subscription = Workflow.define ... continueAsNew subscription next` (warning FS0040 and a
  runtime initialization check), or a capability parameter passed into the body. Both are clumsier
  than Option A.
- It must not be catchable, so it has to be a node that is never resumed. A C# `finally` or `using`
  around it then never runs, and an F# `use` would need special handling.
- It needs rules for a call inside a branch: all other branches are abandoned, in-flight activity
  results are dropped, and a child start in flight may leave an orphan. These match today's abandoned
  race branches, but each one has to be specified and tested.

### Option C: an untyped `continueAsNew<'Input>` with a runtime check

This is the easiest to write, and the type is checked only when run N+1 decodes its input. Reject it,
because it turns a compile-time error into a parked or faulted instance.

**Recommendation: Option A.** In Temporal, continue-as-new is an error the workflow function returns
(Go `workflow.NewContinueAsNewError`) or throws (TS, Java, .NET). In practice it is almost always used
at the top of a loop, which Option A covers with full type safety.

---

## 2. Storage model (the central decision)

### Facts that constrain it

- The persistence ID comes from the actor path, `{name}/{shard}/{entityId}`, and is fixed when the
  actor is constructed (`AkklingHelpers.fs:43-53`). One sharded entity means one persistence ID.
- Akka.Persistence always loads the latest snapshot first (default `Recovery()`), then replays
  journal rows with `sequence_number > snapshot.SequenceNr`. Akka.Persistence.Sql reads them through
  the `(persistence_id, sequence_number)` key. With no snapshot it reads every row of the persistence
  ID. Workflows set no recovery today, so every workflow recovery already queries the snapshot store
  and finds nothing. A snapshot therefore adds no query to recovery.
- The snapshot store is configured (`default.hocon:27-35`) and aggregates use it (`Actor.fs:529-536`).
  Messages that implement `ISerializable` serialize with STJ (`default.hocon:52`).
- Projections follow each persistence ID's sequence numbers and require contiguous history. Missing
  rows raise `JournalHistoryException` ("Retain or restore its journal history",
  `Projections.fs:105-108, 365, 413, 587`). A projection without an entity filter reads every event,
  workflow records included (`Projections.fs:199-203`). CLAUDE.md states that projections may read
  `WorkflowRecord`s.
- Schedules size their runs' persistence IDs as `<workflow>/default-shard/<scheduleId@due>`, which is
  exactly 50 characters besides the escaped names, under a 255-character limit
  (`Schedules.fs:1408-1462`).
- The instance holds its `Result` waiters (`WorkflowActor.fs:149`), pending signal and cancel senders
  (`159-160`), and timers in memory. Any design that moves a run to another actor must hand these
  over.

### Option A: one persistence ID, a boundary record, and a boundary snapshot (recommended)

Journal of `subscription/default-shard/alice`:

```
1 Started(input = {MonthsPaid=0}, ...)
2 StepCompleted(0/0/0 ...) ... 6 StepCompleted(1 charge)
7 ContinuedAsNew(input = {MonthsPaid=1}, run = 2, at, traceparent')   <- snapshot at seq 7
8 StepScheduled(2.0/0/0 until ...) ...
```

- New additive case: `ContinuedAsNew of input: Payload * run: int * at: DateTime * traceparent:
  string option`. It stores the run number although that could be counted, so projections and
  operators can read it without counting.
- Snapshot type (library-owned, a persistent contract like `WorkflowRecord`):
  `WorkflowRunStart = { Run: int; Input: Payload; At: DateTime; Traceparent: string option; Parent:
  string option; Signals: CarriedSignal list }`, where `CarriedSignal = { Name; Payload; Traceparent }`.
  It implements `ISerializable`.
- Live switch in `applyLive` (next to `WorkflowActor.fs:691-740`):
  1. `cleanup ()`, which should find nothing because Option A ends a run only when its body returns.
  2. Reset the history: clear `Steps` and `blocks`, and keep `Signals`. Clear `children` and
     `cancelledChildren`. Set `machine <- None`.
  3. Set `started <- Some(next, traceparent')` and `run <- N+1`.
  4. `SaveSnapshot` while `LastSequenceNr` is the boundary record's.
  5. Write the span or log line, then `runBody next`. The records it returns are persisted as usual.
     The actor stays alive throughout, so waiters, signal senders and the idle state are untouched.
- Recovery:
  - A `SnapshotOffer` sets `started`, `run`, and `history.Signals`.
  - A journal-only recovery reaches the `ContinuedAsNew` record in `applyRecovered`
    (`WorkflowActor.fs:767-777`) and `History.Apply` (`Machine.fs:76-93`) and performs the same
    reset. Replaying the same records always gives the same carried signals.
  - `RecoveryCompleted` (`852-881`) then works as today.
- Snapshot housekeeping:
  - On `SaveSnapshotSuccess`, delete older snapshots of the instance (`DeleteSnapshots` with
    `maxSequenceNr < S`), which keeps one row per continued instance.
  - On `SaveSnapshotFailure`, log a warning and continue. The journal is still complete, and the next
    recovery only replays more.
  - A snapshot that cannot be read is handled like `ReplayFailed` (`WorkflowActor.fs:1050-1052`): the
    instance stops and the process keeps running. This is the same deliberate exception to fail-fast
    that parking is: running instances restart with the process, so a crash would repeat at every
    start.
- Recovery cost: one snapshot read plus the current run's rows, independent of how many runs came
  before. Memory: `History.Steps` holds only the current run.
- Projections: unaffected. Sequence numbers stay contiguous, rebuilds work, and a projection sees one
  new case. A projection that tracks "current step" must reset on `ContinuedAsNew`.
- Schedule index and persistence-ID limits: unaffected.
- Atomicity: one record in one write. Akka's `Persist` stashes incoming messages until the handler
  has run (Akkling `PersistAll`, `WorkflowActor.fs:838-841`), so no request runs between "run N
  ended" and "run N+1 started".

### Option B: a new persistence ID per run, with a pointer

The run would be a separate sharded entity (for example `alice#2`). Because the persistence ID is
fixed by the actor path, the entity at `alice` has to become a router with its own small journal
(`Started`, `RunPointer 2`, ...).

- The switch writes two persistence IDs: the pointer, and the new run's `Started`. Those writes
  cannot be atomic, so a crash between them needs reconciliation. Starting the new run again works
  because a second start is idempotent, but it is one more recovery path.
- Every Start, Status, Result, Signal and Cancel request takes an extra hop through the router.
- Signals that arrive during the switch must be buffered in the router. Result waiters held by the
  old run's actor must be moved or answered with a "moved" reply that the asker follows.
  `awaitChild` and `handle.Result` would both need to follow it.
- The suffix lengthens persistence IDs, which breaks the 50-character assumption in
  `Schedules.fs:1417-1462` and shrinks the allowed schedule-ID length.
- Projections see one persistence ID per run, so read models keyed by workflow ID must strip the
  suffix.
- The pointer journal still grows by one record per run and eventually needs a snapshot anyway.
- Advantage: old runs' rows could be deleted per persistence ID without touching the live one, and
  recovery never reads them even without a snapshot.

### Option C: one persistence ID, deleting old rows at the boundary

`DeleteMessages(S)` after the switch.

- This breaks every unfiltered projection that has not yet read those rows, and every rebuild from
  the start (`JournalHistoryException`). It also destroys the audit trail. Disqualified as a default.
- Akka.Persistence.Sql behaviour, as I read it (verify before relying on it): with the default
  `delete-compatibility-mode = false`, a delete marks rows up to S as `deleted` and physically
  removes all of them except the highest, which keeps the sequence number. I confirmed that the
  setting and the `deleted` column exist in the 1.5.59 package. I have not read the DAO's delete code.
- It could later become an explicit opt-in retention setting for applications whose projections all
  filter out workflow entities. It is not part of this proposal.

### Option D: one persistence ID, no snapshot, discarding old records during replay

Recovery still reads and deserializes every row, so I/O grows without bound. This saves only machine
work and does not meet the goal.

### Comparison

| | A: one ID + snapshot | B: ID per run | C: delete rows | D: skip on replay |
|---|---|---|---|---|
| Recovery reads | snapshot + current run | current run | current run | whole journal |
| Switch atomic | yes, one record | no, two IDs | yes | yes |
| Waiters and signals across the switch | same actor, nothing to do | hand-over needed | same actor | same actor |
| Projections and rebuilds | unchanged | per-run IDs | break | unchanged |
| Schedule persistence-ID limits | unchanged | tightened | unchanged | unchanged |
| New persistent contracts | 1 record case, 1 snapshot type | pointer records, ID suffix format | 1 record case | 1 record case |
| Correct without its optimisation | yes, journal-only replay | n/a | n/a | yes |

**Recommendation: Option A.** It is the only option where the switch is one atomic write and nothing
in memory has to move. The snapshot is the standard Akka mechanism for starting replay at a sequence
number, and it is only an optimisation over a complete journal. That matches the existing guarantee
in CLAUDE.md: "Snapshots shorten replay. They do not replace or repair the journal."

---

## 3. Signals across the boundary

The current rules: a signal is stored before it is acknowledged (`WorkflowActor.fs:927-935,
741-743`). Stored signals that no wait has consumed sit in `History.Signals`, oldest first
(`Machine.fs:20-22`). A wait consumes the oldest signal with its name (`Machine.fs:67-73, 249-274`).
A `waitSignalFor` whose timeout passed leaves the signal stored (`Workflow.fs:366-368`).

### Options

1. **Carry over (recommended).** Every signal stored before the `ContinuedAsNew` record and not
   consumed by a wait of run N is in run N+1's `History.Signals` at its start, in the same order and
   ahead of signals stored later. The snapshot carries them, and journal-only replay reaches the same
   list.
2. **Drop.** Temporal's behaviour, as far as I know: signals buffered in the SDK and not drained by
   the code are lost on continue-as-new, and Temporal's guidance is to drain them before continuing.
   (The server does reject completing a workflow task while new signals arrived during it, so the SDK
   retries the task and the code can see them. I am fairly but not fully sure of the details.)
   Dropping would break FCQRS's meaning of an acknowledgement: the caller was told the signal was
   stored for this workflow ID.
3. **Carry only names the definition declares.** Not possible: FCQRS has no signal declarations.

### Precise rule (Option 1)

- A signal accepted before the switch record is persisted belongs to run N's history. If run N does
  not consume it, run N+1 receives it.
- A signal that arrives while the switch record is being persisted is stashed by Akka. It is
  processed after run N+1 has started (and after run N+1's first records are persisted, because that
  persist also stashes), so run N+1 stores and consumes it.
- No signal is lost and none is delivered twice: a consumed signal is removed by the `StepCompleted`
  of its wait (`Machine.fs:82-84`) before the boundary.
- In the subscription example, a `cancel` that arrives while `charge` runs is stored, carried over,
  and wins run N+1's race at once.
- Consequence: a signal name that no later run ever waits for accumulates in every snapshot. Log a
  warning at the switch when more than a fixed number (for example 100) of signals are carried. Do
  not add a drop API in v1.

---

## 4. Result waiters and parent workflows

- Under Option A the instance is the same actor across runs. `resultReply ()`
  (`WorkflowActor.fs:309-315`) returns `None` while `final` is `None`, which stays true across a
  switch. Waiters in `waiters` (`149, 903-918`) keep waiting and are answered when the final run
  stores `Finished`, `Faulted` or `Cancelled` (`727-739`). No code change is needed beyond not
  touching `waiters` in the switch.
- `handle.Result id timeout` (`Api.fs:133-146`) gets the final run's `'Result`. All runs share one
  definition, so one result type.
- `awaitChild` (`Children.fs:111-150`) asks the child ID once a minute. The child ID does not change
  when the child continues as new, so the parent gets the child's final result. A child whose chain
  never ends keeps its parent waiting. The parent's own deadline or cancel is the remedy, as today.
- `WorkflowFlow.run` / `Run` (`Streams.fs:82-99, 128-142`) await the final run. Their `timeout`
  covers the whole chain, which the XML comment should say.
- Temporal comparison: a client's `GetResult` follows the continue-as-new chain by default, and a
  parent waiting on a child gets the result of the child's last run. FCQRS matches that without run
  IDs, because it addresses instances only by workflow ID.

---

## 5. Status, run identity, keys, traces, metrics

### Status

`WorkflowStatus` (`Messages.fs:24-37`) describes the workflow ID, which means its current run.
`ContinuedAsNew` is never a status, unlike Temporal, where a closed run has the status
`ContinuedAsNew`. FCQRS has no per-run addressing, so a client never sees a closed run. During run N
the status is `Running ["2.3 charge"]`: the pending list shows the run through the path prefix (see
below). No change to the type, so no wire or source break. A `Run` field on `Running` can come later
if operators ask for one.

### Run identity: number, not GUID

Use a run number, starting at 1. Existing instances and every instance's first run are run 1. A GUID
run ID adds nothing when runs are addressed by workflow ID, and a number is deterministic, short, and
ordered.

### Idempotency keys, command IDs and child IDs must differ per run

All of these derive from `(workflow name, workflow ID, step path)`:

- `AttemptContext.IdempotencyKey = $"{definition.Name}/{id}/{path}"` (`WorkflowActor.fs:428-429`).
- `send`'s correlation ID and command ID are hashes of that key (`Workflow.fs:386-404`).
- A child's ID is `$"{parentId}:{path}"` (`Children.fs:63, 103`, and `WorkflowActor.fs:182, 185,
  551-565`).

Paths restart at `0` in each run, so without a change:

- Run 2's `charge` reuses run 1's key, and the payment provider returns month 1's charge.
- Run 2's `send` reuses run 1's command ID, and the aggregate answers with run 1's verdict, as it
  must for a repeat.
- Run 2's `startChild` reaches run 1's child. `startChild` accepts any `StartReply`
  (`Children.fs:85`), so the new run would await the old child's result.

These are silent correctness failures, so v1 must fix them.

**Option 1 (recommended): run number in the step path.** The root fiber of run N≥2 gets the prefix
`"{N}."` in place of `""` (`Machine.fs:166`). Paths become `2.0`, `2.3/0/1`, `2.5@change`.

- Every derived identifier becomes unique per run with no format change. Run 1 is byte-for-byte
  what it is today, so steps in flight during the upgrade keep their keys.
- Path parsing still works:
  - `ReachedFrom` strips the fiber prefix (`Machine.fs:31-42`).
  - `Record` and `inAbandoned` split on `/` and `@` (`Machine.fs:45-60, 181-195`), so the block
    path is `2.3`.
  - Version markers are `{peek}@{id}` (`Machine.fs:294`).
- No collisions: a run-1 path never contains `.`.
  - A run-N child ID `P:2.5` has its last `:` before `2.5`, which is not a valid run-1 path, so it
    cannot equal any run-1 child ID.
  - An idempotency key ends in `2.5` rather than in a run-1 path, so it cannot equal a run-1 key.
- Status, log scopes and span tags show the run for free.

**Option 2: an explicit suffix on each derived identifier.** For example the key `name/id/path~N`
and the child ID `parent:path#N`. Three places to change and keep consistent, plus the 7.7.3 detail
field. `~` cannot be used in child IDs because workflow IDs forbid it (`Api.fs:68-69`).

**Option 3: expose `Run` and let applications build keys.** Easy to forget, and the built-in `send`
and child IDs would still collide.

Body visibility: the body does not see the run number in v1. It cannot read its own ID today either
(`Schedules.fs:411-412`), and a counter it needs belongs in its input. Activities see the run
implicitly through `IdempotencyKey`. Adding `Run` to `AttemptContext` would break code that
constructs that public record, so leave it out unless asked.

An aside, which exists today: `name/id/path` is ambiguous when IDs contain `/`. IDs `a/1/0` (path
`0`) and `a` (path `1/0/0`) give the same key. This is out of scope, but worth a note in the
documentation or a later fix.

### Traces

Today every span parents to the trace that started the instance (`traceParent ()`,
`WorkflowActor.fs:210-213`). A subscription that runs for years would put all its spans in one trace,
which tracing backends handle badly.

**Recommendation:** at the switch, start a new trace context. Store it as the record's `traceparent`
and add a link from the new run's first span to the previous run's trace (`mark` already supports
links, `WorkflowActor.fs:288`). Generating it at the switch and storing it keeps replay deterministic.
The alternative, keeping the first trace forever, is simpler but unbounded.

### Metrics and logging

- New counter `fcqrs.workflow.continued` (tag `fcqrs.workflow.name`). `started` counts instances, not
  runs. `running` stays at 1 across the switch.
- `fcqrs.workflow.recovery.records` now counts records after the snapshot. Document that.
- A flow line: "Workflow {Workflow} {Id} continued as new as run {Run}". Add `fcqrs.workflow.run` to
  the log scope and span tags (`WorkflowActor.fs:216-230, 252-259`).
- The `WorkflowTelemetry` XML comment (`Messages.fs:14-20`) and `docs/how-to/observability.md` list
  the new instrument.

---

## 6. Cancel, parking, and children across the boundary

### Cancel

Today a cancel stores `CancelRequested` and fails every pending step in one write
(`WorkflowActor.fs:940-995`). A body that lets the failure pass ends `Cancelled`. One that handles it
can run cleanup steps and then return (`Machine.fs:464-466`).

- **Rule (recommended):** a cancel request ends the chain. If a body returns `ContinueAsNew` after
  `CancelRequested` is stored in its run, the machine stores `Cancelled` instead and logs a warning.
  The caller cancelled the workflow ID, not one run. A body that handles the cancel and returns
  `Complete` still ends `Finished`, as today. Because no run starts after a cancel request,
  `CancelRequested` and the cancelled-children tracking (`observe`, `WorkflowActor.fs:178-197`) never
  need to cross a boundary.
- Alternatives: carry the cancel into run N+1, which then starts already cancelled; or allow the
  continue and ignore the cancel. Both surprise the caller.
- Race between a cancel and the switch:
  - If the cancel is processed first, run N's pending steps fail with `Cancelled`, and a later
    `ContinueAsNew` becomes `Cancelled` by the rule above.
  - If the switch record is persisted first, the stashed cancel reaches run N+1 and fails its first
    stored steps.
  - The workflow ID ends cancelled in both orders.
  - A step that run N+1 has begun but not yet stored (`ScheduledAt = None`) is skipped by the cancel
    loop (`WorkflowActor.fs:972`), as today. Check that the existing machine then fails the step
    when it is stored, as it does for run 1.

### Parking

- A parked instance has no machine (`WorkflowActor.fs:571-586`), so it cannot reach the switch.
- Run N+1 replays only its own records, so a deploy that changes old code paths cannot park it. Only
  a change to the code of the run in progress can.
- Existing gap that this feature makes more likely: an input the code cannot read faults the instance
  rather than parking it. The decode happens inside `definition.Body` (`Api.fs:78-80`), which runs
  inside `protect` (`WorkflowActor.fs:591`), so the exception becomes `Faulted BodyFailed`. Every run
  boundary decodes an input, so a deploy that changes `'Input` would fault every subscription at its
  next switch. **Recommendation:** decode the input outside `protect`, so an unreadable input goes
  through `guarded` and parks. Also read the next input back before storing `ContinuedAsNew`, as
  `attemptDone` reads a result back (`WorkflowActor.fs:786-794`). If it cannot be read, the run ends
  `Faulted NotRetryable` rather than storing an input that no run could read.

### Children of earlier runs

- With Option A, a run ends only when its body returns. At that point every awaited child has
  returned. A child started with `startChild` and never awaited is detached by definition and keeps
  running, as it does when a parent finishes today (`Children.fs:57-58`). A child start abandoned in
  a race's losing branch may leave an orphan, also as today.
- A cancel of the workflow ID in run N+1 does not reach run N's detached children. Document it in
  the same place that says a detached child outlives its parent.
- A child that itself continues as new keeps its ID, so its parent's handle stays valid (section 4).

---

## 7. Versioning and deploys during the switch

- Run N+1 starts from the top of the latest code with an empty step history, so every `version`
  marker returns its `latest` (`Workflow.fs:491-492`, `Machine.fs:305-308`). Markers of earlier runs
  no longer matter. Continuing as new regularly is therefore also a way to retire old code paths: once
  every instance has passed a boundary after a deploy, the `else` branch of a `version` check can be
  deleted. The documentation should say how to confirm that (for example, a projection over
  `ContinuedAsNew.at`).
- Deploy in the middle of the switch: the switch is one record, so there are only three states.
  1. The process stopped before `ContinuedAsNew` was stored. Run N replays on the new code. If the
     new code still continues, it stores the switch. If it diverges, the instance parks (existing
     behaviour; protect the change with `version`).
  2. `ContinuedAsNew` was stored and the snapshot was not. Journal-only recovery resets at the record
     and runs run N+1 on the new code.
  3. Both are stored. Recovery starts from the snapshot.
- The next input's type is a persistent contract between consecutive deploys, exactly like a start
  input today. Changing it needs a reader for the old shape, and with the parking change above, a
  mismatch parks.

---

## 8. Migration and compatibility

- Existing journals contain no `ContinuedAsNew` and no snapshot. They recover exactly as today: run
  1, empty prefix, the same keys and child IDs. The snapshot query was already happening.
- Additions to persistent contracts:
  - The `WorkflowRecord.ContinuedAsNew` case. It is journaled as
    `FCQRS.Workflows.WorkflowRecord+ContinuedAsNew, FCQRS.Workflows` (`Journal.fs:47-51`).
  - The snapshot type's name, namespace, assembly and fields.
  - The run-path prefix format `"{N}."`.
- Rollback: an older FCQRS.Workflows cannot resolve the new case, so any instance that has continued
  fails replay (`ReplayFailed`, it stops). Projections built against the older package fail on that
  row. CLAUDE.md says "Add readers before writers"; the owner's recorded preference is single
  releases with breaking changes documented. **Recommendation:** ship in one release (7.8.0) and state
  in the changelog that rolling back below it is unsupported once any instance has continued as new.
- Source compatibility: adding a union case makes exhaustive F# matches over `WorkflowRecord` in user
  projections incomplete (warning FS0025, `MatchFailureException` at run time). Changelog entry.

---

## 9. Schedules, streams, C#

- **Schedules** (`Schedules.fs`): a scheduled run's ID is `schedule@due` (`755-757`). A run that
  continues as new keeps that ID and its `ScheduledRun<'Input>` input, so `Due` keeps the original due
  time unless the body changes it. The schedule never waits for runs, so nothing in the dispatcher or
  its index changes. Under Option A, persistence-ID lengths do not change either. Risk: a scheduled
  run whose chain outlives the next due time overlaps with the next run, and an endless chain per due
  time leaks instances. Document: use either a schedule or continue-as-new for periodic work, not
  both, unless each chain ends before the next due time. Do not reimplement schedules on
  continue-as-new (Temporal's cron is built that way; FCQRS's schedule aggregate already gives
  catch-up and pause semantics).
- **Streams**: no code change. Document that the outcome is the final run's and that `timeout` covers
  the chain.
- **C#**: `ContinuingWorkflow<TInput, TResult>`, the `Define`/`Register`/`AddWorkflow` overloads, and
  the `RunEnd` union with protected helpers. `IWorkflowClient` is unchanged. The F# and C# examples in
  the documentation stay equivalent.

---

## 10. Test strategy

### Machine tests (`test/Facade.Tests/WorkflowMachineTests.fs`, no actor)

- A body returning `ContinueAsNew` emits one `ContinuedAsNew` with the encoded input and run N+1. A
  body returning `Complete` emits `Finished`.
- The existing prefix-replay property (`WorkflowMachineTests.fs:166`) extended to multi-run journals:
  replay from every prefix of a three-run journal reaches the same records and repeats no stored step.
- The reset: steps of run N are not visible to run N+1, and `checkConsumed` applies per run.
- Carried signals:
  - Unconsumed signals are carried in order.
  - Consumed signals are not carried.
  - A signal that timed out is carried.
  - A signal stored after the boundary is consumed by run N+1.
- Path prefix:
  - Run 2's paths are `2.n`, including inside `all`/`race` blocks.
  - A version marker in run 2 returns `latest`.
  - `ReachedFrom` and `HoldsBlock` work with the prefix.
- A next input that cannot be encoded or read back ends `Faulted`, and nothing is continued.
- `ContinueAsNew` after `CancelRequested` stores `Cancelled`.
- A continuing definition's `Body` called inside another body faults with the "start it as a child"
  message.
- Serialization: the new case and the snapshot type round-trip through the journal serializer
  (extend `WorkflowMachineTests.fs:33`) and through `VerifySerialization.fs`.

### Actor tests (`WorkflowTests.fs`)

- A `Result` waiter registered during run 1 receives run 3's result.
- `awaitChild` on a child that continues twice gives the child's final result.
- `Start` during run 2 returns `AlreadyStarted (Running ...)`.
- Activity keys recorded across three runs are distinct, and run 1's key equals the 7.7.x format
  (golden test).
- A `send` in each run reaches the aggregate as a new command, not a repeat.
- A `startChild` at the same position in runs 1 and 2 creates two children.
- Signals sent concurrently with the switch, in a tight loop: each one is consumed exactly once, in
  order.
- Cancel before, during and after the switch: the instance ends `Cancelled` in every order.
- Snapshot behaviour:
  - After a switch, recovery replays only the current run (assert `recovery.records`, or add a
    `Records` field to `Inspect`).
  - With the snapshot rows deleted, recovery from the journal alone reaches the same state.
  - With a corrupt snapshot row, the instance stops and the process keeps running.
  - Older snapshots are deleted after a successful save.
- A 7.7.x journal fixture without the new case recovers unchanged.
- A projection over all events sees `ContinuedAsNew` with contiguous sequence numbers, and a rebuild
  from the start works.

### Crash tests (in the style of `WorkflowBankInvariantTests.fs`)

A subscription workflow charges a test "bank" that deduplicates by idempotency key, and the process
is killed at random times. Invariants: each month is charged exactly once, `MonthsPaid` equals the
number of distinct charges, and no instance is lost or duplicated. Add deterministic injection points
for each state of the switch:

1. Before `ContinuedAsNew` is stored.
2. After it is stored and before the snapshot is saved (a test hook that fails or delays
   `SaveSnapshot`).
3. After the snapshot and before run N+1's first record.
4. During run N+1's first activity.

Run on SQLite and, under `FCQRS_TEST_POSTGRES`, on PostgreSQL.

### C# (`test/Workflows.CSharp.Tests`)

A `ContinuingWorkflow` compiled by the C# compiler, using `return ContinueAsNew(...)` and
`return Complete(...)`, checked against the F# behaviour.

### Documentation outline

- `docs/concepts/workflows.md`: a short section "Continue as new" after "Repeat steps". It contains
  the subscription example in F# and C#, then:
  - the guarantee (one record ends the run and starts the next, on the latest code);
  - signals carry over;
  - `Result` and `awaitChild` get the final run's outcome;
  - keys differ per run;
  - a cancel ends the chain;
  - detached children of earlier runs keep running.

  It replaces the advice at `workflows.md:318-320` ("start a new instance for each period of work")
  and `359-361` ("start a detached child with its remaining work"). That workaround nests child IDs
  (`id:3:3:3...`) until the persistence ID passes 255 characters.
- A how-to page, "Run a workflow for years": when to continue, keeping the input small, retiring old
  code paths, carried signals, and choosing between a schedule and continue-as-new.
- Reference details live in XML comments (`defineContinuing`, `RunEnd`, `ContinuingWorkflow`,
  `AttemptContext.IdempotencyKey`), the observability page, and the changelog. The path prefix and the
  snapshot are reference material, not concept-page material.
- The workflow tutorial needs no new step. A tenth step could follow later if wanted.
- Update CLAUDE.md's Workflow guarantees: "A workflow has no snapshot" becomes "A workflow takes a
  snapshot only where a run continues as new; the snapshot holds the next run's input and carried
  signals and only shortens replay."

---

## Decisions that need the owner (most important first)

1. **Storage model.** Recommend Option A: one persistence ID, a `ContinuedAsNew` record, and an Akka
   snapshot at the boundary, with no row deletion in v1. Retention by deletion stays a possible later
   opt-in because it breaks projections.
2. **Run identity in derived IDs.** Recommend prefixing run N≥2's step paths with `"{N}."`, so
   idempotency keys, `send` correlation and command IDs, and child IDs become unique per run while run
   1 stays byte-identical. The alternative is a suffix on each identifier. Either way this must ship
   with the feature: without it, run 2 silently reuses run 1's charge, command verdict, and child.
3. **API shape.** Recommend a typed top-level result (`Workflow.defineContinuing` with `RunEnd =
   ContinueAsNew | Complete`, and C# `ContinuingWorkflow` with protected helpers) over a
   Temporal-style terminal callable anywhere.
4. **Signals.** Recommend carrying every stored, unconsumed signal into the next run in order, with a
   warning past a threshold. The alternative is dropping them, as Temporal does by default.
5. **Cancel and continue.** Recommend that a run which returns `ContinueAsNew` after a stored cancel
   request ends `Cancelled`.
6. **Unreadable input parks.** Recommend decoding a run's input outside `protect`, so an unreadable
   input parks the instance instead of faulting it. This also changes run 1's behaviour today, so it
   could ship separately first.
7. **Traces.** Recommend a new trace per run, linked to the previous one.
8. **Release and rollback.** Recommend one release, with rollback below it documented as unsupported
   once an instance has continued.
9. **A run with no step that continues.** Recommend faulting it ("continued as new without storing a
   step"), because otherwise it writes records and snapshots in a hot loop. The alternative is to
   allow it and rely on the author.

## Risks and open questions

- **Akka snapshot load failure in 1.5.59.** I have not verified whether Akka.NET 1.5.59 falls back to
  an older snapshot or fails recovery when a snapshot cannot be deserialized. The design assumes it
  fails recovery (`ReplayFailed`-like handling). Check `Eventsourced` recovery in the 1.5.59 source
  before implementing.
- **Akka.Persistence.Sql delete semantics.** Relevant only to a future retention option. My reading
  (the highest row is kept as logically deleted) is not verified against the DAO code.
- **Temporal details I am unsure of:**
  - whether a parent's `ParentClosePolicy` applies to its children on continue-as-new (I believe it
    does);
  - the exact server rule for signals that arrive during a continue-as-new workflow task.

  Neither changes the recommendation.
- **Snapshot table growth.** One row per continued instance once older snapshots are deleted. If the
  delete after a successful save is skipped or fails, rows accumulate. Log on `DeleteSnapshotsFailure`.
- **Running instances stay in memory.** A running instance never passivates (`WorkflowActor.fs:385-392`
  passivates only ended or unstarted instances). A million subscriptions sleeping for a month are a
  million resident actors. Continue-as-new shrinks each one's history but not their number. This is
  out of scope, but it is the next limit for the stated use case.
- **Carried signals accumulate** if the code stops waiting for a name. Mitigated only by the warning.
- **Pending strings change shape.** Status pending strings and log scopes show `2.3` paths for later
  runs. Anyone parsing them will see the new shape.
- **The internal terminal case touches the core loop.** It must be handled in `force`,
  `Continuation.Advance`, `whileLoop`, the C# `AsWorkflow` loop (`CSharp.fs:101-124`), and
  `Machine.run` (`Machine.fs:284-311`), so the stack-safety tests (`WorkflowMachineTests.fs:220`)
  should include a long chain of runs.
- **Projection authors.** Projections that derive "current step" from workflow records must reset on
  `ContinuedAsNew`. The documentation needs an example.
- **Existing key ambiguity.** The `name/id/path` ambiguity for IDs containing `/` predates this work.
  Decide separately whether to fix it, since fixing it changes run 1's key format.
