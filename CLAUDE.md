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

Use the document example across adjacent pages unless a different domain is necessary. Introduce only
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
- `docs/get-started.fsx` runs one complete command, journal, projection, and query loop.
- `docs/tutorial/` builds the system in order from aggregate to production.
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
- Commit a read-model update and its source offset in the same transaction when they share a store.
- Read-your-writes means one selected projection has published the matching notification.
- It does not mean every projection or external system is current.
- Subscribe before sending. Subscribing afterward can miss the notification.
- Projection subscriptions are in-memory request coordination, not a durable client queue.

### Saga

- A saga stores workflow progress and issues commands across aggregate boundaries.
- Recovery re-drives side effects for recovered state. Commands it issues must be retry-safe or
  explicitly recovery-aware.
- A persisted saga state cannot make an external operation exactly once.
- External calls need idempotency keys, timeouts, retry policy, and compensation or intervention paths.
- Avoid circular workflows and give every expected event a domain timeout, either hand-rolled with
  `toSelfAfter` or declared with `StayExpecting`.
- A `StayExpecting` deadline is measured from the persisted state-entry time; the reminder timer is
  best-effort and re-arms on recovery, so restarts cannot postpone the deadline.
- `ExpectationExhausted` means the outcome is unknown, not failed. The domain must answer it with a
  transition, and the escalated state must expect that the original reply may still arrive.

### Best-effort async effects

- `RunAsync` work is ephemeral. A stop, restart, or shard move can lose it.
- Its runner must convert every outcome into a command. An escaping exception terminates the process.
- Use a saga when the work must survive recovery.

## Architecture map

- `src/FCQRS.Model/`: validated domain values and query model types.
- `src/FCQRS/Actor.fs`: aggregate persistence, replay, snapshots, and command delivery.
- `src/FCQRS/Saga.fs`: saga state machines, recovery, and command dispatch.
- `src/FCQRS/Query.fs`: journal projection and correlation subscriptions.
- `src/FCQRS/FSharp.fs`: F# facade.
- `src/FCQRS/CSharpInterop.fs`: C# types and lower-level interop.
- `src/FCQRS/HostExtensions.fs`: C# host-builder and dependency-injection registration.
- `src/FCQRS/default.hocon`: embedded Akka.NET defaults.
- `samples/getting-started-fsharp/` and `samples/getting-started-csharp/`: equivalent first-project
  flows kept on stable .NET 10.
- `sample/` and `saga_sample/`: executable examples.
- `test/Facade.Tests/`: facade and behaviour tests.

## Development workflow

Restore and build:

```bash
dotnet build FCQRS.sln
```

Run the Expecto facade and behaviour tests:

```bash
dotnet run --project test/Facade.Tests/Facade.Tests.fsproj
```

Build the documentation:

```bash
dotnet fsdocs build --properties Configuration=Release
```

The fsdocs process can finish with exit code zero even when an embedded F# snippet reports an error.
Inspect its output for `error(` and verify every changed executable page is listed as evaluated.

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
