---
title: Part 1 · Why CQRS
category: Workshop
categoryindex: 4
index: 2
---

# Part 1 — Why CQRS, and why event sourcing

Before we touch the framework, it is worth being honest about the problem it exists to solve.
Because if the problem does not sound like one you have, the machinery in the later parts will feel
like ceremony for its own sake. So this part has no FCQRS code in it at all. It is the argument.

## One model, pulled in too many directions

Imagine an ordinary order in an e-commerce system. Four different people want to ask questions about
that order, and they do not want the same thing.

The domain expert wants to know whether the order *may legally ship* — has it been paid for, is the
address valid, is the item in stock. The customer wants to know *where their parcel is*. The
warehouse wants a flat *pick list*: which shelf, how many, in what order to walk the aisles. Finance
wants *revenue grouped by region for the quarter*. These are not variations on one question. They
are genuinely different shapes of information, and each is best served by a different structure.

If you have one model — one `Order` class, one table — that has to answer all four, it slowly turns
into something nobody is happy with. You bolt on fields that only the finance report reads. You
write joins at query time that are expensive precisely because the data was laid out for writing,
not for reading. And worst of all, you start bending the business rules to fit the reporting, so
that the code that decides *whether an order can ship* is tangled up with the code that produces a
dashboard. Each of those compromises is individually reasonable. Together they produce a model that
is hard to change and easy to get wrong.

The observation behind CQRS — Command Query Responsibility Segregation — is almost embarrassingly
simple once you say it out loud. **The model you use to make decisions and change things does not
have to be the model you use to answer questions.** Split them. Let the write side be shaped for
correctness, and let each read side be shaped for the question it answers.

## What flows between the two sides

Once you split writing from reading, an obvious question appears: how does the read side learn about
changes the write side made? You could have the read side poll the write side's tables, but then you
are back to coupling their shapes together. The clean answer is that the write side announces *what
happened*, as a stream of facts, and any number of read sides listen.

Those facts are **events**. "OrderPlaced." "PaymentReceived." "OrderShipped." Each one is a thing
that has already happened, named in the past tense, and — this is the important part — never changed
afterwards. The write side appends events; the read sides fold those events into whatever shape they
need. The customer's read side folds them into a status timeline. The warehouse's read side folds
the same events into a pick list. Finance folds them into regional totals. Same facts, different
shapes, each optimised for its audience, and none of them entangled with the rules on the write
side.

## Storing the events, not just the state

Here is the step that feels strange the first time and obvious ever after.

Most systems store *current state*: the order row says `status = 'shipped'`, and the fact that it
was once `pending` and then `paid` is gone, overwritten. **Event sourcing** stores the events
themselves as the source of truth, and treats current state as something you compute by replaying
them. The order's current status is not a column you read; it is the result of folding
`OrderPlaced`, then `PaymentReceived`, then `OrderShipped`.

This sounds like more work, and in the narrow sense it is. What you get back is worth far more than
it costs.

You get a complete, truthful audit trail for free, because the events *are* the history — not a log
you remembered to write alongside the real change, but the change itself. You can answer questions
you did not anticipate, because the raw facts are still there to be folded a new way. And you get
the single most liberating property of the whole approach, which is best shown by a story.

Suppose finance comes to you and says: "We have been calculating regional revenue wrong for six
months. The rule was subtly off. Fix it." In a system that stored only current state, this is a
nightmare — the wrong numbers are baked into rows, and you are reduced to patching production data
by hand and hoping. In an event-sourced system, the events were always correct; only your
*interpretation* of them was wrong. So you fix the projection logic, throw away the read model, and
replay the events through the corrected code. The numbers rebuild themselves, correctly, from facts
that never lied to you. A read model is disposable precisely because it is never the source of
truth.

## Why this suits F# (and lands cleanly in C#, too)

There is a practical reason this style feels especially natural here. F#'s instincts — immutable
records, discriminated unions, no nulls, exhaustive pattern matching — are a near-perfect fit for
modelling commands and events, and they fit *badly* with traditional object-relational mapping. An
ORM wants mutable objects with parameterless constructors and navigation properties; F# wants
immutable values and closed sets of cases. Trying to please an ORM from F# usually means writing
half-hearted C# in F#'s clothing.

Event sourcing sidesteps the fight entirely. An event is just an immutable value that gets
serialised and appended. There is no mapping of a rich object graph to tables, because the write
side never queries tables — it replays events. Your domain types stay idiomatic, and your read
models can be whatever is convenient: plain DTOs, a SQL table, a search index, a cache.

This is no longer an F#-only story. With C# 15's discriminated `union` types, C# can express a
closed set of commands or events as cleanly as F# can, and FCQRS understands those unions natively.
Throughout this workshop you will see the same domain modelled both ways, and they will look
remarkably alike.

## Where FCQRS comes in

Everything so far is general CQRS and event sourcing; none of it is specific to this framework. What
FCQRS adds is a disciplined, batteries-included way to run it.

It puts each entity behind an Akka.NET actor, so that an aggregate processes one command at a time
with no locks and no races, and so that entities can be distributed across a cluster and quietly put
to sleep when idle. It gives sagas a home — long-running processes that turn events into follow-up
commands and make side effects like sending e-mail reliable and retryable. And it gives you a
correlation id that threads through a whole request, so a client can know *exactly* when the read
side has caught up with the command it just sent, instead of saving and then hoping.

The bet FCQRS makes — stated plainly so you can decide whether you accept it — is that these
guarantees are valuable from day one, even for a humble CRUD application, and that the framework
should carry the distributed-systems complexity so you do not have to. Whether that bet pays off for
your situation is a judgement call. The rest of this workshop is about helping you make it from a
position of understanding.

In [Part 2](part-2-the-cast.md) we put names and shapes to the pieces — the aggregate, the journal,
the projection, the saga, and the correlation id — and see how they fit together on a single
diagram.
