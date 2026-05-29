---
title: CQRS and event sourcing
category: Concepts
categoryindex: 4
index: 2
---

# CQRS and event sourcing

Before any framework detail, it is worth being honest about the problem these two ideas solve —
because if the problem does not sound like yours, the rest will feel like ceremony.

## One model, pulled in too many directions

Picture an order in an e-commerce system. Four people ask about that order and want different things.
The domain expert wants to know whether it *may legally ship* — paid, valid address, in stock. The
customer wants to know *where their parcel is*. The warehouse wants a flat *pick list*. Finance wants
*revenue by region for the quarter*. These are not variations on one question; they are different
shapes of information, each best served by a different structure.

Force one model — one `Order` class, one table — to answer all four and it slowly buckles. You bolt
on fields only a report reads. You write expensive joins because the data was laid out for writing,
not reading. Worst of all, you start bending business rules to fit the dashboards, until the code
that decides *whether an order can ship* is tangled up with the code that draws a chart. Each
compromise is individually reasonable; together they produce a model that is hard to change and easy
to get wrong.

**CQRS** — Command Query Responsibility Segregation — is the almost embarrassingly simple observation
that the model you use to *make decisions and change things* need not be the model you use to *answer
questions*. Split them. Let the write side be shaped for correctness, and let each read side be
shaped for the question it answers.

## What flows between the two sides

Once writing and reading are separate, how does the read side learn about changes? Not by polling the
write side's tables — that just re-couples their shapes. The clean answer is that the write side
announces *what happened* as a stream of **events**, and any number of read sides listen. "OrderPlaced." "PaymentReceived." "OrderShipped." Each is a thing that has already happened, named in the past
tense, and never altered afterwards. The write side appends events; each read side folds them into
the shape it needs. Same facts, different views, none entangled with the rules on the write side.

## Storing the events, not just the state

Here is the step that feels strange once and obvious ever after. Most systems store *current state*:
the row says `status = 'shipped'` and the fact that it was once `pending` then `paid` is gone,
overwritten. **Event sourcing** stores the events themselves as the source of truth and treats current
state as something you *compute* by replaying them. The order's status is not a column you read; it is
the result of folding `OrderPlaced`, then `PaymentReceived`, then `OrderShipped`.

That is more work in the narrow sense, and worth far more than it costs. You get a truthful audit
trail for free, because the events *are* the history rather than a log you remembered to write
alongside the real change. You can answer questions you never anticipated, because the raw facts are
still there to fold a new way. And you get the most liberating property of all, best shown by a
story: finance says "we calculated regional revenue wrong for six months — fix it." With only current
state, that is a nightmare of hand-patched rows. With event sourcing, the events were always correct;
only your *interpretation* was wrong, so you fix the projection, throw the read model away, and replay
history through the corrected code. The numbers rebuild themselves from facts that never lied.

## Why this suits F# (and lands cleanly in C#)

F#'s instincts — immutable records, discriminated unions, no nulls, exhaustive matching — are a near
perfect fit for modelling commands and events, and a poor fit for object-relational mapping. Event
sourcing sidesteps the fight: an event is just an immutable value that gets serialized and appended;
the write side never maps an object graph to tables because it replays events instead. With C# 15's
`union` types, C# can express the same closed sets of commands and events just as cleanly, and FCQRS
understands both. See [C# interop](csharp-interop.html).

Everything so far is general CQRS and event sourcing. What FCQRS adds is a disciplined way to *run*
it: aggregates as actors, sagas for reliable side effects, and correlation ids for client
coordination. The next page starts there, with the [aggregate](aggregates.html).
