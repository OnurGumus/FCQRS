---
title: Concepts
category: Concepts
categoryindex: 4
index: 1
---

# Concepts

This section explains the ideas FCQRS is built on, in the order they build on each other. It is the
"why," not the "how" — if you want runnable code, the [tutorial](../tutorial/index.html) and the
[how-to guides](../how-to/index.html) are the places for that. Read these pages in order the first
time; afterwards each stands on its own.

<img src="../img/architecture.svg" alt="How FCQRS fits together" width="900"/>

## The cast of characters

Everything in the diagram above is one of six things, and the whole framework is just these six
playing their parts:

- An **aggregate** takes commands and emits events. It is the unit of consistency on the write side —
  one Akka.NET actor per entity, processing one message at a time.
- An **event** is a fact in the past tense. Events are appended to the **journal**, an append-only
  log that is the single source of truth.
- A **projection** folds events into a **read model** — a structure shaped for the questions you ask
  of it. Read models are disposable; the journal is not.
- A **saga** is the mirror image of an aggregate: it takes events and issues commands, which is how
  side effects (e-mails, external calls) become reliable and recoverable.
- A **correlation id (CID)** travels through a whole request, tying a command to its events to a
  saga's follow-up commands — and letting a client know exactly when the read side has caught up.

## The pages

1. **[CQRS and event sourcing](cqrs-and-event-sourcing.html)** — the two ideas at the foundation, and
   why storing events instead of current state changes everything downstream.
2. **[Aggregates and the write side](aggregates.html)** — how an aggregate decides, why it is an
   actor, and the path a command takes to become a stored event.
3. **[The read side](read-models.html)** — projections, offsets, and why a read model can always be
   thrown away and rebuilt.
4. **[Sagas](sagas.html)** — process managers, the handshake that starts one safely, and how they
   make side effects reliable.
5. **[Consistency and recovery](consistency-and-recovery.html)** — correlation ids, read-your-writes,
   versions, snapshots, and what happens across a restart.
6. **[C# interop and serialization](csharp-interop.html)** — using FCQRS from C# with discriminated
   unions, and how messages are serialized.
