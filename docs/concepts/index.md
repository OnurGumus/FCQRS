---
title: Concepts
category: Concepts
categoryindex: 4
index: 1
---

# Concepts: build the mental model

This section explains FCQRS from the ground up. It begins with the problem a conventional application
model runs into, then introduces one new idea at a time. You do not need prior knowledge of actors,
CQRS, event sourcing, projections, or sagas.

Use the concepts to understand *why* the framework works as it does. Use the
[tutorial](../tutorial/index.html) to learn by building, and the [how-to guides](../how-to/index.html)
when you already know the model and need to complete one task.

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
- A **correlation id** ties one request to the commands, events, saga steps, and projection signal it
  causes.
- A **snapshot** is a recovery checkpoint. It shortens replay but does not replace the journal.

These terms describe different responsibilities. In particular, aggregate state, stored events, and
read-model rows are not three names for the same data.

## Read in this order

1. [CQRS and event sourcing](cqrs-and-event-sourcing.html): begin with an ordinary update-and-query
   application, then separate decisions from queries and replace overwritten state with recorded
   facts.
2. [Aggregates and the write side](aggregates.html): find a consistency boundary, model a command
   decision, fold events into state, and understand what actor serialization guarantees.
3. [The read side](read-models.html): turn the journal into query-shaped data, manage projection
   offsets, rebuild safely, and coordinate read-your-writes.
4. [Sagas](sagas.html): coordinate several independent owners as a durable state machine, including
   retries, timeouts, and partial failure.
5. [Consistency and recovery](consistency-and-recovery.html): put versions, offsets, correlation ids,
   replay, snapshots, and restarts on one timeline.
6. [C# interop and serialization](csharp-interop.html): map the same model to C#, understand the
   compiler options, and treat serialized messages as durable contracts.

## A useful learning loop

For each page, try to explain the idea without FCQRS terminology first. For example: “all cancellation
requests for one order must inspect and change the same current state, one at a time.” Then attach the
term: that consistency boundary is an aggregate.

After reading a concept, follow its tutorial and how-to links. The tutorial shows the idea growing
inside one application. The how-to guide reduces it to a repeatable implementation recipe.
