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
  aggregate to sagas and changing stored events. Each step has a runnable F# and C# program under
  `samples/accounts/`; step 6 continues from step 5's database, so both build an assembly named
  `Accounts`.
- `docs/workflow-tutorial/` ("Tutorial: Workflows") builds an order workflow in ten steps, from one
  activity to changing a workflow while orders run. Step 10, "Feed orders from a stream", starts
  orders from an Akka.Streams flow and stops the process while two of them ship. Each step has an F#
  and C# program under `samples/orders/`, and its page shows every line that is new or changed since
  the previous step: `scripts/check-learning-snippets.py` fails otherwise. Steps 8 and 9 continue
  from the previous step's database. `scripts/check-orders.py` runs every step and checks the
  output its page shows. The track does not require the CQRS tutorial.
- `docs/concepts/` explains the underlying models and boundaries.
- `docs/how-to/` provides focused task instructions.
- `docs/configuration.md` is the runtime configuration reference.
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
  PostgreSQL, an event that commits after later-numbered ones is applied in a later pass, after them.
- Commit a read-model update and its progress in the same transaction when they share a store. A
  projection that stores progress after its handler returns can hand an event to it again.
- Most projection queries read only recent writes. A write that commits more than
  `LateWriteWindow` after taking its journal number is handled at the next full scan, so
  `CatchUpAsync` can return before it.
- Read-your-writes means one selected projection has published the matching notification.
- It does not mean every projection or external system is current.
- Subscribe before sending. Subscribing afterward can miss the notification.
- Projection subscriptions are in-memory request coordination, not a durable client queue.

### Saga

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
- A workflow starts by ID, with no aggregate event. A second start with the same ID stores nothing.
- A retry policy's deadline is measured from the stored start of the step. Attempt counts restart
  with the process. An activity whose deadline passed while the process was stopped fails without
  another attempt.
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
- A faulted workflow is final. Nothing restarts it.
- An instance leaves memory when it ends. A request to an instance that is not running recovers it
  by replaying its journal, and it then stays in memory until it has had no request for
  `akka.fcqrs.workflow-idle-timeout`.
- Step names, step kinds, the workflow name, and stored payload types are persistent contracts.
- A workflow record is journaled under the CLR name of its `WorkflowRecord` case, which a projection
  can read before any workflow is registered. The type's name, namespace, and assembly are
  persistent contracts too.

### Deployment

- FCQRS runs on one node. Nodes do not form one cluster (each joins itself), and a command's reply
  can be lost between nodes. Do not document multi-node deployment as supported.

### Best-effort async effects

- `RunAsync` work is ephemeral. A stop, restart, or shard move can lose it.
- Its runner must convert every outcome into a command. An escaping exception terminates the process.
- Use a saga when the work must survive recovery.

## Architecture map

- `src/FCQRS.Model/`: validated domain values and query model types.
- `src/FCQRS/Actor.fs`: aggregate persistence, replay, snapshots, and command delivery.
- `src/FCQRS/Saga.fs`: saga state machines, recovery, and command dispatch.
- `src/FCQRS/Query.fs`: correlation subscriptions and projection notifications.
- `src/FCQRS/Projections.fs`: projections, their per-aggregate progress, catch-up, and retries.
- `src/FCQRS/FSharp.fs`: F# facade.
- `src/FCQRS/CSharpInterop.fs`: C# types and lower-level interop.
- `src/FCQRS/HostExtensions.fs`: C# host-builder and dependency-injection registration.
- `src/FCQRS/default.hocon`: embedded Akka.NET defaults.
- `src/FCQRS.Workflows/`: durable workflows. `Workflow.fs` holds the builder and the steps,
  `Machine.fs` the pure replay interpreter, `WorkflowActor.fs` the persistent actor, `CSharp.fs` the
  C# API with its async method builder, and `Streams.fs` the Akka.Streams flows.
- `samples/workflows/`: the quote workflow in F# and C# on .NET 10, with project references. The
  workflow task guide quotes it.
- `samples/orders/`: the workflow tutorial's steps as equivalent F# and C# programs on .NET 11,
  with the published packages. Its `Directory.Build.props` keeps the repository's settings out.
- `samples/accounts/`: the tutorial's steps as equivalent F# and C# programs on .NET 11. The C#
  programs use C# 15 unions, and `samples/accounts/global.json` selects the .NET 11 SDK. The task
  guides quote these programs too; `serve-over-http/` puts step 4 behind ASP.NET Core endpoints.
- `samples/registration-*/` and `samples/getting-started-*/`: older .NET 10 samples kept for CI checks
  and persisted-compatibility checks. The C# interop page links `registration-csharp` as a .NET 10
  example.
- `sample/` and `saga_sample/`: executable examples.
- `test/Workflows.CSharp.Tests/`: a C# program that checks the C# workflow API as the C# compiler
  builds it. Run it with `dotnet run --project test/Workflows.CSharp.Tests -c Release`.
- `test/Facade.Tests/`: facade and behaviour tests. `WorkflowMachineTests.fs` tests replay without an
  actor system, and `WorkflowBankInvariantTests.fs` kills processes while transfers written as
  workflows run. Every test actor system runs Akka's `serialize-messages` through
  `VerifySerialization.fs`, and the last test names each message type that another node could not
  read. `BankInvariantTests.fs` compiles step 5's F# `Account.fs` and
  `Transfer.fs`, kills processes during transfers, and checks that the total balance is unchanged, on
  SQLite and, when `FCQRS_TEST_POSTGRES` is set as in CI, on PostgreSQL.

## Development workflow

Restore and build:

```bash
dotnet build FCQRS.sln
```

Run the Expecto facade and behaviour tests in Release, as CI does. A Debug build fails with
`InvalidProgramException` in core's command subscription:

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
