---
title: Concepts
category: Understand
categoryindex: 3
index: 1
---

# Concepts: build the mental model

This section deepens topics introduced by the numbered [learning path](../tutorial/index.html). If you
are new to FCQRS, finish the matching course stage before opening its concept page. The course already
contains the reasoning required to continue; these pages are for questions that need a wider design or
failure model.

<img src="../img/architecture.svg" alt="A command enters an aggregate, stored events flow through the journal to projections and sagas, and queries read a purpose-built read model" width="900"/>

## Start with four questions

Every FCQRS design can be approached through four questions:

1. **What decision is being requested?** A command names the caller's intent, such as `CancelOrder`.
2. **Which information must be consistent for that decision?** That information belongs inside one
   aggregate boundary.
3. **What fact should be recorded if the decision succeeds?** An event such as `OrderCancelled`
   becomes part of the permanent history.
4. **What information must callers query?** A projection turns the stored facts into a read model
   shaped for that question.

If one decision needs work owned by several aggregates, a saga coordinates the steps without merging
their state.

## The vocabulary, in dependency order

- A **command** asks the system to do something. It may be accepted or rejected.
- An **event** records an outcome that has already happened.
- An **aggregate** owns the state and rules required to decide commands for one identity.
- The **journal** is the append-only history of persisted events.
- A **fold** rebuilds current aggregate state by applying those events in order.
- A **projection** consumes journal events and updates query-ready data.
- A **read model** is the data produced by a projection for a particular query or screen.
- An **offset** records how far a projection has consumed the journal.
- A **saga** stores the progress of work that crosses aggregate or service boundaries.
- A [**correlation id**](correlation-ids.html) ties one request to the commands, events, saga steps,
  and projection signal it causes.
- A [**snapshot**](aggregate-lifecycle.html) is a recovery checkpoint. It shortens replay but does not
  replace the journal.
- **Passivation** stops an inactive actor without deleting its journaled history.

These terms describe different responsibilities. In particular, aggregate state, stored events, and
read-model rows are not three names for the same data.

## Choose by the question you are asking

| Question | Read | Course prerequisite |
|---|---|---|
| Why separate decisions, history, and queries? | [CQRS and event sourcing](cqrs-and-event-sourcing.html) | Quickstart |
| Where should one business rule live? | [Aggregates and the write side](aggregates.html) | Chapter 1 |
| How do projections, offsets, and query models differ? | [The read side](read-models.html) | Chapter 2 |
| How does one request wait for the correct projection? | [Correlation IDs and read-your-writes](correlation-ids.html) | Chapter 2 |
| How do independent owners coordinate after failures? | [Sagas](sagas.html) | Chapter 3 |
| What do defer, snapshot, and passivation each do? | [Deferring, snapshots, and passivation](aggregate-lifecycle.html) | Chapter 2 |
| What is durable at each point in the complete flow? | [Consistency and recovery](consistency-and-recovery.html) | Chapter 3 |
| How should C# messages cross the persistence boundary? | [C# interop and serialization](csharp-interop.html) | Quickstart |

## How to use a concept page

Start with a concrete question from your application. Read the matching page, identify the guarantee
and its boundary, then return to your design. Use [Apply](../how-to/index.html) only when you are ready
to turn that decision into code or configuration.
