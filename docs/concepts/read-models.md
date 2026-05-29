---
title: The read side
category: Concepts
categoryindex: 4
index: 4
---

# The read side

The write side persists events; the read side turns them into something you can query. The component
that does the turning is a **projection** — a function the framework calls once per event, in the
order the events were written.

## A projection is a fold with a side table

<img src="../img/read-path.svg" alt="The read path: events become query-ready tables" width="900"/>

A projection consumes the event stream and folds it into a **read model** — whatever structure
answers your queries best. In the sample apps that is a couple of SQLite tables (one row per current
entity, plus a history table); it could equally be a search index, a cache, or a document store. The
projection's whole job is small and mechanical: when a `CreatedOrUpdated` event arrives, upsert the
row; when an `Approved` event arrives, flip a status column.

The read model's shape is chosen entirely for the questions it answers and owes nothing to the events
or to the aggregate's state. That freedom — to denormalize, to keep several differently-shaped views
of the same facts — is the entire point of separating reads from writes.

## The offset, and exactly-once

A projection remembers **how far it has read** — an offset into the event stream — and the disciplined
move is to write that offset *in the same transaction* as the rows it produces. Because the data write
and the offset bump commit together, the projection is exactly-once even across a crash: on restart it
resumes from precisely where it left off and never double-applies or skips an event. When you write
your own projection, copy this habit above all others.

## Queries never touch the journal

Reads run against the read model, not the event store. A query is just a query — `SELECT … FROM
Documents` — fast, independent of the write path, and free to use whatever indexing or storage suits
it. The journal is for rebuilding the read model, not for serving it.

## Why a read model is disposable

This is the promise from [CQRS and event sourcing](cqrs-and-event-sourcing.html) made concrete.
Everything in the read model was put there by the projection, folding events the journal still holds.
So if the projection has a bug, or you want an entirely new table, you fix the projection, delete the
read model, reset the offset to zero, and let the stream replay from the beginning. The corrected
projection rebuilds every row from events that never changed. Edit, drop, replay — no production data
surgery, no migration against mismodelled state. The journal is permanent; the read model is a
regenerable opinion about it.

## The bridge to the client

A projection in FCQRS also *returns* the events it handled, and those are republished onto a
subscription stream. That return value is easy to overlook, but it is what lets a caller learn that
the read side has caught up with a specific command — the mechanism behind read-your-writes, covered
next in [consistency and recovery](consistency-and-recovery.html).

The runnable version is in the [tutorial](../tutorial/index.html); the recipe is in
[add a projection](../how-to/add-a-projection.html).
