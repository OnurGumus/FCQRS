---
title: The FCQRS Workshop
category: Workshop
categoryindex: 4
index: 1
---

# The FCQRS Workshop

This is a guided introduction to building systems with **FCQRS** — a small F# framework for
Command Query Responsibility Segregation (CQRS) and event sourcing, running on Akka.NET actors.
It is written to be *read in order*, like a short book, rather than skimmed for snippets. Each
part assumes you have understood the one before it.

By the end you will be able to look at a running FCQRS application and trace a single user action
all the way through it: from an HTTP request, into a command, through an actor that decides what
happened, into a durable event, out to a read model, and finally back to the caller who now knows
their change has landed. You will also understand *why* the pieces are shaped the way they are,
which matters more than memorising any particular function name.

## Who this is for

You should be comfortable reading either F# or C#. Every concept here is shown in **both
languages, side by side**, because the framework is used from both — the F# version is the native
one, and the C# version uses the new C# 15 discriminated `union` types to express the same domains
just as cleanly. You do not need prior experience with CQRS, event sourcing, or Akka.NET. You *do*
need to accept, at least for the duration of the workshop, the framework's central bet: that the
reliability and clarity you get from modelling everything as commands and events is worth a little
more ceremony up front, even for ordinary applications.

## The two applications we build toward

The workshop teaches against two real, runnable code bases so that nothing here is hypothetical.

The first is **`saga_sample`**, which lives in this repository. It is a deliberately tiny domain —
a user registers, receives a verification e‑mail, and signs in — and it is where we will meet every
moving part for the first time, in isolation. It is small enough to hold in your head all at once.

The second is **Focument**, a document-versioning application that exists in two parallel ports:
[`focument`](https://github.com/onurgumus/focument) in F# and
[`focument-csharp`](https://github.com/onurgumus/focument) in C#. Once the individual pieces make
sense, Focument is where we watch them work together under realistic conditions: an HTTP API, a
SQLite read model, an approval workflow, and a deployment story.

We deliberately start small and grow. The first time you see a saga, it will be the four-line one
from `saga_sample`, not the production-shaped one from Focument.

## How the parts fit together

The first two parts build a mental model with no code to get in the way. The middle parts are
hands-on: you will read and write aggregates, projections, and sagas. The final parts put
everything together and then show you how to configure and run it for real.

1. **[Why CQRS, and why event sourcing](part-1-why-cqrs.md)** — the problem these ideas solve, and
   why storing *events* instead of *current state* turns out to be the move that makes everything
   else fall into place.
2. **[The cast of characters](part-2-the-cast.md)** — aggregates, the event journal, projections,
   sagas, and the correlation id that ties a single request together. The whole system on one page.
3. **[Your first aggregate](part-3-your-first-aggregate.md)** — commands, events, the decide function
   and the fold function, built from `saga_sample`'s `User`.
4. **[Event sourcing in practice](part-4-event-sourcing.md)** — how state is rebuilt from events, what
   snapshots and versions are for, and how the framework survives a restart.
5. **[The read side](part-5-the-read-side.md)** — turning a stream of events into query-ready tables,
   and why a projection can always be thrown away and rebuilt.
6. **[Telling the client it is done](part-6-client-coordination.md)** — correlation ids, the
   "subscribe before you send" rule, and read-your-writes on an eventually-consistent read model.
7. **[Sagas: making side effects reliable](part-7-sagas.md)** — the saga as a small state machine that
   reacts to events and issues commands, and the handshake that starts one safely.
8. **[Focument, end to end](part-8-focument-end-to-end.md)** — one request traced through the entire
   system, in F# and in C#.
9. **[Configuration and running it](part-9-configuration.md)** — HOCON, the SQLite and clustering
   knobs, and what changes when you go to production.

## A note on the code samples

Where a sample is taken from a real file, the path is given so you can open it yourself. F# and C#
samples are always shown as a pair and are meant to be read as "the same idea, twice" — if one
language's version is clearer to you, read that one first and then glance at the other.

When you are ready, start with [Part 1](part-1-why-cqrs.md).
