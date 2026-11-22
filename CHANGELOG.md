# Changelog

## 7.15.0 (FCQRS.Workflows)

- **See what is running.** `Workflows.running api` (C#: `WorkflowHost.Running(actor)`) lists the
  step attempts the process is running, oldest first: each activity call, command waiting for its
  reply, and child start, with its workflow, instance, step, attempt, start time, and whether its
  token was cancelled. An attempt stays listed until its call returns, so a cancelled one that
  stays listed is a call that ignores its token.
- **A shutdown waits for cancelled activities.** Stopping the actor system stops every workflow,
  which cancels each running attempt's token, as before. It now also waits up to
  `akka.fcqrs.workflow-drain-timeout` (three seconds) for those calls to return, so an activity
  that observes its token can finish its cleanup, and logs a warning naming each call still
  running when the time is up. Above five seconds, raise
  `akka.coordinated-shutdown.phases.before-actor-system-terminate.timeout` too.
- A test now checks that an abandoned branch's running activity, a race's loser or a failed `all`
  block's sibling, sees its token cancelled. That was already the behaviour; the documentation now
  says so.
- Nothing stored changes.

## 1.0.0 (FCQRS.Analyzers)

- **New package: determinism analyzers.** `FCQRS001` reports a workflow body that reads the clock, a
  GUID generator, a random source, or an environment variable, and `FCQRS002` an aggregate's fold
  that does. An activity's call and a decision are not checked. In C# the compiler runs the analyzer
  in every build; in F#, run the `fsharp-analyzers` tool after a build, or use Ionide. New guide:
  "Check bodies and folds for nondeterminism".

## 7.5.0 (FCQRS core)

- **Projections on SQL Server.** `ProjectionSqlDialect.SqlServer` joins SQLite and PostgreSQL:
  `Fcqrs.projection`, `Fcqrs.transactionalProjection`, and the C# `AddProjection` and
  `AddTransactionalProjection` run on a journal connected with a `SqlServer` `DBType`. Install
  Microsoft.Data.SqlClient in the application. Projections need SQL Server 2016 or later, at
  compatibility level 130 or higher, and a projection name of at most 128 characters. A projection
  applies each aggregate's events in version order and skips none, as on the other databases.
- **A SQL Server database must have a binary collation.** Akka.NET creates the journal and snapshot
  tables without a collation, so their persistence-ID column takes the database's. Under a
  case-insensitive collation, a server's usual default, `Account/0/abc` and `Account/0/ABC` are one
  journal key, so one aggregate could recover the other's events. The actor system now refuses to
  start, with `InvalidOperationException`, on a SQL Server database whose default collation is not
  binary (`_BIN` or `_BIN2`), or whose existing journal or snapshot persistence-ID column has another
  collation than the database's. Create a new database with one, such as
  `CREATE DATABASE accounts COLLATE Latin1_General_100_BIN2_UTF8`. **Breaking for an existing SQL
  Server journal on a case-insensitive database:** first check that no two persistence IDs differ
  only in case, then change the database's collation and the `persistence_id` columns of the
  journal, snapshot, and tags tables, dropping and recreating the indexes that include them. Other
  databases are not checked.
- With another provider than SQLite, PostgreSQL, or SQL Server, registering a projection raises
  `ArgumentException` naming the provider, as before.
- Nothing stored changes. The SQL that projections run on SQLite and PostgreSQL is unchanged.

## 7.14.0 (FCQRS.Workflows)

- **Schedules on SQL Server.** The schedule dispatcher keeps its index table,
  `fcqrs_schedule_index`, on SQL Server too, with the requirements of FCQRS core 7.5.0, which this
  version needs. On SQLite and PostgreSQL the table and its row text are unchanged.

## 7.4.0 (FCQRS core)

- **A projection with stored progress can start a workflow on SQLite.** `Fcqrs.projection` with
  `Named` progress, and a named C# `AddProjection`, ran their handler inside the transaction that
  stores the position, which holds SQLite's single writer lock. A handler that waited for a journal
  write, such as a workflow's `Start` or `Signal`, waited for that lock until the projection timed
  out, and the process terminated. The handler now runs before that transaction; the position is
  stored after it returns, only if no other writer moved it. Delivery stays at least once. A
  transactional projection still runs its handler inside the transaction, and its documentation
  now says it must not wait for a journal write on SQLite. New guide: "Start a workflow from an
  aggregate event".
- **Akka.NET 1.5.75, Akka.Persistence.Sql 1.5.73, Akkling 0.18.1.** The package now requires
  FSharp.Core 10.1.201 or later, and caps Akka.Persistence.Sql below 1.6.
- **linq2db 6.** Akka.Persistence.Sql 1.5.73 brings linq2db 6.5.1, a major version. An application
  that references linq2db 5 itself must move to 6. The provider names FCQRS passes still resolve.
- Nothing stored changes.

## 7.13.0 (FCQRS.Workflows)

- **Run a free-monad program as a workflow.** `Free.toWorkflow step program` carries out each
  instruction of a `FCQRS.Model.FreeMonad` program as the workflow step that `step` gives for it,
  and `Free.answer` boxes a step's result as the instruction's answer. Each instruction stores its
  answer as its step's result, so a restart replays the stored answers through the program and
  continues at the instruction it had reached. The program's code must be deterministic, like a
  workflow body, and the step names and the order of the instructions are persistent contracts. A
  step whose result has another type than the program expects faults the instance. It works with
  FCQRS.Model 6.1.0 and 6.2.0. New guide: "Run a free-monad program as a workflow". F# only.

## 6.2.0 (FCQRS.Model)

- **Nested binds in a `Free` program stay cheap.** `Free.bind` kept one closure per bind, so a
  program that recursed `n` levels below a `let!` called `n` closures for every instruction: 8,000
  levels took 775 ms, and 200,000 overflowed the stack. `bind` now keeps the bound functions as
  data and runs them in a loop: 8,000 levels take 4 ms and 300,000 levels run. A loop whose
  recursive call is the last thing in its block was already linear; a million rounds took 40 ms
  before and 72 ms now, in one measurement each. `Pure` and `Impure` are unchanged, so a hand-written interpreter still works,
  and no public signature was removed. Under Fable, `bind` is the earlier one.
- **The builders take loops and `try`.** `free` and `freeResult` now accept `while`, `for`,
  `try ... with`, `try ... finally`, and an `if` without `else` followed by more code. A handler
  sees an exception that the program's own code throws between instructions. `freeResult`'s loops
  end at the first `Error`.
- **Interpreters.** `Free.run`, `Free.runAsync`, and on .NET `Free.runTask` run a program with a
  function that answers each instruction, so an interpreter need not match on `Pure` and `Impure`.
- **A program value shares its first block's state between runs.** The builder runs a block's
  code up to its first instruction when the block is built, so a `let mutable` or a `for` loop's
  position created there belongs to the value, and a second run of the same value continues from
  the first one's state. Build such a program in a function and call it for each run. The
  builders' XML comments say so.
- **Fable.** FCQRS.Model compiles with Fable 5.24. CI now checks it: `scripts/check-fable.py`
  compiles `test/Model.Fable.Tests` to JavaScript with the Fable version pinned in
  `.config/dotnet-tools.json` and runs it under Node. Under Fable, `Eff.safeUnbox` does not check an answer's type, because
  JavaScript keeps no generic type to test and a type test of an option always failed there; it
  also called `Type.ToString`, which Fable does not support. `ShortString.ToString` and
  `LongString.ToString` return their string directly, because Fable 5 rejects `String.ToString`.
- **Documentation.** New concept page "Free programs": instructions, the builders, what an
  interpreter is, and what the types do not check. Every public member of `FreeMonad` has an XML
  comment.
- `Impure`'s continuation is declared over `obj | null`: the answer to an instruction that gives
  `unit` or `None` is null. The compiled signature is the same.

## 7.12.0 (FCQRS.Workflows)

- **Resume a faulted workflow at its failed step.** `handle.Resume id` (C#: `ResumeAsync(id)`)
  stores a `Resumed` record that sets aside the failure of the step that faulted the instance. The
  body replays its stored steps, so steps that completed before the failure do not run again, and it
  runs the failed step again with the same idempotency key, then goes on. The step's retry policy
  starts over. A service that stores its answer for a key, failures included, returns the stored
  failure to the resumed step, as it does to any retry with that key; a new run gives new keys. A resume is refused with `InvalidOperationException` when the instance has not
  faulted, when the body threw an exception of its own, and when the body ran steps after the
  failure, such as compensation. The actor finds the step by replaying the body over the journal
  without running anything.
- `WorkflowRecord.Resumed` is a new record case, a persistent contract. 7.11.0 and earlier cannot
  read a journal that holds one.
- **Start a faulted or cancelled workflow again under its ID.** `handle.StartWith
  WorkflowIdReuse.AfterFailure id input` (C#: `StartAsync(id, input, WorkflowIdReuse.AfterFailure)`)
  begins a new run with the new input in the same journal. `Start` is `StartWith Reject`, which
  keeps today's rule: an ended instance is final. A finished instance stays final under either
  policy, and a running or parked instance is never replaced.
- A run replays none of the earlier runs' steps or signals: it runs every step from the first,
  including those that succeeded before the fault. From run 2 on, idempotency keys, command IDs,
  and child IDs name the run as `<ID>#<run>`; run 1 keeps its keys, so journals written before runs
  existed keep theirs. The key format is a persistent contract. Work the failed run did is therefore
  done again unless that run compensated, or an activity passes a key of its own, such as an order
  number, that the external system dedupes across runs.
- **Breaking:** a workflow ID or schedule ID that contains `#` is refused with `ArgumentException`,
  because `#` names a later run. `StartWorkflow` carries the reuse policy, and `WorkflowHandle` and
  `IWorkflowClient` gain a member.
- Recovery reads every run's records, and a cancel that reached a child of an earlier run is sent
  again at recovery, as it is for the current run.
- `WorkflowTest.Resume()` and `WorkflowTest.StartAgain` do the same in a test. **Breaking:**
  `WorkflowTest.Resume(definition, journal)`, which runs code over a stored journal, is renamed
  `WorkflowTest.Recover`.
- No existing record changes shape. A new run is a second `Started` record.

## 7.11.0 (FCQRS.Workflows)

- **`WorkflowTest`.** Runs a workflow instance in memory, for tests, with no actor system or
  database. It drives the same replay machine as the workflow actor and stores the same records in
  the same order. Activities, commands, and child workflows do not run: `Complete` and `Fail` give
  a waiting step's outcome, `Signal` stores a signal, `Advance` moves the test's clock and fires the
  timers and signal timeouts that are due, and `Cancel` cancels. `Waiting`, `Status`, `Outcome`,
  `Result`, `Journal`, and `Summary` (one line per record) show where the instance is. The input,
  results, and signals are stored and read back, as recovery reads them.
- `CheckReplay` restarts the instance after every record the test stored, never inside one write,
  and raises `WorkflowReplayException` when a restart stores a different journal: a body whose
  steps, stored values, or result change on replay. An activity's arguments are not stored, so a
  GUID or time read outside a step that reaches only an activity's call passes the check.
  `Restart` does one restart. `WorkflowTest.Resume` runs code over a stored journal
  and reports `Parked` when they no longer match.
- `WorkflowTest.Start` and `Resume` take an F# definition or a C# `Workflow<TInput, TResult>`.
- Retry policies are not applied in a test: the outcome a test gives is the step's final one.
- A new task guide, "Test a workflow", tests the quote workflow with Expecto and xUnit, in
  `samples/workflows/fsharp-tests` and `samples/workflows/csharp-tests`.
- Nothing stored changes.

## 7.3.0 (FCQRS core)

- **`AddKeyedTransactionalProjection(options, handler)`.** A C# host registers one projection with
  `AddProjection` or `AddTransactionalProjection`; a second call throws. An application whose
  projection already keeps its read model could not add another, although F# can start any number.
  The new builder method adds a further transactional projection beside it. It starts after the main
  projection, stops before it, keeps its progress under `options.Name`, and is resolved as a keyed
  `IProjection` under that name for `CatchUpAsync` and subscriptions.
- Every C# projection registration now refuses a name that another registration already uses, with
  `InvalidOperationException`: two projections with one name would share their stored progress.
- Nothing stored changes.

## 7.10.0 (FCQRS.Workflows)

- **`WorkflowJournal.InstanceOf`.** Reads the workflow name and instance ID from the persistence ID
  that a workflow record is journaled under, such as `EventEnvelope.PersistenceId`. A
  `WorkflowRecord` does not hold its instance's ID, so a projection over workflow records needs it.
  It raises `ArgumentException` for a persistence ID that is not `<workflow>/<shard>/<ID>`.
- **`WorkflowStatus.IsWaitingFor`.** Tells whether a running instance waits on a step with the
  given name, such as a signal wait, whose step is named after its signal. Before, a caller matched
  the `"path name"` strings of `Running`.
- A new task guide, "Find workflows that wait for a signal", checks one quote with `Status` before
  signalling it, lists the waiting quotes with a transactional projection, and lists every instance
  with its status with a second one. Its programs are in `samples/approvals`.
- Nothing stored changes, and `WorkflowStatus` keeps its cases.

## 7.9.0 (FCQRS.Workflows)

- **`workflowResult`.** A builder for a workflow that gives a `Result`. A `let!` on a step that
  gives an `Error` ends the block with that `Error`, and a `let!` on any other step, such as `now`
  or `sleep`, continues with its value. A plain `Result` can be bound too. A loop stops at the first
  `Error`, and `and!` runs both branches and gives the first branch's `Error`. An `Error` is the
  step's stored result: it is not retried, and replay returns it with its fields.
- An `Error` is a value, so `try ... with` does not run for it and `try ... finally` does.
  Compensation for an `Error` belongs in the code that receives it.
- `WorkflowResult.bind`, `map`, `mapError`, and `ofWorkflow` work on a workflow that gives a
  `Result`. `mapError` lets steps with different error types share one block.
- C# has no builder: an `await` followed by a `return` ends the body the same way. An abstract
  record returned from an activity needs a `JsonDerivedType` for every case to be read back.
- The concept page has a new section, "Return an expected failure as a value". Step 7 of the
  workflow tutorial returns "out of stock" as a value and catches a warehouse that stays busy.
- Nothing stored changes. Existing workflows and journals are unaffected.

## 7.8.0 (FCQRS.Workflows)

- **Breaking: every activity has a time limit.** An attempt without one waited as long as its
  call, so an activity that ignored its cancellation token kept its step waiting until a cancel.
  `Retry.none` is replaced by `Retry.once attemptTimeout`, and `Retry.times attempts` is now
  `Retry.times attempts attemptTimeout`; code that calls them stops compiling until it names a
  timeout. `Retry.forever deadline` and `Retry.command` are unchanged. `activity` and C#'s
  `Activity` raise `ArgumentException` for a policy with neither `AttemptTimeout` nor `Deadline`,
  or with one that is not positive. An attempt that runs out of time fails even when its call
  ignores the token; the call keeps running and its result is ignored. A command step is
  unchanged: without `AttemptTimeout` one send waits for the command timeout. Policies are not
  stored, so running instances and their journals are unaffected.
- **Use Polly inside an activity.** A new task guide and `samples/resilience` put a circuit
  breaker and a concurrency limiter inside an activity and leave retries to the step's policy.
  FCQRS takes no Polly dependency.

- **Monthly schedules.** `ScheduleRule.Monthly(days, times, zone)` is due on days of the month,
  written as `MonthDay` values: `Day n` from 1 to 31, `LastDay`, `Nth(n, weekday)` with `n` from 1
  to 4, and `Last weekday`. A month without day `n` runs on its last day, so `Day 31` runs on
  30 April and on 28 or 29 February. `Nth(3, DayOfWeek.Wednesday)` is the third Wednesday and
  `Last DayOfWeek.Thursday` the last Thursday, a fifth one included.
- **Several times a day.** `ScheduleRule.DailyAt(times, zone)` and
  `ScheduleRule.WeeklyAt(days, times, zone)` take a list of local times. `Monthly` takes one too.
- A rule's days and times form one set of local date-times, so a date-time that two entries name,
  such as `Day 31` and `LastDay` in a 31-day month, is due once. Daylight saving works as for
  `Daily`: a skipped local time is due at the next valid time, and a repeated one once, at its first
  occurrence. Two whole-minute times inside a skipped hour are one due time. A skipped time keeps
  its seconds, so 02:15:30 and 02:45:10 are due at 03:00:30 and 03:00:10, in that instant order.
- `ScheduleRule.validate`, and so `create` and `change`, reject an empty or missing day or time list,
  a `Day` outside 1 to 31, an `Nth` outside 1 to 4, a value that is not a day of the week, and an
  unknown zone, with an error and never an exception.
- A repeated `create` sorts and deduplicates the days and times of every rule before it compares
  the content, as it did for `Weekly` days.
- `ScheduleRule.missed` reads a rule's local dates backwards from `now` and stops once it has the
  due times the policy keeps, so a rule with many times a day costs no more after a long downtime
  than after a short one. For `Daily` and `Weekly` it returns what it returned before.
- `MonthDay`, `Monthly`, `DailyAt`, and `WeeklyAt` are persistent contracts, in the schedule events
  and in the index rows, with golden tests. The stored JSON and index text of `Daily`, `Weekly`,
  and `Every` do not change, and the index format stays 1. FCQRS.Workflows 7.7 cannot read the new
  rules: roll back only before a schedule uses one.
- In C#, `ScheduleRule.NewMonthly`, `NewDailyAt`, and `NewWeeklyAt` take F# lists, which a
  collection expression builds, and a day is `MonthDay.NewNth(3, DayOfWeek.Wednesday)`,
  `MonthDay.NewDay`, `MonthDay.NewLast`, or `MonthDay.LastDay`.
- The package needs FCQRS core 7.2.3.

## 7.7.3 (FCQRS.Workflows)

- A cancelled parent sends its children's cancels again when it recovers. 7.7.2 sent each cancel
  once, from memory: if the process stopped after the child stored its start and before it stored
  the cancel, the child kept running. Now, whenever an instance whose journal holds a cancel request
  recovers, whether it is still running its cleanup, is parked, or has ended, it sends a cancel to
  each child that the cancel reached: the children of the child steps the cancel failed. A parent
  that has ended recovers only when a request reaches it, so until then such a child keeps running.
  A child that was never started, or has ended, refuses the cancel, which changes nothing. An ended
  parent sends the cancels at every recovery, for example at a status request after it left memory,
  and each one makes a child that is not in memory replay its journal to refuse it. A parent can
  resume before its child's workflow is registered, so a cancel that finds no such workflow is sent
  again a few times over about 30 seconds and then logged as a warning.
- A parked parent that is cancelled cancels the child it was starting or waiting for. In 7.7.2 it
  ended without cancelling any child. A child step in a race's losing branch, or in a branch of a
  block that the body went past, stays scheduled in the journal but is no longer waited for, so its
  child keeps running, as it does when a running parent is cancelled. One case remains: when a
  failed `all` or `allAtMost` abandoned a branch and the parent was parked before it stored any step
  after that block, the journal does not show the abandonment, and the cancel reaches the child of
  that branch.
- A parent whose child steps were all stored before 7.7.3 keeps 7.7.2's behaviour when it is
  parked and cancelled: it cancels no child, and a parked parent cancelled under 7.7.2 sends no
  cancel when it recovers after the upgrade. A child that such a parent left running for weeks is
  not cancelled by the upgrade.
- The `StepScheduled` record of a `startChild` or `awaitChild` step holds the child's ID in its
  `detail`, so a recovery that does not run the body knows which child to cancel. 7.7.2 ignores the
  field for these steps, so a rollback reads the new records. A step stored before 7.7.3 holds no
  ID: recovery then cancels the child that a start at that step would have made, which is right for
  a start and reaches no child for an `awaitChild`.
- Waiting for a child no longer leaves a request on the child each minute. `awaitChild` asks the
  child for its result, waits a minute, and asks again. The child kept every request until it
  ended, so a child awaited for a day held about 1,400 of them and then answered each one into dead
  letters, and the parent kept one timer for each retry. A request now holds how long its asker
  waits: the child drops a request whose wait has ended when the next one arrives, and does not
  answer it when it ends. A caller that polls `handle.Result` with a short timeout leaves no requests
  behind either, and a request with a zero timeout still waits without end. A step keeps at most one
  timer of each kind.
- A child started with `startChild` begins a trace of its own, as the workflow design specified.
  Before, every child joined its parent's trace, so a detached child that ran for days kept adding
  spans to the trace of the request that started its parent. Its `Workflow.Start` span now has no
  parent and links to the parent's `Step` span, and its other spans, also after a restart, are
  children of that start span. A child started with `child` (C#: `ctx.Child`) still joins the
  parent's trace. The trace is chosen when the child starts, so a `startChild` handle that the
  parent awaits later still has its own trace.
- The concepts page warns against starting an instance of a child's workflow with an ID of the form
  `{parentId}:{path}`. A parent whose child would get that ID takes the instance for its child,
  waits for it, and cancels it. A child's ID leaves out the parent's workflow name, so two parent
  workflows with the same instance ID that start the same child workflow at the same path share one
  child. Child IDs do not change.
- The package needs FCQRS core 7.2.3.

## 7.7.2 (FCQRS.Workflows)

- A running instance whose journal holds steps that its new code no longer reaches is parked. Before,
  an instance that had stored some of the steps a deploy removed from the end of its body replayed,
  reached the new end, and finished or faulted, and the stored results of the removed steps, or the
  work of a step that was still running, were dropped without a trace. When the body ends, the
  machine now raises `WorkflowDivergedException` naming the first stored step it did not reach and
  what is stored there, so the instance parks and resumes when code that has the step is deployed.
  Stored signals that no wait consumed and version markers do not count, nor do the stored steps of
  a race's losing branches or of the branches a failed `all` or `allAtMost` abandoned. An instance
  that has ended does not replay its body and is unaffected.
- Cancelling a parent while a `startChild` step is in flight cancels the child too. Before, the
  parent failed the step and ended, and a child that the start had created kept running with no
  parent to cancel it. The parent now sends a cancel to the child's ID, which it knows before the
  start is stored. Because that cancel can reach the child before the start does, the start also
  sends a cancel behind itself when the parent was cancelled while it was on its way. A child that
  was never created refuses the cancel, which changes nothing. This holds for a start that recovery
  re-ran after a restart. `awaitChild` keeps its behaviour, and a child that a parent started and
  does not await still runs on after the parent ends. The parent sends the cancel once and does not
  send it again on recovery: if the process stops after the child stored its start and before it
  stored the cancel, the child keeps running. A parked parent that is cancelled ends without
  running any step, so it cancels no child.
- The package needs FCQRS core 7.2.3. No stored record, step path, or child ID changes.

## 7.2.3 (FCQRS core)

- The Release IL of `CatchUpAsync` passes IL verification. Its `with` handler raised from each of
  its branches, and the F# compiler then pushed a placeholder of the wrong type after a call that
  always throws. The JIT accepted the method, because that path never runs. The handler now
  chooses the exception and raises it once; the exceptions it raises are unchanged. CI verifies the
  IL of the Release assemblies as well as the Debug ones, for all six published packages.

## 7.7.1 (FCQRS.Workflows)

- A decided schedule run whose stored input its workflow cannot read no longer retries forever. When
  the input stored with a `Firing` does not read as the workflow's input type, for example after a
  deploy changed that type, 7.7.0 tried the start again every few seconds, logged an error each
  time, and kept the schedule from firing again; neither `delete` nor `change` stopped it. The
  dispatcher now logs one error that names the schedule, the due time, the workflow, and the
  exception, and reports the run's start, so the schedule goes on to its next due time. The run for
  that due time is lost, and the error is the only signal. A later due time stores the schedule's
  current input, so it fails the same way until `Schedules.change` gives the schedule an input the
  workflow reads. Rolling the deploy back afterwards does not bring the lost run back. A schedule
  whose workflow is not registered at all still logs at each due time and waits, because a later
  deploy can register it.
- The XML comments of `WorkflowFlow.run`, `Run`, `start`, and `Start` state the parallelism limit at
  its boundary: the stream keeps at most `parallelism` workflows between start and outcome.
  Workflows that resumed after a restart, or whose timeout failed the stream, run regardless.
- The package needs FCQRS core 7.2.2, released with it, including its reserved `fcqrs-` projection
  names. No stored schedule event, snapshot, workflow record, run ID, or index row changes.

## 7.2.2 (FCQRS core)

- **Breaking for projections named with `fcqrs-`:** projection names that start with `fcqrs-` are
  reserved for FCQRS's own projections, such as the schedule dispatcher's `fcqrs-schedule-index`,
  whose stored read positions a user projection of the same name would share.
  `Fcqrs.projection` with `Named "fcqrs-..."`, `Fcqrs.transactionalProjection` and
  `Projections.start` with such a `TransactionalProjectionOptions.Name`, `QueryApi.Init`, and C#'s
  `AddProjection` and `AddTransactionalProjection` raise `ArgumentException` naming the prefix. The
  C# builder methods raise when they are called, before the host starts. Rename such a projection;
  its new name reads the whole journal once.
- A Debug build of FCQRS no longer fails with `InvalidProgramException` when it sends a command.
  The F# compiler in .NET SDK 10.0.4xx emits a stack underflow in Debug IL when the argument of a
  `raise` whose value is used contains a let binding (dotnet/fsharp#20564), and the command
  subscription's `SagaStartRefused` case had one. It now binds the correlation ID before raising.
  The published packages are built in Release and were not affected; a Debug build from source, as
  the samples under `samples/workflows` and `samples/schedules` make through project references,
  crashed at its first command. Release behaviour, wire formats, and stored data are unchanged.

## 7.7.0 (FCQRS.Workflows)

- **Restarts read one row per schedule, not the run history.** The schedule dispatcher keeps its
  index in the table `fcqrs_schedule_index` in the journal's database and schema, one row per
  schedule, written by the projection `fcqrs-schedule-index` in the transaction that stores its read
  position. A start reads the rows and then only the schedule events stored since; 7.6.0 read every
  schedule event, two per run, at each start. No schedule fires before both are read. With 2,000
  hourly runs on SQLite, a restart read 4,001 events under 7.6.0 and reads none now.
- The table is derived from the schedule events. When it is missing, or a row cannot be read, the
  dispatcher deletes the rows and the projection's read positions and reads the schedule history
  again; an unreadable row is logged as a warning naming the schedule. The first start on a journal
  written by 7.6.0 has no table, so it reads the whole schedule history once, in one transaction per
  event, and no schedule fires until it has finished. On SQLite this took about 0.5 ms per schedule
  event (2 seconds for 4,001 events), roughly 8 minutes per million events; PostgreSQL was not
  measured. Every run now also costs two small transactions on the journal's database, one for each
  of its two schedule events.
- **New database rights.** 7.6.0 only read the journal. 7.7.0 creates the tables
  `fcqrs_projection_progress`, `fcqrs_projection_progress_lock` and `fcqrs_schedule_index` in the
  journal's schema on first use, then reads and writes them, so the journal's database user needs the
  right to create tables there and then to read and write them. Tables created up front must match
  what FCQRS creates; the index is `fcqrs_schedule_index (schedule_id TEXT NOT NULL PRIMARY KEY,
  entry TEXT NOT NULL)`. Without these rights the dispatcher logs an error every second while it
  tries to load the index, and no schedule fires.
- New persistent contracts: the table's name, its columns `schedule_id` and `entry`, the text stored
  in `entry` (pinned by a test), and the projection's name.
- `Schedules.createLatest` creates a schedule with `CatchUp.Latest`, the default policy, and C#'s
  `ScheduleClient.CreateAsync` has an overload without the policy that does the same.
  `Schedules.create` and the overload with a policy are unchanged; a null policy still raises
  `ArgumentNullException`.
- **Breaking for configurations whose query and write journals differ:** `Schedules.register`
  raises `ArgumentException` when `akka.persistence.journal.sql` and
  `akka.persistence.query.journal.sql` differ in connection string, schema, table, or columns,
  because the index and its read positions live in the journal they read. 7.6.0 started with such a
  configuration; set both journals alike. The check runs before `register` registers anything, so a
  failed `register` does not leave the actor system marked as having a dispatcher.
- The package needs FCQRS core 7.2.1, released with it. No stored schedule event, snapshot, workflow
  record, or run ID changes.

## 7.2.1 (FCQRS core)

- An unfiltered projection's query of recent writes on SQLite reads only the journal rows numbered
  above its starting number, as a filtered one does since 7.2.0. It read the whole persistence-ID
  index on every poll, so each poll cost as much as the journal was long: on a journal of one
  million rows, a poll that found 100 new rows took 23 ms and now takes 0.05 ms. A query whose
  starting number lies far behind the journal's end now costs more: 34 ms against 33 ms with
  100,000 rows above it, and 364 ms against 98 ms when every row lies above it. The full scan and
  PostgreSQL are unchanged, and the query finds the same rows as before.

## 7.6.0 (FCQRS.Workflows)

- **Schedules start workflows at due times.** `Schedules.register api [ ScheduledWorkflow.ofHandle
  handle ]` registers the schedule aggregate and starts the actor system's dispatcher, which may
  start the listed workflows. `Schedules.create`, `change`, `pause`, `resume`, and `delete` manage a
  schedule by ID while the application runs, and `Schedules.state` reads the dispatcher's index.
  C# has `ScheduleHost.Register(actor, ScheduleHost.Workflow(client))`, which returns a
  `ScheduleClient` with `CreateAsync`, `ChangeAsync`, `PauseAsync`, `ResumeAsync`, `DeleteAsync`, and
  `State`.
- Rules are `ScheduleRule.Daily(time, zone)`, `Weekly(days, time, zone)`, and `Every(interval)`,
  from one second to 366 days. Local times follow the time zone across daylight saving: a skipped
  time runs at the next valid time, and a repeated time runs once, at the first occurrence.
  `CatchUp.Latest`, `All` (at most the 1000 most recent), and `None` decide which missed due times
  run. A weekly rule's days are sorted and their repeats removed, so a repeated `create` with the
  days in another order is the same content.
- `create` and `change` raise `ArgumentException` before sending anything for a rule that
  `ScheduleRule.validate` rejects (a time zone the system cannot read, no days, a value that is not
  a day, an interval out of range), and for an ID that is blank, contains `~`, is longer than 200
  characters, or whose escaped form with the escaped workflow name exceeds 205 characters, so a
  run's persistence ID fits in 255. A missing rule or policy raises `ArgumentNullException`. The
  aggregate refuses, and never raises for, any rule or a due time it cannot compute.
- Schedule commands raise `InvalidOperationException` until the actor system's saga starters are
  wired, and the dispatcher fires nothing until then, because an aggregate that stores an event
  without them terminates the process after `akka.fcqrs.saga-start-timeout`.
- A run starts at least once per accepted due time. Its workflow ID is `<schedule ID>@<due time in
  UTC, ISO 8601>`, so a repeated start stores nothing, and its input is
  `ScheduledRun { Schedule; Due; Input }`. The schedule aggregate decides every run, so a stale
  index cannot start a run for a paused or deleted schedule, or for a due time that a change
  removed. A run decided before a
  change, pause, or delete still starts, on the workflow and input it was decided with.
- A schedule whose workflow the dispatcher does not list, because no `Schedules.register` call
  passed it, logs an error at each due time and starts no run until a later start lists it.
  `akka.fcqrs.schedule-check-interval` (default one minute) is the longest the dispatcher sleeps
  between reads of its index while fewer than 16 starts are running. While 16 are running, it waits
  for one to end, so the error for an unlisted workflow's due time that passes meanwhile is logged
  then. A start that fails is tried again the shorter of the check interval
  and five seconds after the failure, and the dispatcher does not wait for it: a start that hangs
  delays only its own schedule, while fewer than 16 hang at once.
- New persistent contracts: the aggregate `fcqrs-schedule`, its event cases, `ScheduleRule`,
  `CatchUp`, `ScheduleSpec`, run IDs, every run's input `ScheduledRun { Schedule; Due; Input }`,
  and, because the aggregate takes snapshots, `ScheduleState` and `AcceptedDue`. Commands are not
  journaled. Tests pin the JSON of each.
- The documentation of `version` says what its name decides: when a name may repeat, and that a
  rename switches instances that ran it back to the old steps.
- The schedule index reads only the schedule aggregate's events, through the entity filter of
  FCQRS core 7.2.0. The package needs FCQRS core 7.2.0, released with it. No stored workflow record
  or step path changes.

## 7.2.0 (FCQRS core)

- **A projection can read only named entities.** `Projection.ofEntities ["Account"]` in F#,
  `TransactionalProjectionOptions.Entities` for a transactional projection, and an optional
  `entities` argument on every `AddProjection` and `QueryApi.Init` overload in C#. A name, as
  registered, selects the persistence IDs that start with it and `/`: an aggregate's, saga's, or
  workflow's. FCQRS escapes it as Akka escapes a sharding region's name, so `Order Line` selects
  `Order%20Line/...`.
  Without names, a projection reads every entity, as before.
- The filter is applied in the SQL that captures journal heads, including the full scan, and that
  finds the next events, on SQLite and PostgreSQL. Other entities' events are not sent to the
  process or deserialized. On SQLite the full scan reads only the names' range of the journal's
  persistence-ID index; the queries bounded by a journal number read every row above it, so a
  listing never reads and sorts the entity's whole history. On PostgreSQL the database reads other
  entities' rows in every query. A filtered pass reads each persistence ID's events with Akka's
  `CurrentEventsByPersistenceId`, one query per persistence ID with events among the next
  `BatchSize` rows (500 by default), whose envelopes carry the global journal number, and applies
  them in journal order, as an unfiltered pass does. Interleaved identities therefore cost about one query per one or two
  events in a first pass. Progress is stored only for the named entities.
- **Breaking for F# record construction:** `Projection` has a new `Entities` field. Code that builds
  the record directly adds `Entities = []`; `Projection.single`, `multi`, and `filtered` set it.
- **Breaking for C# binaries:** the optional `entities` parameter changes the signatures of
  `QueryApi.Init` and `AddProjection`. C# source compiles unchanged, but an assembly compiled
  against 7.1 fails with `MissingMethodException` until it is rebuilt.
- The package needs Akka.Persistence.Sql 1.5.59 or later, the version whose
  `CurrentEventsByPersistenceId` envelopes were checked to carry the global journal number.
- No stored event, progress row, or journal query of an unfiltered projection changes.
- The actor system starts the Akka extensions FCQRS uses (Persistence, SQL persistence, cluster
  sharding, distributed pub-sub, and the stream materializer) before it joins the cluster. Akka
  rebuilds its settings without a lock when an extension starts. A projection created while a
  shard coordinator started its journal store on another thread could make the Persistence
  extension fail with "Couldn't find serializer id", and stay failed for every persistent actor.

## 7.5.0 (FCQRS.Workflows)

- **C# streams start workflows.** `WorkflowFlow.Run(client, idOf, parallelism, timeout)` takes an
  `IWorkflowClient<TInput, TResult>` and gives a flow that starts one workflow per element and
  emits a `WorkflowEnded<TResult>` in input order: the ID, and whether the workflow finished,
  faulted, or was cancelled, with its `Result` or `Error`. A faulted or cancelled workflow is an
  element, not a failure of the stream. A workflow that does not end within `timeout` fails the
  stream, as in F#. `WorkflowFlow.Start(client, idOf, parallelism)` emits the ID with true when the
  element started the workflow.
- **`IWorkflowClient.OutcomeAsync(id, timeout)`** gives how a workflow ended without throwing for a
  faulted or cancelled one. A class that implements `IWorkflowClient` itself must add it.
- `WorkflowFlow` is now one static class in namespace `FCQRS.Workflows`, for F# and C#. F# calls
  `WorkflowFlow.run` and `WorkflowFlow.start` as before. An F# program compiled against 7.4.0 may
  need a rebuild.
- No stored record or step path changes. The package still needs FCQRS core 7.1.0.

## 7.4.0 (FCQRS.Workflows)

- **`send` gives only the reply to its own command.** A saga's commands carry the correlation ID of
  the command that started the saga. A command step took the first event its aggregate published
  under that ID. A step that sent its command again, after a lost reply or a restart, could
  therefore store an event the saga had caused as the reply to its own command. The step now takes
  only an event that its own command caused. Every send of one step carries the same command ID, so
  the reply to an earlier send still answers the step, and an aggregate can recognise a repeat by
  the command's ID.
- **`sendWithFilter` waits for an event a filter accepts.** It gives the first event that the
  aggregate publishes under the step's correlation ID and that the filter accepts, whichever
  command caused it, so a step can wait for an event of the saga its command started. C# has
  `ctx.Send(policy, aggregate, id, command, filter)`. The wait is in memory and can miss an event
  that is published while no attempt listens; the workflow concept page states the limits.
- **A command step waits for its policy's `AttemptTimeout`.** One send waited for
  `akka.fcqrs.command-timeout` whatever the policy said, so a longer `AttemptTimeout` had no
  effect: the step sent its command again every 30 seconds. One send now waits for the policy's
  `AttemptTimeout`, and for the command timeout when the policy sets none. A `sendWithFilter` that
  waits for a saga can send once and wait.
- **`Retry.command`** is the policy `send` uses, for a `sendWith` or `sendWithFilter` that changes
  one of its settings.
- The workflow concept page has a section on a command whose event starts a saga.
- No stored record or step path changes. `send` and `sendWithFilter` store the same step. The
  package needs FCQRS core 7.1.0, released with it.

## 7.1.0 (FCQRS core)

- **The command of an async effect keeps the ID of the command that started the effect.** An
  aggregate that answers through `RunAsync` gave the effect's command a new ID, so its events did
  not carry the ID of the command they answer. Only a command sent with an expected version kept
  its ID. Every effect's command now does.

## 7.3.0 (FCQRS.Workflows)

- **`version` gives 1 to an instance that passed a step which stored nothing.** FCQRS decided that
  an instance had passed a `version` step only from a record at the path of the step right after
  it. A block with no branches, such as `allAtMost` over an empty list, stores no record. An
  instance past such a block got the latest version, ran the new steps, and ran its later steps
  again at shifted paths with new idempotency keys. A step stored anywhere after the `version` step
  now shows that the instance passed it.
- **A command that was waiting for its reply is sent again after a restart, even past its deadline.**
  A step whose deadline passed while the process was stopped failed without an attempt. An outage
  longer than five minutes therefore failed every `send` in flight as `OutcomeUnknown`, and every
  `startChild` as `DeadlineExceeded`. After a restart these two steps now run each attempt to its
  end, and the deadline ends them only when an attempt fails after it. An activity keeps the old
  rule: its deadline is the application's own limit.
- **A parked instance that was already cancelling can be cancelled.** The request was accepted and
  changed nothing, so the instance stayed parked until matching code was deployed.
- **`awaitChild` fails once on a result it cannot read.** It asked again every second without end,
  for example when C# `ctx.Child<TInput, TResult>` named a result type the child does not give. The
  step now fails as `NotRetryable`.
- **An activity result that cannot be read back fails the step as `NotRetryable`.** A result that
  the serializer could write and not read was stored, and the instance was parked at every start
  after the call had been made.
- **Replacing a step with a parallel block, or a block with a step, parks the instance.** The
  mismatch went unnoticed, and the instance ran the new steps beside the stored ones.
- **`until` reads a `DateTime` that names no zone as UTC.** It was read as local time, so the timer
  was due early or late by the server's offset from UTC.
- **A burst of requests to an ended instance replays its journal once.** An instance that is not
  running left memory after every status or result request, so each request replayed its journal.
  It now stays for five seconds after a request, or for `akka.fcqrs.workflow-idle-timeout`. An
  instance still leaves memory when it ends.
- Workflow records keep their journal names, which are the CLR names of the `WorkflowRecord` cases.
  The registration of a `fcqrs.workflow.record` name never took effect and is removed: a stable name
  would have to be registered before any projection read a workflow row. A test now pins the names.
- No stored record or step path changes. The package needs FCQRS core 7.0.1, released with it.

## 7.0.1 (FCQRS core)

- **A correlation waiter is notified after a projection retries a failed read.** A projection holds
  correlation-ID notifications until its read of the journal completes. When a projection started
  with `Fcqrs.projection` or `AddProjection` failed part-way through a read, it dropped the
  notifications of the events it had applied, and the retry did not apply those events again.
  `sendAwaiting` then timed out although the read model was current. The next read that completes
  now delivers them.
- **Projections skip the sharding coordinator's records.** Akka journals the coordinator of each
  saga and workflow type under `/system/sharding/`, beside the shard records under `/sharding/` that
  projections already skipped. Projections handed the coordinator's records to their handlers. Akka
  deletes the start of that history after its snapshots. A projection that then read the journal
  from the start stopped on the missing records, and one started with `Fcqrs.projection` terminated
  the process.
- The XML comments of `Projections.start`, `startTracked`, and `AddTransactionalProjection` describe
  the journal order of a pass, as the F# facade's comments already did.

## 7.2.1 (FCQRS.Workflows)

- **A workflow body can recurse to any depth.** A body that did more work after a recursive call,
  or recursed inside `try`, wrapped each later step's continuation once per open level. Each step
  then cost time in proportion to the depth, and a few thousand levels overflowed the stack. A stack
  overflow cannot be caught, so the process stopped, and recovery replayed the same journal and
  stopped again at every start. C# workflow methods that await themselves were affected the same
  way. `bind`, `try ... with`, `try ... finally`, and the C# await path are now data that the
  interpreter rewrites in a loop: 50,000 levels run and replay on a 256 KB stack.
- The workflow concept page shows how to repeat steps, and that every round stays in the journal.
- No stored record or step path changes. The package still needs FCQRS core 7.0.0.

## 7.2.0 (FCQRS.Workflows)

- **`ctx.Both` runs two C# steps together** and gives both results as a tuple, whose types may
  differ. It is the C# form of F#'s `and!`. `ctx.All` takes steps of one type only.
- The workflow concept page shows `and!` and `ctx.Both`.
- No stored record changes. The package still needs FCQRS core 7.0.0.

## 7.1.0 (FCQRS.Workflows)

- **A workflow logs its failures.** A step that fails for good writes a warning with its exception,
  and a workflow that faults writes an error. Before, neither wrote a line, and the stack trace of a
  step's last failure was kept nowhere: the journal stores an exception's type and message only.
- **A workflow tells its progress in `FCQRS.MessageFlow`**, the category aggregates and sagas use: the
  start, each stored step, each signal received, a cancel request, and the end, at `Information`. The
  lines hold no input, result, or signal payload, a replayed step writes none, and
  `Telemetry.MessageFlowLogging` turns them off.
- **Log scopes.** A workflow's lines, and the lines an activity writes through the same logger
  factory, are in a scope that names the workflow, the instance, the step, and the attempt. With a
  trace listener attached they are written under the step's span, so they carry its trace ID.
- **The span of a signal wait links to the caller that sent the signal.** The sender's trace context
  was stored with the signal but not used.
- No stored record changes. The package still needs FCQRS core 7.0.0.

## 7.0.0 (FCQRS core and FCQRS.Workflows)

- **`FCQRS.Workflows` is a new package for durable workflows.** A workflow is straight-line code
  that starts from input data, with no aggregate required to trigger it. FCQRS stores the result of
  each step, and after a crash an instance replays its journal and continues at the step it had
  reached. A stored step result is never computed again; a step that was running when the process
  stopped runs again, so external calls take an idempotency key.
- **Steps.** `activity` with a retry policy (schedule, attempt limit, deadline from the stored start
  of the step, attempt timeout, retry filter), `sleep` and `until`, `now`, `newGuid` and `random`,
  `waitSignal` and `waitSignalFor`, `send` to an aggregate, `startChild`, `awaitChild` and `child`,
  `all`, `race`, `allAtMost` and `and!`, and `version` for changing a workflow while instances run.
- **From outside.** A handle starts an instance by ID, reports its status, waits for its result,
  stores a signal, and cancels it. `WorkflowFlow.start` and `WorkflowFlow.run` start workflows from an
  Akka.Streams stream.
- **C#.** A workflow derives from `Workflow<TInput, TResult>` and writes `Run` as an `async` method
  that returns `WorkflowTask<T>`. Awaiting an unfinished task that is not a workflow step throws.
  `AddWorkflow` registers a workflow with the host.
- **Telemetry.** An `FCQRS.Workflow` activity source, added to `Telemetry.AllActivitySources`, and a
  meter with the same name. A resumed workflow continues the trace of the caller that started it.
- **An instance whose journal does not match its code is parked**, not a reason to stop the process:
  it runs nothing and resumes when matching code is deployed.
- **Core** lets `FCQRS.Workflows` see its internals and names the new activity source. The package
  needs this core version.
- Workflows run on one node, like the rest of FCQRS. See the Workflows concept page for what the
  application must still do.

## 6.1.0 (FCQRS.ExpectoTickSpec)

- **A pending scenario's steps no longer need to exist.** Until now only a feature whose scenarios
  were all `@pending` skipped step binding. A scenario tagged `@pending` in an otherwise running
  feature still had to bind every step, so a spec written ahead of its steps failed the whole suite
  when it was built. The bridge now blanks pending scenarios out of the feature text before TickSpec
  binds it, along with their tags and any rule left without scenarios. The lines are blanked, not
  removed, so TickSpec's line numbers still point into the real file.
- **`FeatureTest.discover` builds every feature under a resource prefix.** It returns one test list
  per `{prefix}.*.feature` resource, named after the file, with tests named by scenario title
  without the `Scenario:` keyword. `createTest` keeps its names.
- **`Pending` lists waiting scenarios in code, with a reason.** A tag cannot carry a sentence, so
  `@pending` says only that a scenario waits. A `Pending.feature` or `Pending.scenario` entry passed
  to `discover` skips the scenario the same way, without binding it, and reports the reason with the
  skipped test. A first test fails if an entry names a feature or scenario that does not exist.

## 6.15.0 (FCQRS core)

- **`ShiftedScheduler` moves the time a test runs at, without a virtual clock.** It is Akka.NET's
  default scheduler, telling real time moved by its `Shift`. FCQRS stamps commands and events with that
  time and `IActor.TimeProvider` tells it, so a test that lets days pass sets `Shift` and every
  aggregate, and every reader of `TimeProvider`, sees the later time. Its timers run on real time, so
  command timeouts, saga handshakes and cluster sharding behave as in production, which
  `ObservingScheduler`'s virtual clock does not allow. See the configuration reference, "Time in
  tests".

## 6.14.0 (FCQRS core)

- **A projection applies each pass in journal order.** Since FCQRS 6.12, a pass applied its events
  one persistence ID at a time, in alphabetical order of the ID. A handler that reads rows written by
  other aggregates' events saw them out of order: replaying one application's 50,449 events, a
  learner's answer arrived after the course edit that later removed its question, and 328 events
  failed that FCQRS 6.6 had handled. A pass now applies the events it found in the order the journal
  numbered them. Each aggregate's position still decides what is applied, so no event is skipped.
  On SQLite, which runs one write at a time, that is the order the events were written. On
  PostgreSQL, an event that commits after higher-numbered ones is still applied in a later pass.
- A pass with events to apply reads the journal in its global order from the earliest of them,
  `BatchSize` events at a time, instead of reading each aggregate's history separately.

## 6.13.1 (FCQRS core)

- Requires FCQRS.Model 6.1.0, so every application that updates FCQRS gets the CID check below.
  `Fcqrs.cid` and `Values.CreateCID` no longer check `~` themselves; `CID`'s constructor throws the
  same `ArgumentException`. A string longer than 255 characters that contains `~` now fails the
  length check first, with that check's exception.

## 6.1.0 (FCQRS.Model)

- **A CID cannot contain `~` however it is created.** `Fcqrs.cid` and `Values.CreateCID` rejected
  `~`, but `ValueLens.Create` and `ValueLens.CreateAsResult` built such a CID, and its `IsValid` was
  true. FCQRS names sagas and pub-sub topics by joining a CID to an aggregate ID with `~`, so that
  saga never received its originator's events. `CID`'s constructor now throws `ArgumentException`
  for `~`, and `IsValid` is false for a CID containing one.
- The constructor throws instead of returning an error so that its signature does not change: FCQRS
  core releases built against FCQRS.Model 6.0.0 keep working with this release. Reading a CID from
  JSON does not run the constructor, so stored events with such a CID still load.

## 6.13.0 (FCQRS core)

- **Projection queries read only recent writes.** Each pass used to read the latest sequence number
  of every aggregate and saga from the whole journal, and the projection's progress for all of them.
  On a local PostgreSQL 17 with 5 million events across 1 million histories, a caught-up pass took
  3.6 seconds and a new event reached the handler after about 3 seconds. A pass now reads only journal
  rows numbered after what the projection handled `LateWriteWindow` earlier, and the progress of the
  histories it found: the same pass takes about 1 ms, and a new event arrives in about 9 ms. A full
  scan still runs when the projection starts, once more a window later, and every
  `FullScanInterval`; at that size it takes about 5 seconds.
- A write that commits more than `LateWriteWindow` after taking its journal number, 30 seconds by
  default, is handled by the next full scan, within `FullScanInterval`, 5 minutes by default.
  `CatchUpAsync` can return before such a write; no event is skipped.
- `TransactionalProjectionOptions` has `LateWriteWindow` and `FullScanInterval`. `SqlProjectionStore`
  takes `journalOrderingColumn` for a journal whose ordering column is not named `ordering`.

## 6.12.0 (FCQRS core)

- **A saga no longer runs for a starting event that was never stored.** A saga recovered before its
  first state asks its originator whether its starting event was stored. An originator recovered from
  a snapshot with no later events did not know which event held its current version, and answered yes
  when the versions matched. If the process had stopped before the event was stored and another event
  later took that version, the saga ran anyway: the bank invariant test found a transfer credited to
  its target without the debit. The originator now reads the event at that version from its journal
  and compares it.
- **A projection no longer skips an event that commits late.** `Fcqrs.projection` and `AddProjection`
  followed the journal's global event number. On a database that runs writes concurrently, such as
  PostgreSQL, a write that committed about a second after later-numbered writes was passed over and
  never handled, without an error. Projections now follow each aggregate's and saga's own sequence
  numbers, as transactional projections already did.
- **Projections react to local writes at once.** An aggregate that stores an event wakes the
  projections on its node, so they no longer wait for their next poll; transactional projections
  included. Writes on other nodes still arrive by polling.
- A projection started with `Fcqrs.projection` or `AddProjection` retries a failed journal read or
  progress write with backoff, from 1 to 30 seconds. Missing journal history, such as a deleted row,
  terminates the process with `JournalHistoryException`. A transactional projection raises the same
  exception, an `InvalidOperationException`, and stops as before.

### Breaking

- A projection's starting offset is replaced by its progress. `FromStart` keeps progress in memory
  and reads the whole journal at each start, as offset 0 did. `Named "..."` stores progress in the
  journal database under that name and resumes; its handler can receive an event again after a crash.
  In C#, `AddProjection(handler)` starts from the beginning and `AddProjection(handler, name: "...")`
  stores its progress, and `QueryApi.Init` takes the same optional name.
- Handlers receive the event without an offset: `obj -> unit` in F#, `Action<object>` in C#, and the
  same for the `Notify` and list-returning shapes, including `Query.autoPublish` and
  `Query.filterPublish`.
- Events reach a handler in version order within each aggregate, with no order between aggregates.
- `Fcqrs.projection` returns `IProjection`, with `CatchUpAsync` and `Completion`, and `AddProjection`
  registers it for dependency injection.
- Projections require a SQLite or PostgreSQL journal. `Query.init`, the global-offset reader, is removed.

## 6.11.0 (FCQRS core)

- **A saga start that would be lost is refused.** A saga is named after its originator's aggregate ID
  and the event's correlation ID, and keeps the event that started it. Another event that started the
  same saga, from a second command with the same correlation ID or from the same command, was stored
  and ran no workflow: a transfer's money left the account and never arrived or came back. The
  aggregate now refuses such a command and stores none of its events, and the caller's send fails with
  the new `SagaAlreadyStartedException`. Give each command that can start a saga its own correlation
  ID. A repeated starting message for the same event still counts as the same start.
- The saga-start handshake replaces its unused `Retired` case with the refusal, so a node on an
  earlier release cannot read a refusal. Upgrade every node together.

## 6.10.0 (FCQRS core)

- **C# sagas no longer see F# options, and their registration names no types.** A C# saga used to
  receive its state as `FSharpOption<TState>`, which is `null` before the first state, and was
  registered with four type arguments and a separate start predicate. The saga class now handles the
  message before its first state in `Start`, the later ones in `HandleEvent` with the stored state, and
  declares its start rule as `StartsOn`. `AddSaga` infers the saga's types from the class its factory
  returns.
- **A command reaches an aggregate or saga on another node.** Akkling wraps each command sent through
  an entity reference in a `ShardEnvelope`, and the default JSON serializer could not rebuild FCQRS's
  validated IDs inside it. The receiving node dropped the command without logging it, and the caller
  timed out. FCQRS now binds its own serializer to the envelope, which serializes the command with the
  serializer bound to the command. A node on an earlier release still cannot read commands from other
  nodes, so upgrade every node together. Joining a cluster through seed nodes and replies across nodes
  still have open defects.
- The command subscriber's request and an aggregate's saga-start signals never leave their node and
  are now marked `INoSerializationVerificationNeeded`, so Akka's `serialize-messages` check skips them.

### Breaking (C#)

- `Saga<TEvent, TSagaData, TState>` is now `Saga<TData, TState, TEvent>`: the order of the F# `Saga`
  definition, with the originator's event last as in `Aggregate<TState, TCommand, TEvent>`.
- `HandleEvent(object, SagaState<TData, FSharpOption<TState>>)` is split in two.
  `Start(object, TData)` receives messages before the saga has a state, normally its starting event,
  and `HandleEvent(object, SagaState<TData, TState>)` receives the later ones.
- The start rule is the saga's `StartsOn(Event<TEvent>)` member. `AddSaga` takes only the factory,
  as in `.AddSaga(services => new Transfer(services.AggregateFactory<Account>()))`; the
  four-type-argument overload and the `startOn` argument are removed.
- `SagaApi` (`Init`, `InitSimple`, and `Factory`) is removed. Derive from `Saga<TData, TState, TEvent>`.
- Stored sagas stay readable: what a saga stores depends on its `SagaName` and its data, state, and
  event types, not on the C# base class.

## 6.9.0 (FCQRS core)

- **Starting a saga holds no thread.** An aggregate storing an event that starts a saga used to block
  its thread until the saga was ready, and a per-node coordinator raised the thread pool's floor to
  cover those threads. About 1,500 simultaneous saga starts still ran out of threads, and the
  handshake timeout terminated the process. The aggregate now evaluates the start rules itself, sends
  each saga its starting message, and waits for the sagas' readiness as messages. Commands that arrive
  meanwhile are stashed and processed in order once the event is stored; commands an application
  stashed with `Stash` stay stashed. Concurrent saga starts are now bounded by journal throughput.
- **Events that start no saga are stored at once.** Every stored event used to make a round trip
  through the per-node coordinator.
- **A saga that restarts during the handshake is reached again.** The waiting aggregate repeats the
  starting message to sagas that have not answered, instead of relying on a recovered saga's broadcast.
- **A start rule that throws terminates the process with a named error**, instead of surfacing as a
  handshake timeout 30 seconds later.

### Breaking

- `config:akka:fcqrs:max-worker-threads` and `akka.fcqrs.saga-batch-ttl` are ignored, and FCQRS no
  longer changes `ThreadPool.SetMinThreads`.
- A saga recovered during a handshake no longer broadcasts its readiness. An originator on FCQRS 6.8.0
  or earlier waiting for such a saga on a 6.9.0 node therefore waits until its handshake timeout.
  Upgrade every node together.

## 6.8.0 (FCQRS core)

### Breaking

- A saga built with `Fcqrs.saga` or the C# saga builder stores its state rows and snapshots under
  stable journal names when its state type and its originator's event type are registered with
  `JournalTypes`, so renaming or moving those types no longer breaks recovery. FCQRS 6.7.0 reads these
  rows; earlier releases cannot. Rows stored under CLR names keep reading.
- FCQRS depends on FCQRS.Serialization 6.1.0, which stores generic C# union case names without
  assembly versions. Nodes on FCQRS 6.7.0 or earlier cannot read those names.

## 6.7.0 (FCQRS core)

Fixes from a review of the core. Existing journal rows, snapshots, and entity names remain readable.

- **A C# union command case reaches its aggregate.** A C# 15 union shares no base type with its
  cases, so a case passed as `object`, for example `SagaCommands.ToAggregate(accounts, id, new
  ReceiveTransfer(...))`, arrived as a command of the case's own type. The aggregate left it unhandled,
  and the saga waited until its expectation ran out, again and again. An aggregate whose command type
  is a C# union now accepts a command of any of its case types as that union, from a saga, a direct
  send, or a `RunAsync` runner.
- **Registration rejects event types that would be stored as `{}`.** System.Text.Json writes a value
  through an abstract class or interface without polymorphism as an empty object. A C# aggregate whose
  event base type lacked `[JsonDerivedType]` stored every event as `{}` without an error; the replies
  looked right, and the next load could not read the rows. Registering such an aggregate now throws
  `InvalidOperationException`. F# unions, C# unions, and base types with `[JsonDerivedType]`,
  `[JsonPolymorphic]`, or `[JsonConverter]` register as before.
- **Transactional projections skip Akka's sharding bookkeeping.** Sagas remember their entities
  through cluster sharding, which journals that bookkeeping under persistence IDs starting with
  `/sharding/` and deletes its early history after each snapshot. A new or lagging transactional
  projection treated the deleted history as a gap and stopped. Catch-up now excludes those IDs.
- **Correlation waiters wake after the events that caused them.** A transactional projection commits
  persistence IDs in key order, so a saga follow-up that reuses a correlation ID could wake
  `sendAwaiting` before the originator's own event committed. Correlation-ID subscriptions
  (`Subscribe(cid, ...)` and `sendAwaiting`) now receive their notifications after the whole snapshot
  commits, which can add the time the rest of the snapshot takes. Other subscriptions still receive
  every notification when its event commits, in commit order. Correlation-ID subscriptions are also
  routed on publication, so other requests' events can no longer evict a waiter's notification.
- **Deferred events no longer start sagas.** A repeated verdict answered with `DeferEvent` or
  `persistIf false` started a second saga when it carried a new correlation ID.
- **A recovered saga keeps its readiness.** Expectation retries and ignored events reinstated
  handshake flags from before the saga resubscribed, so a repeated start went unanswered and the
  originator's handshake timeout terminated the process.
- **A recovered saga continues whenever its originator stored its starting event.** A saga recovered
  before it left `Started` was aborted as soon as its originator had stored any later event, so a
  restart could silently drop the workflow of a stored event. An originator that has moved past the
  starting event now reads the event stored at that version from its journal. It compares event IDs as
  well as versions, because after a failed save another event can hold the same version; an originator
  recovered from a snapshot with no later events still compares only the version. Answers go only to
  the saga that asked, so a continue answer no longer republishes an old event to every subscriber of
  its correlation ID. An abort that answers an outdated recovery check no longer ends a saga that has
  moved on.
- **Passivation and shard hand-off wait for saves in flight.** Entities stop through an internal
  message instead of `PoisonPill`, which bypassed the persistence stash and dropped the reply of a save
  in flight and the commands queued behind it. This includes a saga's own passivation after
  `StopSaga` or an abort.
- **Pending sends are released on shutdown.** A command waiter that stopped before replying left its
  caller waiting forever. The caller now receives `OperationCanceledException`; the command may or may
  not have been applied. A waiter that stops before it accepts the command raises `TimeoutException`
  after the command timeout plus five seconds.
- **Journal names for unregistered types omit assembly versions.** A node on an older FCQRS release
  could not bind names written by an upgraded node and terminated during a rolling deployment. This
  includes the state rows of builder sagas. Readers accept both forms. This release also reads a
  `saga-wrap` tag, which a later release can write so builder saga state uses registered names.
- **Failures are logged.** Journal write failures and recovery failures of aggregates and sagas, saga
  snapshot failures, and repeated journal read failures in `Query.init` are now logged.
- **A write the journal rejects terminates the process**, as a serialization error already does. In
  6.6.0 the entity kept running, so its next event skipped the rejected sequence number, and the gap
  stopped transactional projections. Stopping only the entity would drop the commands queued behind
  the rejected write. A failed write, such as one during a database outage, still stops only its entity.
- **`RunAsync` runners start off the aggregate's thread.** Synchronous work before a runner's first
  asynchronous step no longer blocks its aggregate, and a synchronous exception reaches the documented
  fail-fast path.
- `sendAwaiting` no longer leaves its timeout timer running after the notification arrives. Over the
  C# host's `IProjection`, it now uses the configured command timeout instead of 30 seconds.
- **A save before the saga starter exists waits for it.** Aggregate regions start before
  `wireSagaStarters` or the host registers the saga starter, and remembered sagas can wake aggregates
  in that window. The saga-start check sent there went to dead letters, so the handshake timeout
  terminated the process. The check now waits for the starter within the same timeout.
- **A custom saga name must keep the correlation ID.** A saga reads its originator, event topic, and
  command correlation ID from its own name. A `PrefixConversion` that changed the correlation ID
  produced a saga that never received its originator's events, so a builder saga stayed in `Started`
  forever. The saga starter now logs an error and does not start such a saga, or one whose conversion
  throws. A conversion that adds a prefix ending with `~`, such as `"audit~" + cid`, still works.
- **A second start of a shared host builder uses only its own saga rules.** Each start of the C# host
  appended its saga start rules to a list on the builder. A later start, such as a second service
  provider built from the same service collection, kept the rules bound to the earlier, stopped
  actor system.
- **An expectation from a low-level saga's initial state keeps its deadline across restarts.** A saga
  registered with `InitializeSaga` never persists entry into its initial state, so a `StayExpecting`
  from that state anchored at arming time and a restart postponed the deadline. It now anchors at the
  creation time of the journaled starting event.
- **Traces and flow logs name a C# union by its active case.** A saga whose state was a C# union
  was traced as `Saga:TransferState` in every state, and flow logs showed a union payload as its type
  name alone. Saga spans, state-change log lines, and rendered payloads now use the active case, as
  command and event spans already did. A payload counts as a union only when the C# compiler marked it
  as one: a command record with a `Value` field was named after that field's type, as in
  `Command:Decimal`. A saga state that is not a union, enum, number, or string is now named by its type,
  so a record's field values no longer reach a `Saga:` span name.
- `SagaApi.InitSimple` documents that its typed handler receives `default(TSagaState)` before the
  first state, so an enum or struct state needs a zero value that means "not started".

### Added

- `Values.VersionValue(version)` returns the number in a `Version` as a `long`, so C# code can read a
  reply's persisted version, for example to pass it to `SendIfVersionAsync`. C# previously had only
  `ToString()`. F# code keeps using `ValueLens.Value`.

### Breaking

- `Actor.Connection.ConnectionString` is a `LongString` instead of a `ShortString`, so connection
  strings longer than 255 characters are accepted. `Fcqrs.connect`, `ActorApi.Create`, and `AddFcqrs`
  take a plain string and are source compatible. Code that builds the record directly changes the
  field's type annotation from `ShortString` to `LongString`.

## 6.1.0 (FCQRS.Serialization)

- A null reference to a class-based C# union is written as JSON `null` instead of terminating the
  process.
- Union case names are written and matched without assembly versions, so a row written on one .NET or
  application version reads on another. Names written with versions by 6.0.0 still read.

### Breaking

- FCQRS.Serialization 6.0.0 cannot read a generic union case written by 6.1.0, because it expects
  the versioned name. Upgrade every node that reads the journal before any node writes with 6.1.0.
  A non-generic case's name has no versions and does not change.

## 6.6.0 (FCQRS core)

- F# applications can declare command-handler records with `FCQRS.FSharp.Handler<'Command, 'Event>`
  and initialize their fields with `Fcqrs.handler api definition`. Registration happens immediately;
  each invocation returns the matching event payload without its envelope. Argument order is event
  filter, correlation id, aggregate id, then command. Projection waiting and saga wiring remain explicit.

## 6.5.0 (FCQRS core)

FCQRS core adds conditional commands and event upcasting. The satellite packages remain at 6.0.0.
Existing journal rows, event envelopes, snapshot formats, and entity names are unchanged.

- **Reject a command when its aggregate version has changed.** `Fcqrs.sendIfVersion` in F# and
  `ActorWiring.SendIfVersionAsync` in C# compare the expected persisted version inside the aggregate
  before running its handler. A mismatch raises `AggregateVersionConflictException` with the
  expected and actual versions. Deferred replies do not advance the version; persisted batches
  advance it once per event. Stashed commands and asynchronous continuations recheck the condition.
  This does not deduplicate commands or wait for a projection, and a timeout does not undo a write.
  Upgrade every destination node before sending these new conditional-command wire messages.
- **Convert readable historical events for current consumers.** Register one-to-one conversions
  with `Fcqrs.withEventUpcaster<Old, New>` or C# `WithEventUpcaster<Old, New>`. Chains run during
  aggregate and saga recovery and both projection read paths. They preserve envelope identities,
  metadata, versions, and journal positions without rewriting stored data. Registration is scoped
  to an actor system and freezes before its first consumer starts. Duplicate sources, cycles,
  null results, and failed conversions cannot silently skip history.
- **Keep compatibility boundaries explicit.** Old payload types must still deserialize. Live
  messages and application-owned snapshot state are not converted. FCQRS upgrades its own saga
  wrappers around historical originator events while retaining workflow state and data. Converter
  failures follow the consumer's failure policy; a transactional projection rolls back the event
  and leaves its checkpoint before that event.

The full suite passes 103 tests with SQLite and PostgreSQL enabled, with 2 existing tests ignored.
Thirteen upcasting cases cover historical fixtures, chained conversion, mixed replay, projections,
and aggregate/saga snapshots. See [Send only at an expected version](docs/how-to/send-if-version.md)
and [Evolve persisted events](docs/how-to/evolve-events.md) for F# and C# examples and rollout guidance.

## 6.4.0 (FCQRS core)

FCQRS core adds transactional projections for SQLite and PostgreSQL. The satellite packages remain
at 6.0.0. Existing projection registrations, persisted event shapes, snapshots, and entity names
are unchanged.

- **Wait for a projection across the entire journal.** `IProjection.CatchUpAsync` captures the highest
  committed sequence number for each persistence identity in one database snapshot, then waits for
  this projection to commit every event through those targets. Call it after the aggregate's
  persistence acknowledgment to include that command's event. Later writes do not extend the target;
  ordering is preserved within each identity, with no ordering guarantee between identities.
- **Commit read-model changes and checkpoints together.** Register with
  `Fcqrs.transactionalProjection` in F# or `AddTransactionalProjection` in C#. The handler receives
  the connection and transaction used to store its progress. Durable checkpoints support restart
  and competing instances; missing journal history fails instead of being skipped. The new runner
  requires retained history and does not support Akka event adapters.
- **Configure discovery and bounded waits.** Background journal-head polling defaults to one second.
  Each catch-up call captures its target immediately and has a configurable timeout and cancellation
  support. A timeout ends that caller's wait without undoing committed work. Processing errors fault
  `IProjection.Completion` and require the application to correct the cause and restart the runner.
- **Configure PostgreSQL from C#.** `AddFcqrs` and `ActorApi.Create` now have overloads accepting the
  database type. The existing overloads continue to select SQLite.

Fourteen catch-up tests cover SQLite and PostgreSQL, including transaction rollback, recovery,
concurrent instances, commit order, ambient transactions, cancellation, and shutdown. CI runs the
PostgreSQL integration cases. See [Catch up projections](docs/how-to/catch-up-projections.md) for
equivalent F# and C# examples and the guarantee's boundaries.

## 6.3.1 (FCQRS core)

FCQRS core only; the satellite packages remain at 6.0.0. Public signatures, persisted event shapes,
snapshot formats, and entity names are unchanged.

- **Saga startup waits for each distinct saga type and entity.** Duplicate acknowledgments cannot
  satisfy another saga's readiness. A saga acknowledges only after its starting event is persisted
  and its subscription is acknowledged, including completed sagas restored from snapshots.
- **Saga acknowledgments reach the coordinator waiting for them across cluster nodes.** Start
  coordination stays on the aggregate's node; sagas reply directly to that coordinator, or broadcast
  readiness after recovery has lost the transient coordinator reference. The acknowledgment uses
  the existing F# serializer with a version-independent type manifest, so a published 6.3.0 host can
  read it during a rolling upgrade to 6.3.1.
- **Projection subscriptions register before returning.** Immediate publications cannot overtake
  registration. Each subscriber has a bounded queue, so a slow callback drops its own oldest
  notifications without blocking other subscribers. Disposing or cancelling an incomplete awaiter
  cancels its task instead of reporting that the requested notifications arrived.
- **Command replies are checked against the publishing aggregate's type and entity.** Sharing a CID,
  entity ID, and event type across aggregate registrations no longer lets one aggregate satisfy
  another aggregate's caller.
- **Command timeouts are fixed deadlines.** `akka.fcqrs.command-timeout` now bounds the wait from
  subscription setup; nonmatching events cannot keep extending it as they could with an idle timeout.
- **Hosted-service constructors can inject handlers, aggregate references, and subscriptions.** The
  injected handles resolve before startup and delegate operations to the runtime after FCQRS starts.
- **Journal type registration is atomic.** Concurrent conflicting registrations cannot both succeed,
  and a rejected registration leaves all names and aliases unchanged.

Fourteen regression tests cover these failures, including two-node coordination and the existing
acknowledgment wire format. The complete facade suite passes 57 tests, with 2 ignored.

## 6.3.0 (FCQRS core)

FCQRS core only; the satellite packages are unchanged. No journal change: passivation stops an idle
actor, it does not touch events, snapshots, or entity identity.

- **Idle passivation is now settable per aggregate type, in configuration and at registration.**
  Every shard region previously took `ClusterShardingSettings.Create(system)`, so Akka's single
  `akka.cluster.sharding.passivate-idle-entity-after` (120s) governed every aggregate in the process.
  Two levers replace that: keys nested under the entity name
  (`akka.cluster.sharding.Order.passivate-idle-entity-after`) override the shared block for one type,
  and a definition's `PassivationPolicy` overrides both. Resolution runs
  definition → per-type config → shared config → Akka's 120s.
- **Sagas are unaffected by either lever.** Saga regions remember entities, which disables idle
  passivation in Akka.NET; a saga still ends at `StopSaga` or abort.

### Breaking

- `Aggregate` (F# facade) gains a required `Passivation: PassivationPolicy` field. Existing
  definitions compile again by adding `Passivation = PassivationPolicy.Default`, which keeps the
  previous behaviour exactly.
- `IActor.InitializeActor` and `IActor.InitializeActorWithRunner` take a `PassivationPolicy` after the
  `SnapshotPolicy`. C# callers of `ActorWiring.InitActor` / `InitAggregate` / `InitActorWithRunner` /
  `InitAggregateWithEffects` are unaffected: the existing overloads remain and pass
  `PassivationPolicy.Default`. The C# `Aggregate<>` base gains an overridable `PassivationPolicy`.

## 6.2.1 (FCQRS core)

FCQRS core only; the satellite packages are unchanged.

Saga naming fixes. No journal or API change: every name below is byte-identical for ids and
correlation ids that need no escaping, so existing journals and snapshots keep reading. Every id
shape whose saga name does change was one that previously crashed the process or left the saga
silently dead, so no working deployment changes behaviour.

- **Sagas now start for every legal entity id and correlation id.** The saga's entity id was built
  from the originator's *actor path name*, which the shard had already written as
  `Uri.EscapeDataString(entityId)` — so the shard escaped it a second time when it named the saga
  actor. Everything that recovered a name from that path then disagreed with everything that stored
  one: the saga-starter's pending batch never matched the `Continue` it was waiting for, the
  originator's start handshake ran to `akka.fcqrs.saga-start-timeout`, and the process died in
  `Environment.FailFast`. Any character `Uri.EscapeDataString` touches triggered it — a space, `+`,
  `%`, `/`, or any non-ASCII character — in **either** the aggregate id or a caller-supplied CID
  (`Fcqrs.cid` / `Values.CreateCID` reject only `~`), and a custom `PrefixConversion` could inject
  one too. Name parsing now works on entity ids throughout, and path names are unescaped at the
  boundary. Two further consequences went with it: the saga subscribed to a topic the originator
  never published to, and its `Originator`-targeted commands addressed a different aggregate entity.
- **An aggregate id containing `~Saga~` no longer kills its saga silently.** `toOriginatorName` split
  on the *first* occurrence of the suffix, so a saga started from id `x~Saga~y` resolved its
  originator to `x`, subscribed to the wrong topic, and parked in `Started` forever — no crash, no
  error, the command returning success. It now splits on the last occurrence, which is always the
  framework's own (a CID cannot contribute one).
- **A long aggregate id no longer fail-fasts the process.** A saga's name is its originator's id plus
  `~Saga~` and a correlation id, and that name was pushed back through the 255-character limit of a
  command's `Sender` field; ids past roughly 213 characters threw inside the saga's command dispatch,
  which escalated to `Environment.FailFast`. The id is resolved once per saga and degrades to no
  sender, with a warning naming the budget, instead of taking the host down.
- **`Fcqrs.sendAwaiting` no longer reports a write as projected when its subscription merely ended.**
  Only a *faulted* notification stream surfaced; one completed normally — by a kill switch, or by the
  notification hub completing during actor-system shutdown — passed silently and returned the
  aggregate's ack as though the read model were current. It now raises unless the awaited
  notification actually arrived.
- **Saga expectation reminders are keyed on an arm epoch instead of the saga version.** Arming does
  not cancel the reminder already scheduled, and two arms at one version (`applySideEffects` runs
  again for the same state when the start handshake acknowledges — reachable through the raw
  `InitializeSaga` API) left two live retry chains, each re-sending and re-arming for the life of the
  state. A monotonic epoch stales the previous chain on every arm.

## 6.2.0 (FCQRS core)

FCQRS core only; the satellite packages are unchanged.

This release makes FCQRS raise the host process's `ThreadPool` minimum worker count — a
process-global setting that affects all code in the process, not only FCQRS. That is why this is a
minor release rather than a patch, even though it carries no API or journal change. The bug it fixes
is not new in 6.1.0: the blocking handshake dates back to at least October 2024 and is present in
every 6.x and 5.x release.

- **Concurrent saga starts no longer kill the process**: the saga-start handshake blocks the
  originator's dispatcher thread until the starter acknowledges, and Akka's default executor is the
  CLR thread pool — so N simultaneous starts held N pool threads, the sagas expected to acknowledge
  them could not be scheduled, and the handshake timed out into `Environment.FailFast`. Measured
  before the fix: 50 concurrent commands to 50 distinct aggregate instances, each starting a saga,
  killed the process on 5 of 5 runs (intermittently from ~35). The saga starter now raises the pool's
  minimum worker count to cover the handshakes it has outstanding, bounded by the new
  `config:akka:fcqrs:max-worker-threads` (default `1024`). The same fan-out now completes in seconds.
  A thread-pool minimum is a floor, not a reservation, so threads are still created only as work
  demands them. The baseline is captured once per process, so a host running several actor systems
  does not ratchet the floor upward.
- New configuration key `config:akka:fcqrs:max-worker-threads` (default `1024`). This RAISES the
  limit on concurrent saga starts rather than removing it: measured on 12 cores, 1000 simultaneous
  starts across distinct aggregate instances now complete and 1500 still fail-fast. The starter warns
  the first time demand exceeds the ceiling, and logs an error if the runtime refuses the raise.
- **A saga started with the wrong event type is loud instead of fatal**: a saga whose registration
  starts it on an event its handler cannot structurally receive dropped the starting message without
  reporting `Continue`, so the originator's handshake ran to its timeout and killed the process,
  reporting the timeout rather than the miswiring behind it. The saga now logs an error naming the
  received and expected types, then releases the originator. Reachable from the raw
  `InitializeSagaStarter` overloads and from C#'s untyped `SagaDefinition.StartingEvent`.
- **`Fcqrs.cid`**: builds a `CID` from a string you already hold and rejects `~`, the separator FCQRS
  builds saga entity names and pub-sub topics from — a CID containing one is parsed back wrong and
  leaves the saga permanently deaf. C# has rejected this since `Values.CreateCID`; the F# facade had
  no equivalent. `Fcqrs.newCid` is unaffected.

## 6.1.0 (FCQRS core)

- Saga expectations: a waiting state can return `StayExpecting { Resend; Deadline; RetryEvery }`
  (F# sugar `expecting`; C# `SagaSideEffectResult.Expect` with `Expectations` / `RetrySchedules`
  factories). The framework sends `Resend` on state entry, re-sends exactly those commands on the
  schedule (backoff applies stretch-only jitter), and past the deadline delivers an
  `ExpectationExhausted` message the saga's event handler must answer with a transition. A thrown or
  unmatched exhaustion is logged as an error and re-delivered one deadline period later instead of
  terminating the process.
- BREAKING (persisted contract): the journaled saga `StateChanged` event and the saga snapshot
  envelope now carry the state-entry timestamp that anchors expectation deadlines. Saga journals and
  snapshots written by 6.0.0 are not readable by this release.
- BREAKING (source): `SagaTransition<'State>` gained the `StayExpecting` case; F# code matching the
  union exhaustively must add it.

## 6.0.0 (FCQRS core)
Stable release of the 6.0.0 line (rc1 through rc6 plus two further audit
rounds). Changes since rc6:

- **Migrated-journal sagas resume correctly**: the rc6 `Incarnation` refactor
  gated the old-shape-snapshot recovery re-drive on `= RecoveredFromSnapshot`,
  but events replayed after such a snapshot flip the incarnation to
  `RecoveredFromJournal`, so migrated journals parked the saga passive
  forever. The re-drive now guards on `IsRecovery`.
- **Throwing command-subscription filters fail the ask loudly**: an exception
  escaping the filter restarted the ephemeral subscriber without its
  `Execute` message or receive timeout, hanging the caller on Akka's infinite
  default ask timeout. The original filter exception now faults the ask.
- **Saga-starter handshake keys batches by full originator path**: keying by
  bare entity id let two aggregate types sharing an id overwrite each other's
  reply-to (lost wakeup or a handshake-timeout FailFast). One subscribed
  saga's `Continue` counts toward every batch tracking it, so one subscribed
  saga satisfies all originators sharing its CID.
- **Connection strings are escaped before HOCON substitution**: Windows
  paths, SqlServer named instances, and quote-bearing passwords no longer
  break the HOCON tokenizer at startup.
- **Passivation cancels a saga's pending delayed commands**: they were only
  cancelled on abort and `StopSaga`, never on ordinary passivation or shard
  handoff, so a resurrected saga re-drove its schedules while the old timers
  were still armed (duplicate delivery with fresh command ids). `PostStop`
  now cancels them.
- **Snapshots store journal-only aggregate state**: snapshots stored the
  defer-polluted behavior state, so a `DeferEvent` fold could survive
  recovery through a snapshot. The aggregate now keeps a journal-only mirror
  (persisted events and replay only) and snapshots that, making snapshot
  recovery provably match journal recovery.
- **`sendAwaiting` awaits its race winner**: a subscription faulted by
  actor-system shutdown no longer passes for read-your-writes, and a
  command-timeout beyond ~49.7 days no longer overflows `Task.Delay`.
- **`CreateCID` rejects `~`**: the saga-correlation separator inside a CID
  broke correlation parsing and left sagas deaf.
- **`AkkaTimeProvider` matches the BCL contract**: due times beyond
  ~24.86 days no longer clamp (firing early), negative due times are rejected
  instead of becoming a silent never-fire, timestamps follow the virtual
  clock under `ObservingScheduler`, exactly one cancel event fires per
  cancel, and `DisposeAsync` is honored via `WatchAsync`.
- **`DynamicConfig.GetSectionAsDynamic` returns the requested section**: it
  returned the parent of the requested section and threw
  `KeyNotFoundException` on missing ones; it now descends the full section
  path and returns an empty object for missing sections.
- **Documented contracts**: `TargetActor.Sender` warns loudly and documents
  that it resolves to the journal or the mediator at side-effect time;
  `InitializeSaga`'s `applySideEffects` documents its idempotency contract
  for the raw API.

The Information-level `FCQRS.MessageFlow` logging ships as the stable
default; suppress it with a standard logger override. The facade suite
stands at 32 tests, including defer-snapshot recovery, CID separator,
special-character entity ids, throwing filters, HOCON connection strings,
and cross-type concurrent saga starts.

## 6.0.0 satellites (FCQRS.Model, FCQRS.Serialization, FCQRS.SQLProvider, FCQRS.ExpectoTickSpec)
- **FCQRS.Model — validation rules are null-safe and never seed empty errors**:
  the string rules threw on null input instead of recording the error,
  `List.pos_` threw on negative indices instead of failing the prism, and the
  validator polluted its error map with empty lists (`single` could even
  return `Error []`). Null now records a violation, negative indices fail the
  prism getter, and empty error lists are never emitted.
- **FCQRS.SQLProvider — `thenby`/`thenbydesc` compose instead of throwing**:
  a spliced quotation is statically `IQueryable`, so `ThenBy` always raised
  `ArgumentException`. Primary and secondary sorts now compose in a single
  query expression (all nine sort combinations verified); `thenby` without a
  preceding `orderby` fails by name via `invalidArg`.
- **FCQRS.Serialization / FCQRS.ExpectoTickSpec**: version alignment with the
  6.0.0 line; no code change since 6.0.0-rc2.

## 6.0.0-rc6 (FCQRS core)
- **Saga handshake state is threaded, not held in cells** (internal refactor,
  no behavior change): the two recovery flags introduced with the rc5
  re-drive fix sat in `ref` cells beside a receive loop that already threaded
  its state functionally. They are now an `Incarnation` DU (`Fresh` /
  `RecoveredFromJournal` / `RecoveredFromSnapshot`) inside a named
  `Handshake` record, so the impossible "recovered from a snapshot but not
  recovered" combination is unrepresentable, and the loop's three bool
  arguments are named fields instead of positional tuple elements — a
  transposition there was the same class of mistake rc5 fixed. The span and
  scheduler-cancelable cells stay: they are resource handles shared with the
  side-effect machinery, not loop state. Internal types only; no public API
  change.

## 6.0.0-rc5 (FCQRS core)
- **Saga starts deliver the starting event exactly once**: a fresh start ran
  the recovery re-drive (`recovering = true` at subscription ack), sending a
  spurious `ContinueOrAbort` that re-published the starting event to every
  same-CID subscriber — or, when a concurrent command had already advanced the
  originator, falsely aborted the just-started saga. The re-drive now runs
  only for genuinely recovered sagas (journal or snapshot replay).
- **Snapshot-recovered sagas complete the resurrection handshake**: recovery
  through a snapshot lost the `subscribed` flag, so a same-CID re-trigger of a
  completed saga dropped the re-delivered starting event and the originator's
  saga-start handshake FailFasted the process. Snapshots now restore the flag
  (the starting-event wrapper always predates a snapshot).
- **Delayed saga commands reach sharded targets**: `toOriginatorAfter` /
  `toAggregateAfter` scheduled the raw command to the shard region, which the
  message extractor rejected — the command was silently lost. Scheduled
  commands are now wrapped in a `ShardEnvelope`; `StopSaga` also delivers the
  delayed commands returned alongside it (Self-targeted ones excepted, with a
  warning, so a completed saga cannot resurrect itself).
- **Bounded waits**: command subscriptions and `sendAwaiting`'s projection
  wait now raise `TimeoutException` after `akka.fcqrs.command-timeout`
  (default 30s; a bare number means seconds, matching `saga-start-timeout`)
  instead of hanging the caller forever on `UnhandledEvent`/`IgnoreEvent`, a
  never-matching filter, or a suppressed notification.
- **Notification hub isolates slow subscribers**: one blocking callback pinned
  the shared BroadcastHub and silently starved every other subscriber.
  Consumers now run behind per-consumer DropHead buffers (with an async
  boundary), so a stalled subscriber sheds only its own backlog. Subscriber
  stream failures are logged instead of vanishing; notification publish
  failures during shutdown no longer FailFast the process.
- **C# host builder fails loudly on ambiguity**: two aggregates sharing a
  command/event type pair made the unkeyed `Handler<C,E>` DI registration
  resolve to the wrong shard region silently — it now throws with guidance,
  and keyed-by-shard registrations are always available. A second
  `AddProjection` call and `TimeProvider` misuse (`Timeout.InfiniteTimeSpan`
  firing immediately, timestamp units ~100x off outside Windows) are fixed
  likewise; `DynamicConfig` no longer crashes on sparse array keys.
- **Typed C# saga adapters report their boundary**: `SagaApi.InitSimple` only
  delivers the originator's `Event<'TEvent>`; other messages (ToSelf timeouts,
  other aggregates' replies) are now logged as ignored instead of silently
  dropped, and the docs point multi-aggregate sagas at the obj-based API.
  Null saga-starter definitions fail with the property named instead of an
  NRE that surfaced as a handshake timeout.

## 6.0.0-rc2 satellites (FCQRS.Model, FCQRS.Serialization, FCQRS.SQLProvider)
- **FCQRS.Model — Aether compose functions no longer crash for typed errors**:
  five `compose*` functions returned `Error (unbox<'e> "Invalid path")`, which
  threw `InvalidCastException` at `set` time whenever the error type parameter
  was not `string`. They now return the same well-typed error the previously
  fixed `>?>` operator does.
- **FCQRS.Serialization — non-public union case constructors**: the C# `union`
  converter binds case constructors regardless of visibility (previously
  unreleased hardening; journal format unchanged).
- **FCQRS.SQLProvider — paging composed Take before Skip**: `augmentQuery` with
  `take=n, skip=m` returned items m+1..n of the first n rows (and nothing when
  m >= n). Skip now composes before Take, giving conventional page semantics.

## 6.0.0-rc4 (FCQRS core)
- **`RunAsync` gets telemetry**: the runner runs off the mailbox, so the trace
  previously had a hole exactly where the latency lives. It is now spanned as
  `Dispatch:<CaseName>` (low-cardinality, `dispatch.type` tag honoring
  `Telemetry.IncludePayloads`), parented onto the originating command's trace
  and disposed when the runner settles; the domain outcome still shows in the
  child result-command span.
- **`RunAsync` from C#**: `EventActions.Dispatch(description)` builds the effect,
  and `ActorWiring.InitActorWithRunner` / `InitAggregateWithEffects` register an
  aggregate with a **Task-based** runner (`Func<object, Task<object>>`, bridged
  to the F# `Async` internally). Same ephemeral + total contract — catch every
  failure into a command in the Task.
- **Docs**: new how-to pages *Dispatch async effects* (RunAsync) and *Read your
  writes* (`sendAwaiting` + the journaled stamp), linked from the how-to index.

## 6.0.0-rc3 (FCQRS core)
- **`RunAsync` effect — a "mini saga" without persistence ceremony**: `decide`
  can now dispatch a short async side effect (e.g. an AI/oracle read) whose
  result becomes a command sent back to the same aggregate, re-entering
  `decide` (re-validated against current state) — all without standing up a
  saga. The effect payload is an **inspectable DATA description**, not a
  closure, so `decide` stays a pure `(command, state) -> effect` function you
  unit-test by structural equality:
  `decide cmd state = dispatch (ClusterThemes texts)` — no runtime, no oracle.
  The oracle lives only in the runner registered at
  `Fcqrs.aggregateWithEffects api def runner`; `dispatch desc` builds the
  effect and `total onError work` makes a runner body total.

  **EPHEMERAL** by design: the in-flight work is process state, NOT journaled —
  a crash / restart / shard rebalance mid-flight loses it silently. Use it only
  when that loss is tolerable; when the result must survive a crash, use a saga
  (which persists its intent). **TOTAL**: the runner must map every outcome
  (oracle error, timeout) to a command — an escaping exception fail-fasts the
  process, like a throwing fold. Additive: `EventAction` gains a `RunAsync`
  case and `IActor` an `InitializeActorWithRunner` method; `Fcqrs.aggregate`
  and existing `decide`/`fold` are unchanged. Facade.Tests pins the pure-decide
  equality, the success self-dispatch, and the total-failure path.

## 6.0.0-rc2 (FCQRS core + FCQRS.ExpectoTickSpec)
- **Delivery stamp: journaled vs deferred acks are now distinguishable**:
  aggregate delivery stamps `fcqrs:journaled` = `true`/`false` into the
  Metadata of every OUTBOUND event envelope (the CID-correlated ack and the
  saga/mediator publish) — `true` for persisted events, `false` for
  deferred/publish-only replies. Only the delivered copy is stamped; the
  journal record stays clean. New `Event.Journaled: bool option` reads it
  (`None` for envelopes that never passed aggregate delivery, e.g. journal
  reads or pre-stamp senders). This closes the gap where read-your-writes
  callers could not tell whether a projection event would ever follow an
  ack — a deferred rejection awaited naively hangs until timeout.
- **`Fcqrs.sendAwaiting` — read-your-writes as one call**: subscribes on the
  CID before sending (the ordering that makes the wait race-free), sends,
  and awaits the projection only if the ack was journaled. Kills the
  per-call-site "was this persisted?" predicates consumers had to write, and
  makes the safe subscribe-before-send ordering impossible to get backwards.
  Awaits exactly one projected event; batch persists should Subscribe with
  an explicit take. Covered in Facade.Tests: persisted acks stamp true with
  the read model consistent on return, deferred acks stamp false and return
  without any phantom wait.
- **Low-cardinality span names + payload switch** (was Unreleased; ships in
  core rc2): aggregate span names are the case name only
  (`Command:Register`, `Event:Registered`, `Abort:VerificationRequested`),
  matching saga spans — .NET 11 rule-based `AddTracing` can target specific
  FCQRS operations, trace viewers group by operation, and payload values
  stop leaking into indexed span names (payloads still ride in span tags and
  message-flow logs). Process-wide `Telemetry.IncludePayloads` switch and
  `FcqrsBuilder.WithPayloadDiagnostics(false)` for sensitive domains.
- **FCQRS.ExpectoTickSpec 6.0.0-rc2 — Gherkin `@focus` / `@pending` tags**:
  focus and pending are now tag-driven (TickSpec merges feature- and
  rule-level tags into every scenario, so the tags work at any level) —
  `@focus` maps to Expecto Focused, `@pending` to Pending, and `@pending`
  wins when both apply. A feature whose scenarios are all pending (e.g. one
  `@pending` above `Feature:`) is built from parsed SOURCE without binding
  steps, so specs written ahead of their implementation join the suite as
  pending scenarios instead of failing step binding (scenario-level
  `@pending` still requires the file's other steps to bind). The legacy `_`
  name-prefix focus remains, though note it cannot match TickSpec scenario
  names (they carry the `Scenario: ` prefix) — tags are the reliable form.
  Covered in Facade.Tests by a pending scenario with a deliberately wrong
  expectation, a fully-unbound pending feature, and a focus tree-shape
  assertion.
- **FCQRS.ExpectoTickSpec joins the 6.0 wave (breaking)**: `FeatureTest` now
  takes the consumer's `Assembly` explicitly —
  `createTest (assembly: Assembly) (resourcePrefix: string) (baseFeatureName: string)`
  (previously the first string parameter doubled as the resource prefix and the
  assembly was resolved via `GetExecutingAssembly()`). As a compiled package
  the old resolution bound to the library itself, scanning an assembly with no
  step definitions — which is why consumers had to vendor the source file to
  use it at all. `StepDefinitions` are now cached per assembly, the project
  carries package metadata at 6.0.0-rc1, and ci.yaml packs/publishes it with
  the other packages. A cross-assembly regression test (`bridge.feature` +
  steps in Facade.Tests, driven through the referenced library) pins the fix.
  The `_` name-prefix focus behavior (ftestList/ftestCase) is unchanged.

## Unreleased
- **.NET 11 preview 6 compatibility verified**: the in-box union support types
  (`System.Runtime.CompilerServices.UnionAttribute`/`IUnion`) match FCQRS's
  name-based detection, and FCQRS's `$case`-discriminated journal format takes
  precedence over System.Text.Json 11's new caseless native union
  serialization (same-shaped cases round-trip correctly; verified end to end
  with a net11.0 consumer against the rc1 packages). Serializer hardening for
  the preview 6 language rules: union case constructors may now be non-public,
  so case discovery reflects non-public single-parameter constructors too
  (copy constructors excluded). Ships with the next FCQRS.Serialization
  publish.

## 6.0.0-rc1
Release candidate for 6.0.0 — the API is frozen from here barring rc-breaking
bugs. All four packages (`FCQRS`, `FCQRS.Model`, `FCQRS.Serialization`,
`FCQRS.SqlProvider`) align on this version.

- **API-freeze cleanup** (breaking vs preview28, all on unused or obsolete
  surface): removed the seven `[Obsolete]` C# shims (`AsyncExtensions`,
  `Helpers`, `Results`, `StringTypes`, `IActorExtensions`,
  `QueryApi.InitWithList`, nested `ISubscribeExtensions` — use the
  namespace-level replacements); renamed `SagaCommands.To*Delayed` →
  `To*After` to match the F# facade; internalized framework plumbing that was
  never meant to be called (`ContinueOrAbort`/`AbortedEvent`, `SagaBuilder`
  wrappers, `Saga.init`, HOCON config providers, scheduler internals,
  `AkkaTimeProvider`, `Query.Internal`).
- **FCQRS.Model cleanup**: `Validator.IsDegist` → `IsDigit`; removed the
  mis-cased `ValueLens.Isvalid` duplicate, the mutable-singleton `isValid*`
  helpers, and the unused `IQuery`/`DataEvent` module; `FreeMonad` moved from
  the global namespace to `FCQRS.Model.FreeMonad`.
- **New test coverage**: restart detection (the `ContinueOrAbort` version
  handshake → `AbortedEvent`) is now exercised end to end across a
  kill-and-reboot, alongside the existing saga-snapshot-recovery and
  atomic-batch tests.
- Includes everything from 6.0.0-preview28 below.

## 6.0.0-preview28
- **Message-flow logging, on by default**: the command/event/saga narrative is
  now readable straight from the console, no tracing pipeline required. Every
  aggregate command logs what it yielded (`Command Register ... to aggregate
  testuser (v0) yielded PersistEvent (VerificationRequested ...)`), every
  persisted/deferred event, every saga state transition, every command a saga
  sends or schedules (with target and delay), every event a saga picks up (with
  the decision), and saga completion — all at Information level under the
  dedicated `FCQRS.MessageFlow` category, each line carrying the CID so one
  grep follows a whole workflow. Payloads render single-line; the internal
  `ContinueOrAbort` handshake is excluded. Toggle process-wide with
  `Telemetry.MessageFlowLogging <- false` or
  `builder.WithMessageFlowLogging(false)`, or filter the category in logging
  configuration (`"FCQRS.MessageFlow": "None"`).
- **Failures flagged in traces**: command spans get Error status on
  `UnhandledEvent` (the classic silent-hang) and on `StateChangedEvent` from an
  aggregate; the restart-detection version mismatch emits an Error `Abort:`
  span and marks the saga's state span before passivation; the saga state span
  gains timestamped `command.issued`/`command.scheduled` events per side-effect
  command. New `Telemetry.FatalFlush` hook (set it to your tracer/logger
  `ForceFlush`) runs bounded inside the fail-fast path, after in-flight spans
  are marked with the OTel exception event — so the fatal flow's own telemetry
  gets out before the process dies.

## 6.0.0-preview27
- **Conditional persist/defer helper**: `EventActions.PersistConditionally(shouldPersist, event)`
  (C#) and `persistIf shouldPersist event` (F#) collapse the common
  `cond ? Defer(e) : Persist(e)` ternary — persist the event when the guard holds,
  otherwise defer it (still returned to the caller, so read-your-writes observes
  it, but not written to the journal). The idempotent "emit this verdict, write it
  only once" shape, e.g. re-approving an already-approved aggregate.

## 6.0.0-preview26
- **Filtered single-event projection handlers**: the middle rung between the
  `preview24` unit/void handler (publish every event) and the list-returning
  ("multi") handler (notify anything). The handler updates the read model and
  returns `Publish` or `Suppress` to say, per event, whether it should wake
  subscribers — the common "publish each event except the intermediate ones"
  case (e.g. suppress a pending-creation event so read-your-writes wakes only on
  the saga's terminal verdict) without building a notification list.
  F#: `Projection.filtered` (and `Query.filterPublish`, the adapter behind it);
  C#: `AddProjection((offset, evt) => evt switch { ... })` `Func<long, object,
  Notify>` overloads (direct + DI) and `QueryApi.Init(..., Func<long, object,
  Notify>)`. The `Notify` discriminated union (`Publish | Suppress`) lives in
  `FCQRS.Common`. The unit and list overloads are unchanged.

## 6.0.0-preview25
- **Single-type-argument `AddAggregate` / `AddSaga`**: the concrete class already
  names its state/command/event types on its `Aggregate<,,>` / `Saga<,,>` base,
  so registration no longer repeats them — `.AddAggregate<DocumentShard>()`, and
  `.AddSaga(create: sp => new QuotaSaga(...), startOn: ...)` with `TSaga`
  inferred from the lambda. Resolved via reflection once per registration at
  host-composition time; the explicit four-type-argument overloads remain.

## 6.0.0-preview24
- **Single-event projection handlers**: the common projection — update the read
  model and notify with the event itself — no longer needs a hand-written
  notification list. The handler just returns unit/void and FCQRS publishes
  each journal event that is an `IMessageWithCID` (every aggregate `Event<'T>`;
  saga internals never qualify, so they are never published).
  F#: `Projection.single` (and `Projection.multi` for the existing
  full-control shape); C#: `AddProjection((offset, evt) => { ... })`
  `Action` overloads (direct + DI) and `QueryApi.Init(..., Action<long, object>)`.
  List-returning ("multi") handlers are unchanged and remain the way to filter
  notifications, e.g. suppressing intermediate events for read-your-writes.

## 6.0.0-preview23
- Notification buffer config hardening: the queue takes
  config:akka:fcqrs:notification-buffer verbatim (any positive size),
  while the BroadcastHub - which requires a power-of-two buffer - gets
  the value rounded down to a power of two, clamped to [8, 4096].
  Previously a non-power-of-two setting crashed stream materialization.

## 6.0.0-preview22
- **Complete delayed/self side-effect helpers**: the saga's scheduled-command
  concept (ExecuteCommand.DelayInMs) is now reachable for every target on both
  surfaces — F#: `toSelf`, `toSelfAfter`, `toAggregateAfter`, `toActorAfter`
  (joining `toOriginatorAfter`); C#: `SagaCommands.ToSelf`, `ToSelfDelayed`,
  `ToAggregateDelayed`, `ToActorDelayed` (joining `ToOriginatorDelayed`).
  `toSelfAfter` is the idiomatic saga timeout: enter a state, schedule a
  reminder to yourself, and HandleEvent decides whether it still matters.

## 6.0.0-preview21

### Snapshots
- **`SnapshotPolicy`** (`Default` / `NoSnapshots` / `Every n`): per-aggregate and
  per-saga snapshot cadence, set on the F# definition record (`Snapshots = ...`)
  or by overriding the virtual `SnapshotPolicy` property on the C# base classes.
- **`WithDefaultSnapshotPolicy(...)`** on the C# host builder: what `Default`
  resolves to for every entity it registers. Resolution: entity override →
  builder default → `config:akka:persistence:snapshot-version-count` → 30.
- **`PersistAndSnapshot`**: persist an event and save a manual snapshot
  checkpoint once it is durable, independent of cadence.

### Events
- **`PersistAllEvents` / `EventActions.PersistAll` / `persistAll`**: several
  events from one command persisted as a single journal `AtomicWrite` —
  all-or-nothing, sequential versions, nothing published until the whole batch
  is durable (preview20).

### Logging & telemetry
- **`WithAkkaLogging(AkkaLogLevel...)`**: enable Akka's internal logging
  (shipped OFF) from the fluent builder, no HOCON editing.
- **Telemetry rebuilt**: trace context now rides `Metadata["traceparent"]`
  (stamped automatically from `Activity.Current` at command creation) and flows
  command → events → sagas → the saga's commands → projections. New
  `FCQRS.Query` ActivitySource closes traces end-to-end; all span sites are
  gated on `HasListeners()` (zero overhead when off); CIDs stay plain GUIDs.
  Register with `tracing.AddSource(Telemetry.AllActivitySources)`.

### Journal manifests
- **Stable logical type names**: register payload types once
  (`Fcqrs.journalTypes [ journalType<Document.Event> "doc.event" ]` /
  `.WithJournalTypes(m => m.Type<DocumentEvent>("doc.event"))`) and journal
  manifests become `fcqrs:ev(doc.event)` instead of CLR
  AssemblyQualifiedNames — CLR types can then be renamed or moved freely;
  only the mapping changes and old rows keep deserializing
  (`JournalTypes.Remap` for deliberate re-pointing, aliases supported).
  Pre-existing journals and unregistered types keep using AQN manifests via a
  read-side fallback: no migration needed, ever.

### Reliability
- Read-your-writes notification queue: overflow now drops the oldest
  unconsumed notification (`DropHead`) instead of faulting the stream;
  buffer size via `config:akka:fcqrs:notification-buffer` (default 1024).

## 6.0.0-preview19
- **Saga snapshots carry the starting event**: a saga recovered through a
  snapshot used to wake with no starting event and silently skip its recovery
  re-drive (pending commands never re-issued). Snapshots now persist it;
  old-shape snapshots still load and re-drive with degraded metadata.

## 6.0.0-preview18
- **Read side self-heals**: a journal-read error used to silently COMPLETE the
  projection stream (frozen read models in a healthy-looking process); now
  `RestartSource` with backoff, resuming from the last processed offset.
- **One crash policy**: aggregates' `HandleCommand`/`ApplyEvent` and the
  serializer (both directions) now FailFast on error like sagas already did,
  instead of quiet actor restarts / silently stopped entities.

## 6.0.0-preview17
- **Saga-start handshake deadlock closed**: a saga resurrected mid-handshake
  (transient persist failure + remember-entities) never re-signaled the
  SagaStarter while the originator stayed parked in an unbounded ask — a
  permanent process-local deadlock. Recovery now re-signals Continue, and the
  handshake ask is bounded (default 30s, `config:akka:fcqrs:saga-start-timeout`)
  with FailFast on expiry.
