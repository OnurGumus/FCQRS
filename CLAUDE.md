# FCQRS contributor guide

FCQRS is an F# framework for event-sourced CQRS applications on Akka.NET. It exposes F# and C# APIs
for aggregates, projections, sagas, correlation subscriptions, persistence, snapshots, and cluster
sharding.

The repository targets .NET 10. Use the SDK selected by `global.json`.

## Documentation goal

The documentation is a learning path from a first aggregate to production operation. A reader should
be able to learn FCQRS without already knowing CQRS, event sourcing, actors, or sagas.

Write documentation in this order:

1. Show one concrete domain example.
2. Explain the FCQRS concept that the example needs next.
3. State the guarantee at its actual boundary.
4. Show the failure mode and the application's remaining responsibility.
5. Link to the next runnable or task-focused page.

Use the accounts example across adjacent pages unless a different domain is necessary. Introduce only
the types needed for the current step.

### Documentation style

- Start with the subject. Do not open with an analogy, rhetorical question, or invented story.
- Prefer an executable example to a paragraph that describes hypothetical code.
- Define a term when the example first needs it.
- Use direct sentences. Avoid promotional claims and artificial suspense.
- Do not guess what the reader knows, feels, or finds difficult.
- Do not call a concept simple, magical, automatic, bulletproof, or enterprise-ready.
- Do not use an em dash as a substitute for sentence structure.
- Explain why ordering matters when code depends on subscribe-before-send, transaction boundaries, or
  registration order.
- Keep F# and C# examples semantically equivalent.
- Treat persisted names and event shapes as compatibility contracts, not ordinary refactoring targets.

Good prose makes the example easier to reason about. It does not narrate the teaching process.

## Learning-path structure

- `README.md` gives repository orientation and the smallest domain example.
- `docs/overview.fsx` explains why FCQRS separates aggregates from read models and maps both
  tutorials.
- `docs/tutorial/` ("Tutorial: CQRS" in the sidebar) builds a small bank in order, from the first
  aggregate to a transfer workflow that the application starts and changing stored events. Each step
  has a runnable F# and C# program under `samples/accounts/`; step 6 continues from step 5's
  database, so both build an assembly named `Accounts`.
- Sagas are the older coordination mechanism: supported for existing applications and their
  journals, not recommended for new code. `concepts/sagas.md` and `how-to/write-a-saga.md` say so
  first and quote `samples/accounts/transfer-with-a-saga`.
- `docs/workflow-tutorial/` ("Tutorial: Workflows") builds an order workflow in ten steps, from one
  activity to changing a workflow while orders run. Step 10, "Feed orders from a stream", starts
  orders from an Akka.Streams flow and stops the process while two of them ship. Each step has an F#
  and C# program under `samples/orders/`, and its page shows every line that is new or changed since
  the previous step: `scripts/check-learning-snippets.py` fails otherwise. Steps 8 and 9 continue
  from the previous step's database. `scripts/check-orders.py` runs every step and checks the
  output its page shows. The track does not require the CQRS tutorial.
- `docs/concepts/` explains the underlying models and boundaries. Workflows span three pages:
  `workflows.md` (the model, recovery, failures as values, cancel, versioning),
  `workflow-steps.md` (signals, parallel steps, loops, children, commands, streams), and
  `schedules.md`. `free-programs.md` explains `FCQRS.Model.FreeMonad`, and the how-to
  `run-a-free-program-as-a-workflow.md` runs one with `Free.toWorkflow`.
- `docs/how-to/` provides focused task instructions.
- `docs/configuration.md` is the runtime configuration reference.
- `docs/schedules-reference.md` is the schedules reference: the exceptions each call raises, ID
  limits, dispatcher timing, the index table and its database rights, deploy cases, and persistent
  contracts. The schedules concept page keeps the example, the guarantee, and the
  behaviour rules, and links there for the rest.
- XML comments in `src/FCQRS/` feed the generated API reference.

Do not duplicate a full tutorial in the README or a concept page. Link readers to the canonical next
step instead.

## Guarantees and boundaries

Keep these distinctions intact in code comments and documentation.

### Aggregate

- One actor processes one aggregate instance's commands sequentially.
- This eliminates races over state inside that aggregate boundary.
- It does not serialize different aggregates, projections, databases, or external services.
- `PersistEvent` stores an event, applies it to state, and publishes it.
- `DeferEvent` publishes and folds a reply without storing it or changing the persisted version.
- Only a persisted event starts a saga. A deferred reply, such as a repeated verdict, does not.
- A fold should leave state unchanged for a deferred rejection or repeated verdict. A state change
  caused only by a deferred event disappears on recovery.
- A command handler waits for the matching aggregate reply. It does not wait for a read model.

### Event history and recovery

- Stored events are the source of aggregate state.
- A fold must be deterministic because it runs during normal processing and replay.
- Snapshots shorten replay. They do not replace or repair the journal.
- `EntityName`, journal type names, event cases, and serialized fields are persistent contracts.
- Stable journal names allow CLR or F# type moves. They do not migrate changed payload fields.

### Projection

- A projection updates query data asynchronously from the journal.
- A projection follows each aggregate's and saga's own sequence numbers, never a global journal
  number, so it cannot skip an event that commits after later-numbered ones. Each pass applies the
  events it found in journal order across aggregates: on SQLite, the order they were written. On
  PostgreSQL, and on SQL Server with `READ_COMMITTED_SNAPSHOT`, an event that commits after
  later-numbered ones is applied in a later pass, after them. SQL Server's default read committed
  makes the journal query wait for that write instead.
- Commit a read-model update and its progress in the same transaction when they share a store. A
  projection that stores progress after its handler returns can hand an event to it again.
- A projection with `Named` progress (a named C# `AddProjection`) runs its handler outside the
  transaction that stores the position, so the handler can wait for a journal write, such as a
  workflow start. A transactional projection runs its handler inside that transaction, which holds
  SQLite's single writer lock: on SQLite its handler must not wait for a journal write.
- A projection that names entities reads only their persistence IDs (`<escaped name>/...`, escaped
  as Akka escapes a sharding region's name): its SQL selects those rows, so other entities' events
  are never fetched or deserialized. On SQLite and SQL Server the full scan reads only the names'
  range of the persistence-ID index; the queries bounded by a journal number read every row above
  it (`NOT INDEXED` on SQLite, `INDEX(1)`, the clustered journal-number key, on SQL Server),
  because the index range would make each call read the entity's whole history. PostgreSQL reads
  other rows in every query. A filtered pass reads each persistence ID's events with its own query, so
  interleaved identities cost about one query per one or two events at startup. A pass still
  applies the named entities' events in journal order. An event of an entity it does not name never
  reaches it. An unnamed projection in an application that has schedules also receives
  `Event<ScheduleEvent>`.
- Most projection queries read only recent writes. A write that commits more than
  `LateWriteWindow` after taking its journal number is handled at the next full scan, so
  `CatchUpAsync` can return before it.
- Read-your-writes means one selected projection has published the matching notification.
- It does not mean every projection or external system is current.
- Subscribe before sending. Subscribing afterward can miss the notification.
- Projection subscriptions are in-memory request coordination, not a durable client queue.
- Projection names that start with `fcqrs-` are reserved for FCQRS's own projections, which start
  through the internal `startOwned`. Every user registration refuses them with `ArgumentException`.

### Saga

- Sagas are the older mechanism. New code coordinates aggregates with a workflow that the
  application starts; a workflow that must follow an aggregate event is started by a named
  projection whose workflow ID comes from the event.
- A saga stores workflow progress and issues commands across aggregate boundaries.
- Recovery re-drives side effects for recovered state. Commands it issues must be retry-safe or
  explicitly recovery-aware.
- A saga recovered before it leaves `Started` continues if its originator stored its starting event,
  and ends otherwise.
- A saga starts once per correlation ID and originator aggregate. A command that would start it again
  stores nothing and fails with `SagaAlreadyStartedException`.
- A persisted saga state cannot make an external operation exactly once.
- External calls need idempotency keys, timeouts, retry policy, and compensation or intervention paths.
- Avoid circular workflows and give every expected event a domain timeout, either hand-rolled with
  `toSelfAfter` or declared with `StayExpecting`.
- A `StayExpecting` deadline is measured from the persisted state-entry time; the reminder timer is
  best-effort and re-arms on recovery, so restarts cannot postpone the deadline.
- `ExpectationExhausted` means the outcome is unknown, not failed. The domain must answer it with a
  transition, and the escalated state must expect that the original reply may still arrive.

### Workflow

- A workflow instance stores each step's result and replays its journal on recovery. A stored step
  result is never computed again.
- A step that was running when the process stopped runs again. An activity runs at least once, and
  the external system must honour its idempotency key.
- The body must be deterministic. It reads time, GUIDs, and random numbers through steps and does
  I/O only in activities. FCQRS cannot detect a body that breaks this.
- A workflow starts by ID, with no aggregate event. A second start with the same ID stores nothing,
  unless the instance faulted or was cancelled and the start passes `WorkflowIdReuse.AfterFailure`:
  then it begins a new run, a second `Started` record in the same journal. A run replays none of the
  earlier runs' steps or signals: it is not a resume, and steps that succeeded before the fault run
  again, so the failed run must have compensated or the activity must pass its own business key. From run 2 on, idempotency keys, command IDs, and child IDs use
  `<ID>#<run>`; run 1 keeps the plain ID. That format is a persistent contract, and IDs refuse `#`.
  A finished instance stays final, and a running or parked one is never replaced.
- A retry policy's deadline is measured from the stored start of the step. Attempt counts restart
  with the process. An activity whose deadline passed while the process was stopped fails without
  another attempt.
- Every activity has a time limit: `Retry.once` and `Retry.times` take an attempt timeout, and
  `activity` refuses a policy with neither `AttemptTimeout` nor `Deadline`. An attempt that runs
  out fails even when its call ignores the token; the call keeps running. Circuit breakers and
  rate limits belong inside the activity, not in the step's policy.
- A command step subscribes before it sends and resends with the same correlation ID after a
  restart. The aggregate must answer a repeat with its first verdict, checked before anything the
  first command changed. No reply by the deadline is `OutcomeUnknown`, not a rejection.
- Every send of a command step carries the same correlation ID and the same command ID, both made
  from the step's idempotency key. The step takes only an event that one of its sends caused.
  `sendWithFilter` takes the first event its filter accepts under the step's correlation ID,
  whichever command caused it. That wait is in memory: an event published while no attempt listens
  is not seen. One send waits for the policy's `AttemptTimeout`, or for the command timeout when
  the policy sets none.
- After a restart, a command step and a child start make whole attempts, because a repeat is safe
  and the first outcome is unknown. The deadline ends them only when an attempt fails after it.
- Cancelling a parent, also a parked one, cancels the child it waits for and a child whose start is
  in flight, including a start re-run after a restart. A child it started and does not wait for
  keeps running. The parent sends these cancels again whenever it recovers after the cancel was
  stored; an ended parent recovers only when a request reaches it.
- An activity result is read back before it is stored. One that cannot be read fails the step as
  `NotRetryable` after the call was made.
- A signal is stored before it is acknowledged. A race stores its winner.
- A workflow has no snapshot. Recovery replays the whole journal, so a loop without a bound makes
  recovery slower with every round.
- A body may recurse to any depth. `bind`, `try`, and the C# await path are data that `force`
  reassociates in a loop, so neither a pending `let!` nor a `try` grows the stack.
- An instance whose journal does not match its code is parked, and the process keeps running. This is
  a deliberate exception to the fail-fast rule: running instances restart with the process, so a
  crash would repeat at every start.
- A body that ends or faults before a step its journal holds is parked too, so removing trailing
  steps parks the running instances that stored them. Stored signals and version markers are not
  steps, and the stored steps of a race's losers or of a failed block's other branches do not count.
- Nothing restarts a faulted workflow by itself. `Resume` stores a `Resumed` record that sets aside
  the failure of the step that faulted it; the body replays its stored steps and runs that step
  again with the same idempotency key, so a service that stores failed answers per key returns the
  same failure; a new run gives new keys. It is refused for an instance that has not faulted, a body
  that threw its own exception, and a body that ran steps after the failure. A start with
  `AfterFailure` instead begins a new run from the first step.
- An instance leaves memory when it ends. A request to an instance that is not running recovers it
  by replaying its journal, and it then stays in memory until it has had no request for
  `akka.fcqrs.workflow-idle-timeout`.
- Step names, step kinds, the workflow name, and stored payload types are persistent contracts.
- `WorkflowTest` (`Testing.fs`) drives the same machine as the actor, without one, and must store
  the same records in the same order; a facade test compares it with an actor's journal. Steps do
  not run and retry policies are not applied: a test gives each step's final outcome. `CheckReplay`
  restarts after each record, never inside one write the test gave. `Recover` runs code over a
  stored journal; `Resume` and `StartAgain` mirror the handle's.
- A workflow record is journaled under the CLR name of its `WorkflowRecord` case, which a projection
  can read before any workflow is registered. The type's name, namespace, and assembly are
  persistent contracts too.
- A schedule is an instance of the aggregate `fcqrs-schedule`, which decides every run. The
  dispatcher's index is a hint: a stale index cannot start a run for a paused or deleted schedule,
  or for a due time that a change removed. The aggregate takes snapshots, so the aggregate name,
  `ScheduleEvent`, `ScheduleRule`, `MonthDay`, `CatchUp`, `ScheduleSpec`, and the snapshot state
  `ScheduleState` and `AcceptedDue` are persistent contracts. Commands are not journaled.
- Schedule rules are `Daily`, `Weekly`, `Every`, and since 7.8.0 `DailyAt`, `WeeklyAt`, and
  `Monthly`, whose days are `MonthDay.Day` 1 to 31 (the last day in a shorter month), `LastDay`,
  `Nth` 1 to 4 of a weekday, and `Last` of a weekday. A rule's days and times form one set of local
  date-times, so a date-time named twice is due once. A skipped local time moves forward in whole
  minutes to the first valid one and keeps its seconds; a repeated one is due at its first
  occurrence. Due times are ordered as instants, not as local times. `missed` reads backwards from `now`, so its cost
  depends on the due times it keeps, not on the gap. `create` and `change` sort and deduplicate days
  and times before the same-content comparison. The 7.8.0 cases are added after the 7.7 ones, whose
  stored JSON and index text are unchanged; 7.7 cannot read the new ones.
- A schedule's run starts at least once per accepted due time, unless its workflow cannot read the
  stored input (below). Its workflow ID is `<schedule ID>@<due time in UTC, "O" format>`, so a
  repeated start stores nothing. The run's input is `ScheduledRun { Schedule; Due; Input }`. Run IDs
  and `ScheduledRun` are contracts. The escaped workflow name and schedule ID must fit in 205
  characters, so a run's persistence ID fits in 255.
- A run decided before a change, pause, or delete still starts, on the workflow and input stored
  when it was decided. Due times inside a pause never run. A deleted schedule ID cannot be created
  again. A fire more than one second early is refused.
- `CatchUp.Latest` runs the most recent missed due time, `All` each missed one up to the 1000 most
  recent, and `None` none, with a one-minute window for a late fire. `Latest` is the default:
  `Schedules.createLatest` and C#'s `CreateAsync` without a policy use it; `create` takes one.
- A schedule whose workflow the dispatcher does not list logs an error at each due time and fires
  nothing until a later start lists it.
- A decided run whose stored input its listed workflow cannot read is lost: the dispatcher logs one
  error naming the schedule, due time, workflow, and exception, sends `Started`, and the schedule
  moves on. The error log is the only signal. No stored event shape changes; `Fired` records it.
- `Schedules.state` reads the dispatcher's index, which lags the aggregate by one projection pass.
- The index is stored in `fcqrs_schedule_index` in the journal's database, one row per schedule,
  by the projection `fcqrs-schedule-index`, each row in the transaction that stores its read
  position. A restart reads one row per schedule plus newer events; nothing fires before both are
  read. The table is derived from the schedule events: a missing table or an unreadable row makes
  the dispatcher delete the rows and positions and reread the history, as the first start on a
  7.6.0 journal does once. The table, its row text, and the projection name are persistent
  contracts.
- The schedule aggregate, like every aggregate, waits for the saga starters before it stores an
  event and terminates the process if they are not wired within `saga-start-timeout`. Schedule
  commands raise `InvalidOperationException` and the dispatcher fires nothing until they are wired.
  `create` and `change` validate the rule before sending: `decide` must never raise, because the
  actor treats that as fatal. One dispatcher per actor system; a hung start delays only its own
  schedule. Schedules give no exactly-once side effect and no precision below a second.

### Databases

- Every Akka.Persistence.Sql provider stores aggregates, sagas, and workflows. Projections and
  schedules run their own SQL (`ProjectionStorage.fs`, the schedule index), written for SQLite,
  PostgreSQL, and SQL Server; another provider raises `ArgumentException` when one is registered.
  The SQL for SQLite and PostgreSQL must stay byte-for-byte what it was when a dialect is added.
- On SQL Server the database's default collation must be binary, and existing journal and snapshot
  persistence-ID columns must use it: Akka creates them without a collation, and a case-insensitive
  one makes `Account/0/abc` and `Account/0/ABC` one journal key. `SqlServer.checkPersistence`
  refuses to start the actor system otherwise. Projections also need compatibility level 130 for
  `OPENJSON`. A binary collation makes catalog names case-sensitive, so tests and docs write
  `INFORMATION_SCHEMA` in upper case.

### Deployment

- FCQRS runs on one node. Nodes do not form one cluster (each joins itself), and a command's reply
  can be lost between nodes. Do not document multi-node deployment as supported.

### Best-effort async effects

- `RunAsync` work is ephemeral. A stop, restart, or shard move can lose it.
- Its runner must convert every outcome into a command. An escaping exception terminates the process.
- Use a saga when the work must survive recovery.

## Architecture map

- `src/FCQRS.Model/`: validated domain values and query model types. `FreeMonad.fs` holds `Free`,
  whose `bind` on .NET queues continuations as data (`Queued`) so nested binds stay linear and
  stack-safe; the Fable path keeps the closure form, because the package is also a Fable library.
- `src/FCQRS/Actor.fs`: aggregate persistence, replay, snapshots, and command delivery.
- `src/FCQRS/Saga.fs`: saga state machines, recovery, and command dispatch.
- `src/FCQRS/Query.fs`: correlation subscriptions and projection notifications.
- `src/FCQRS/SqlServer.fs`: the startup check of a SQL Server database's collation and level.
- `src/FCQRS/ProjectionStorage.fs`: the projection store's SQL for each dialect, its progress and
  lock tables, and the tables FCQRS owns, such as the schedule index.
- `src/FCQRS/Projections.fs`: projections, their per-aggregate progress, catch-up, and retries.
- `src/FCQRS/FSharp.fs`: F# facade.
- `src/FCQRS/CSharpInterop.fs`: C# types and lower-level interop.
- `src/FCQRS/HostExtensions.fs`: C# host-builder and dependency-injection registration.
- `src/FCQRS/default.hocon`: embedded Akka.NET defaults.
- `src/FCQRS.Workflows/`: durable workflows. `Workflow.fs` holds the builder and the steps,
  `Free.fs` `Free.toWorkflow`, which runs a `FreeMonad` program as steps, `Machine.fs` the pure replay interpreter, `WorkflowActor.fs` the persistent actor, `CSharp.fs`
  the C# API with its async method builder, `Testing.fs` the in-memory `WorkflowTest`,
  `Streams.fs` the Akka.Streams flows, and `Schedules.fs` the schedule rules, the schedule
  aggregate, and the dispatcher that starts runs and keeps its index in a table.
- `samples/workflows/`: the quote workflow in F# and C# on .NET 10, with project references. The
  workflow task guide quotes it.
- `samples/workflows/fsharp-tests/` and `samples/workflows/csharp-tests/`: Expecto and xUnit tests
  of the quote workflow with `WorkflowTest`, linking the sample's workflow file. The workflow test
  guide quotes them.
- `samples/resilience/`: a price workflow whose activity runs a Polly circuit breaker and
  concurrency limiter, in F# and C# with project references. The Polly guide quotes it.
- `samples/approvals/`: the quote workflow from `samples/workflows`, compiled unchanged, with a
  transactional projection that lists the quotes waiting for approval, a second one that lists every
  instance with its status, and a `Status` check before a signal, in F# and C# with project
  references; the C# program registers both with `AddKeyedTransactionalProjection`. The waiting-workflows guide quotes it.
- `samples/reservations/`: a workflow per reservation that applies one change at a time and, after
  each, only the newest of the changes signalled meanwhile, in F# and C# with project references.
  The newest-change guide quotes it, and `test/Facade.Tests/ReservationTests.fs` links its F#
  workflow.
- `samples/start-from-an-event/`: a named projection that starts a workflow for each `Opened` event
  an account stores, in F# and C# with project references. CI runs each program twice, so the
  second run checks that nothing starts again. The how-to "Start a workflow from an aggregate event"
  quotes it.
- `samples/orders/`: the workflow tutorial's steps as equivalent F# and C# programs on .NET 11,
  with the published packages. Its `Directory.Build.props` keeps the repository's settings out.
- `samples/accounts/`: the tutorial's steps as equivalent F# and C# programs on .NET 11. The C#
  programs use C# 15 unions, and `samples/accounts/global.json` selects the .NET 11 SDK. The task
  guides quote these programs too; `serve-over-http/` puts step 4 behind ASP.NET Core endpoints, and
  `transfer-with-a-saga/` is step 5's transfer as a saga, compiling step 5's account and statement.
- `samples/registration-*/` and `samples/getting-started-*/`: older .NET 10 samples kept for CI checks
  and persisted-compatibility checks. The C# interop page links `registration-csharp` as a .NET 10
  example.
- `sample/` and `saga_sample/`: executable examples.
- `test/Model.Fable.Tests/`: an F# program that Fable compiles to JavaScript and Node runs, to
  check that FCQRS.Model compiles under Fable and that free programs behave the same there. Run it
  with `python3 scripts/check-fable.py`; Fable is pinned in `.config/dotnet-tools.json`.
- `test/Workflows.CSharp.Tests/`: a C# program that checks the C# workflow API as the C# compiler
  builds it. Run it with `dotnet run --project test/Workflows.CSharp.Tests -c Release`.
- `test/Facade.Tests/`: facade and behaviour tests. `WorkflowMachineTests.fs` tests replay without an
  actor system. Every test actor system runs Akka's `serialize-messages` through
  `VerifySerialization.fs`, and the last test names each message type that another node could not
  read. `BankInvariantTests.fs` compiles step 5's F# `Account.fs` and the saga sample's
  `TransferSaga`, and `WorkflowBankInvariantTests.fs` step 5's `Account.fs` and
  workflow `Transfer.fs`. Both kill processes during transfers and check that the total balance is
  unchanged, on SQLite and, when `FCQRS_TEST_POSTGRES` or `FCQRS_TEST_SQLSERVER` is set as in CI,
  on PostgreSQL or SQL Server. `TestDatabases.fs` makes and drops each test's database on those
  servers; `SqlServerTests.fs` checks the collation refusal and IDs that differ only in case.

## Development workflow

Restore and build:

```bash
dotnet build FCQRS.sln
```

Debug builds work. CI runs the F# schedule sample in Debug, because the F# compiler in SDK 10.0.4xx
can emit invalid Debug IL for a `raise` used as a value whose argument contains a `let`, `|>`, `if`,
or `match` (dotnet/fsharp#20564). Bind such an argument before `raise`. CI also runs
`python3 scripts/verify-il.py`, which checks every method of the six published assemblies' Debug and
Release builds with the ILVerify version pinned in `.config/dotnet-tools.json`. In a `task`, raise once
from a `with` handler instead of from each of its branches: a handler whose every branch raised gave
invalid Release IL.

Run the Expecto facade and behaviour tests in Release, as CI does:

```bash
dotnet run -c Release --project test/Facade.Tests/Facade.Tests.fsproj
dotnet run -c Release --project test/Workflows.CSharp.Tests
```

Build and check the documentation:

```bash
dotnet tool restore
python3 scripts/build-docs.py
```

The build compiles the solution in Release, runs every `docs/**/*.fsx` page, compiles the F# examples
with FsLiveDocs, renders `output/`, and checks sample excerpts, tooltips, rendered examples, and local
links. A failed step stops the build with a non-zero exit code. `scripts/DOCUMENTATION.md` describes
the source formats and checks.

Before finishing documentation work:

- run `git diff --check`;
- verify local documentation links;
- scan for stale type names, package versions, target frameworks, and copied examples;
- confirm every claim against current source or a test;
- confirm the navigation indexes form the intended reading order.

## Change discipline

Preserve existing event contracts unless a migration is part of the change. Add readers before writers
during rolling deployments. Test both old and new serialized event fixtures, mixed-history replay,
projection rebuilds, aggregate recovery, and saga recovery.

When changing a public API, update its XML comment and every F# and C# learning path that calls it.

Give every message that can cross nodes a serializer another node can read. Mark a message that never
leaves its node, such as one that carries a function, with `INoSerializationVerificationNeeded`.
