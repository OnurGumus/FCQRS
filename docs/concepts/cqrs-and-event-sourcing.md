---
title: CQRS and event sourcing
category: Concepts
categoryindex: 4
index: 2
---

# CQRS and event sourcing

The information needed to make a decision is often different from the information needed to answer a
query.

Take an order cancellation. The decision needs the order's current status and the rules that determine
whether cancellation is allowed. An order page may need the customer's name, product descriptions,
delivery address, payment status, and a timeline. A warehouse pick list and a quarterly revenue report
need two more shapes of the same underlying facts.

One model can serve all of these jobs, but each rule and each query then changes the structure on which
the others depend. Business decisions become coupled to joins and fields that exist for screens and
reports.

**CQRS**, or Command Query Responsibility Segregation, gives the two jobs separate models. The write
model is shaped around decisions and business rules. Each read model is shaped around the questions it
must answer.

## What flows between the two sides

The write model publishes **events** such as `OrderPlaced`, `PaymentReceived`, and `OrderShipped`.
Each name describes something that has already happened. A projection reads those events and updates
one read model. Several projections can read the same event and store different results.

The write model therefore does not need to expose its internal state to the query side. Both sides use
the same facts without sharing the same data structure.

## Storing the events, not just the state

In a conventional table, changing an order from `paid` to `shipped` overwrites the previous status.
**Event sourcing** stores `PaymentReceived` and `OrderShipped` instead. The current status is the result
of folding those events in order.

This provides both the current state and the history that produced it. It also lets a new projection
answer a question that did not exist when the events were stored. If a revenue projection contains a
calculation error, the stored payment events remain unchanged. Correct the projection, reset its read
model and offset, and replay the events to calculate the values again.

## Why this suits F# (and lands cleanly in C#)

Commands and events form closed sets of immutable values. F# records and discriminated unions express
those sets directly. C# 15 unions provide the same shape in C#. FCQRS serializes both forms and appends
the events without mapping the aggregate's state to relational tables. See
[C# interop](csharp-interop.html).

FCQRS runs the write model as [aggregate actors](aggregates.html), feeds events to projections, uses
sagas for work across aggregate boundaries, and carries correlation ids from commands to read-side
notifications.
