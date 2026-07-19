---
title: The read side
category: Concepts
categoryindex: 4
index: 4
---

# The read side

The write side stores `OrderPlaced`, `PaymentReceived`, and `OrderShipped`. A screen needs a row with
the order number, customer, total, and current delivery status. A **projection** turns the stored events
into that queryable row.

## From events to query data

<img src="../img/read-path.svg" alt="The read path: events become query-ready tables" width="900"/>

A projection receives each event in order and updates a **read model**. The read model may be a SQL
table, search index, cache, or document store. `OrderPlaced` can insert a row and `OrderShipped` can
change its delivery status.

Another projection can use the same events to maintain a customer order history. Each read model is
shaped for its queries and does not have to match the event or aggregate state.

## The offset and transaction boundary

A projection stores an **offset** that records how far it has read. Store the read-model update and the
new offset in the same database transaction. If the transaction commits, both are saved. If it rolls
back, neither is saved and the event is processed again after restart. This gives the projection
exactly-once updates within that transaction. A projection that writes to several stores must provide
its own idempotency or coordination.

## Queries never touch the journal

Reads run against the read model, not the event journal. The order page can query a table designed and
indexed for that page. The journal remains the source used to build the table.

## Why a read model is disposable

Everything in a read model comes from its projection and the events still held in the journal. To
correct a projection, stop it, replace or clear its read model, reset its offset, and replay the event
stream. The projection then builds the read model again from the stored events.

## The bridge to the client

A projection can publish the events it has handled to a subscription stream. A caller waiting for a
specific correlation id then knows that this projection has committed the corresponding update. This
is the read-your-writes mechanism described in [consistency and
recovery](consistency-and-recovery.html).

The runnable version is in the [tutorial](../tutorial/index.html); the recipe is in
[add a projection](../how-to/add-a-projection.html).
