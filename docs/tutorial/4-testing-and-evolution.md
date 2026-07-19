---
title: 4. Testing and evolution
category: Tutorial
categoryindex: 3
index: 5
---

# 4. Testing and evolution

The running application now has two aggregates and a saga. Before adding infrastructure, make the
domain safe to change. Event-sourced code has four behaviours worth testing separately:

1. a command and state produce the expected action;
2. an event and state produce the expected next state;
3. replaying an event history produces the expected recovered state;
4. retrying a command does not repeat a business effect.

None of these tests requires Akka.NET or a database.

> **Learn alongside this chapter:** revisit [Consistency and recovery](../concepts/consistency-and-recovery.html)
> to see what the tests must prove. Use [Test your domain](../how-to/test-your-domain.html) for test
> helpers and [Evolve persisted events](../how-to/evolve-events.html) for compatibility strategies.

## Test decisions as a table

The important cases for the document aggregate are states and commands, not methods and mocks. Write
the cases down before writing the assertions:

| Publication state | Command | Expected action |
|---|---|---|
| draft | `Publish(documentId, "guides/fcqrs")` | persist `PublicationRequested` |
| waiting for the same slug | the same `Publish` again | defer `PublicationRequested` |
| waiting for the slug | `FinishPublication Published` | persist `PublicationFinished Published` |
| already published | `FinishPublication Published` again | defer `PublicationFinished Published` |

The last row is a retry case. The reply still tells the saga that the document is published, but the
aggregate does not store a second publication.

> **Motivation:** These tests protect the recovery contract, not an implementation detail. If the same
> inputs always select the same action and rebuild the same state, runtime restarts can safely reuse
> the domain logic you tested.

Use a command envelope helper and call `decide` directly:

```fsharp
let command details : Command<_> =
    { CommandDetails = details
      CreationDate = DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)
      Id = None
      Sender = None
      CorrelationId = Fcqrs.newCid ()
      Metadata = Map.empty }

let publishedState =
    Document.Finished(documentId, "guides/fcqrs", Document.Published)

let action =
    Document.decide
        (command (Document.FinishPublication Document.Published))
        publishedState

test <@
    action =
        DeferEvent(
            Document.PublicationFinished(documentId, "guides/fcqrs", Document.Published))
@>
```

Use fixed timestamps in test envelopes even when the current rule does not read time. Stable inputs
make failures reproducible and keep future time-dependent rules deterministic.

## Test folds with histories

A single fold assertion verifies one transition. A replay test verifies that the transitions compose:

```fsharp
let recovered =
    [ Document.PublicationRequested(documentId, "guides/fcqrs")
      Document.PublicationFinished(documentId, "guides/fcqrs", Document.Published) ]
    |> List.mapi (fun index details -> event (int64 index + 1L) details)
    |> List.fold (fun state stored -> Document.fold stored state) Document.initial

test <@
    recovered = Document.Finished(documentId, "guides/fcqrs", Document.Published)
@>
```

This test is the executable definition of recovery. If a fold reads the clock, generates an id, or
performs I/O, the same history can produce a different result on another run. Put those values in the
command or event instead.

## Test the saga at each state

Test a saga in two layers:

- `handleEvent` maps an incoming event and current saga state to a persisted state action;
- `applySideEffects` maps the resulting state to commands and a saga transition.

For `ReservingSlug(documentId, "guides/fcqrs")`, assert that the saga sends one `Reserve` command to the
slug aggregate identified by `guides/fcqrs`. For `ReportingResult Published`, assert that it sends
`FinishPublication Published` to the originating document. Call `applySideEffects` with
`recovering = true` and verify that it returns a command safe for re-delivery or a recovery-specific
status check. Do not assume the original command was either delivered or lost when the process
stopped.

## Treat persisted events as contracts

An event outlives the process and often outlives the code version that wrote it. Renaming a type,
removing a union case, changing a field's meaning, or changing its serialized representation can make
old journal rows unreadable or change the state produced by replay.

Use these rules when events evolve:

- Add a new event case for a new fact. Do not reinterpret an old case to mean something different.
- Keep old cases readable until every stored instance has an explicit migration path.
- Prefer adding optional data or a new versioned event over changing the meaning of a required field.
- Test recovery from a history written by the previous release.
- Deploy readers that understand the new shape before deploying writers that produce it when versions
  overlap during a rolling deployment.

Register stable journal names so moving a CLR type does not change the stored manifest:

```fsharp
Fcqrs.journalTypes [ journalType<Document.Event> "document.event" ]
```

```csharp
builder.WithJournalTypes(types => types.Type<DocumentEvent>("document.event"));
```

Stable names solve type location changes. They do not migrate the fields inside an event. Field and
meaning changes still need compatibility code and replay tests.

## Rebuild a projection deliberately

A projection is derived from the journal. To verify a projection change:

1. stop the projection;
2. create a fresh read model or clear the old one;
3. reset that projection's offset to the beginning;
4. replay the journal with the new handler;
5. compare counts and representative records before switching queries to the rebuilt model.

Do not delete or edit journal rows to repair a projection. Correct the projection and rebuild its
output.

## Understand it and use it

- **Understand:** [Aggregates and the write side](../concepts/aggregates.html) explains why decisions and
  folds can be tested without the runtime. [Consistency and recovery](../concepts/consistency-and-recovery.html)
  explains replay, uncertain delivery, and the durable boundaries the tests protect.
- **Apply:** [Test your domain](../how-to/test-your-domain.html) provides F# and C# envelope helpers.
  [Evolve persisted events](../how-to/evolve-events.html) and
  [Rebuild a read model](../how-to/rebuild-a-read-model.html) turn compatibility and replay into
  repeatable procedures.

Next, [prepare the system for production](5-production.html).
