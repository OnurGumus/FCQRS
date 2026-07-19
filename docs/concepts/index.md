---
title: Concepts
category: Concepts
categoryindex: 4
index: 1
---

# Concepts

These pages explain why FCQRS separates decisions from queries, stores events, and runs aggregates and
sagas as actors. Read them in order for the complete path through the framework. For runnable code, use
the [tutorial](../tutorial/index.html) or choose a task from the [how-to guides](../how-to/index.html).

<img src="../img/architecture.svg" alt="How FCQRS fits together" width="900"/>

## The pieces

- An **aggregate** takes commands and emits events. One Akka.NET actor handles the commands for each
  entity, one at a time.
- An **event** is a fact in the past tense. Events are appended to the **journal**, an append-only
  log that is the single source of truth.
- A **projection** folds events into a **read model** shaped for a particular set of queries. A read
  model can be rebuilt from the journal.
- A **saga** takes events and issues commands. It stores the progress of work that crosses aggregate
  boundaries.
- A **correlation id (CID)** travels through a whole request, tying a command to its events to a
  saga's follow-up commands. A client can also use it to wait for a projection.

## The pages

1. **[CQRS and event sourcing](cqrs-and-event-sourcing.html):** why decisions and queries use different
   models, and why FCQRS stores events instead of overwriting state.
2. **[Aggregates and the write side](aggregates.html):** how a command becomes a stored event.
3. **[The read side](read-models.html):** how projections, read models, and offsets work together.
4. **[Sagas](sagas.html):** how one event starts durable work across aggregate boundaries.
5. **[Consistency and recovery](consistency-and-recovery.html):** correlation ids, read-your-writes,
   versions, snapshots, and restarts.
6. **[C# interop and serialization](csharp-interop.html):** how C# unions become FCQRS messages and how
   those messages are serialized.
