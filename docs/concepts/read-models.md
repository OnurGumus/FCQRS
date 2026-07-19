---
title: The read side
category: Concepts
categoryindex: 4
index: 4
---

# The read side: from facts to answers

The journal is excellent at preserving what happened. It is a poor shape for most questions a user
asks.

An order page wants one query-ready result containing customer, lines, totals, payment state, delivery
state, and a timeline. Reconstructing several aggregates and scanning their event histories for every
page request would make queries slow and couple the user interface to write-side internals.

The read side exists to turn stored facts into useful answers ahead of time.

## Projection and read model are different things

A **projection** is the event-handling process. A **read model** is the data it produces.

For example:

```text
OrderPlaced      -> insert order_summary row
PaymentReceived  -> set payment_status = 'paid'
OrderShipped     -> set delivery_status = 'shipped'
```

The read model may be a SQL table, document, search index, graph, or cache. Its shape follows a query,
not the aggregate state or event schema. It may duplicate names and totals if that makes reads direct
and understandable.

<img src="../img/read-path.svg" alt="Stored events flow through a projection into query-ready read-model tables" width="900"/>

Several projections can consume the same event. `OrderPlaced` might update an order page, a customer
history, a warehouse queue, and a sales report. Those read models can evolve independently because
they share facts rather than one schema.

## Follow the event stream with an offset

The journal assigns an ordered position to the event stream. A projection keeps an **offset** recording
the last position it committed.

Imagine this stream:

```text
offset 41  OrderPlaced
offset 42  PaymentReceived
offset 43  OrderShipped
```

If the projection has committed offset 42, it resumes after 42 and handles `OrderShipped`. It does not
ask each aggregate for current state. The journal is the source; the offset is the projection's
bookmark.

An aggregate version and a projection offset are different counters. A version orders events for one
aggregate identity. An offset locates an event in the stream a projection consumes.

## The transaction boundary creates reliable progress

For a SQL read model, handle one event like this:

1. Begin a database transaction.
2. Apply the read-model insert, update, or delete.
3. Store the new offset in the same transaction.
4. Commit.

If the process stops before commit, neither change is durable and the event is retried. If commit
succeeds, both the data and offset are durable. This prevents the two dangerous split states:

- data changed but offset did not advance, so a non-idempotent update runs twice;
- offset advanced but data did not change, so the event is skipped forever.

Within one transactional store, this produces one committed update per offset. If a projection writes
to SQL and a search service, those systems do not share the transaction. The handler must then use
idempotency, an outbox, or another explicit coordination design.

## Event order is part of the model

A projection should make invalid histories visible. If `OrderShipped` arrives without `OrderPlaced`,
silently inventing a partial row hides a broken contract or rebuild. Failing the projection exposes the
problem at the event that caused it.

Handlers should also define how repeated or superseded facts behave. A transactional offset prevents
normal repeats in one store, but rebuild tools, migrations, or external writes may still benefit from
idempotent operations keyed by event identity.

## Queries use only the read model

Application queries do not load aggregate actors or inspect the journal. They read the structure
created for them. This keeps query latency and indexing independent from write-side recovery and
business rules.

A read model is allowed to be stale for a short time. The aggregate commits first; the projection
commits later. This is **eventual consistency**. It is not lost data, but it changes what the caller may
observe immediately after a command.

## Read your own write when the interaction needs it

Some interactions can redirect immediately and allow the page to catch up. Others must show the new
result before replying.

FCQRS carries a correlation id from the command to its event. A caller can:

1. subscribe to that correlation id;
2. send the command;
3. wait until the required projection commits and publishes the matching event;
4. query that projection's read model.

Subscribe before sending. Subscribing afterward creates a race in which the notification may already
have passed. The notification is a coordination signal, not durable business messaging. The data and
offset remain the durable proof of projection progress.

## Read models are disposable, rebuilds are not casual

Because every read-model value is derived from retained events, it can be rebuilt:

1. stop or isolate the live projection;
2. create or clear the target schema;
3. reset its offset to the chosen starting position;
4. replay and monitor failures;
5. validate counts and representative queries;
6. switch traffic to the rebuilt model.

“Disposable” means the journal can reproduce the data. It does not mean deleting a production view is
risk-free. Large replays take time, old events must remain deserializable, and a broken projection may
fail halfway through. A side-by-side rebuild often provides the safest rollback.

## Design one read model per question

Start from a consumer and a query, not from the event types. Write the result shape the consumer would
like to receive in one read. Then determine which events create and update it, which fields need
indexes, and how the projection handles missing or out-of-order facts.

Chapter 2 of the [tutorial](../tutorial/2-running-it.html) builds the complete write-to-read loop. Use
[Add a projection](../how-to/add-a-projection.html), [Read your writes](../how-to/read-your-writes.html),
and [Rebuild a read model](../how-to/rebuild-a-read-model.html) for focused implementation recipes.
