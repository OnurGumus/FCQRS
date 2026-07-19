(**
---
title: Overview
category: Overview
categoryindex: 1
index: 1
---
*)

(**
# FCQRS

<figure style="margin: 1.5rem 0;">
  <img src="img/two-models.png" alt="On the left, one tangled Entity Framework object graph where every entity references every other; on the right, the same domain split into small command-side aggregates that each enforce their own invariants, plus flat query-side read models shaped for fast queries." style="width: 100%; height: auto; border: 1px solid var(--line, #d7e1ef); border-radius: 12px;"/>
  <figcaption style="margin-top: .6rem; font-size: .9rem; color: var(--muted, #4d5f7d); text-align: center;">Why CQRS, in one picture: a single tangled model on the left, the two-model split on the right.</figcaption>
</figure>

## Why two models?

Developers trust one kind of change above all others: adding code. New code cannot break what already
works. The worst it can do is not work yet. Changing existing code is the dangerous kind, because
something you have forgotten about was relying on the old behaviour. That is where most regressions
come from. It is the whole idea behind the Open/Closed Principle, and most of SOLID with it: shape your
code so that new features are things you add, not things you edit.

A single, all-purpose data model works against that. Everything reads and writes the same shared,
mutable object graph. So every new requirement means changing that graph. You alter the schema, then
hunt down every query and every write that touched it. The one thing everyone depends on is the one
thing you keep editing, and every edit is a chance to break something that already worked.

CQRS stands for Command Query Responsibility Segregation. It splits the model in two, so that new work
lands on the safe side.

The write side stores **events**: facts about what happened. It only ever appends. You never rewrite a
fact, you add a new one. Mutating shared state, the most dangerous thing in software, becomes an
append, the safest.

The read side is built from **projections**: views derived from those events and shaped for the screens
and reports you actually serve. A new feature is usually a new projection over events you already have.
You add a reader instead of editing the writer.

You still change code sometimes. What matters is where. When you do, you change the read side, and that
is safe because a read model is derived, not the source of truth. Get a projection wrong and you have
lost nothing. Fix the code, replay the events, and the view is correct again. Your business rules, on
the write side, never move. That is the real promise. Change happens where it is cheap and reversible,
and the code you cannot afford to break is left alone.

## What FCQRS is

FCQRS is a small F# framework that does this split for you. It runs on Akka.NET and works from both F#
and C#. The interesting part is where it keeps the truth.

Each aggregate, an `Order` or a `Customer`, is a **clustered, sharded actor**. Picture a single object
that lives in memory and has the final say over its own entity. Two guarantees make that real.

First, there is only one of it across the whole cluster. There is exactly one `Order #123` no matter
how many nodes you run. Every command for that order goes to that one instance, which handles them one
at a time. So it decides alone, with no locks and no two nodes ever disagreeing. The contention a
database fights with row locks never happens, because there is only ever one of it.

Second, it is always there. You address it by id and it exists. When it goes idle it may drop out of
memory, and when the cluster rebalances it may move to another node, but your code never sees any of
that. To you it never dies and never moves.

The event log is not the truth. It is the ingredients. When the actor needs to come back into memory,
after a restart, after a rebalance, or on the first request following an idle spell, it replays its
events and folds them into its current state. The facts live on disk. The living object is rebuilt from
them when it is needed.

Those same events also feed your **read models**, so the query side always reflects what actually
happened. **Sagas** turn events into follow-up commands across aggregates. A **correlation id** follows
each request, so the caller knows when the read side has caught up.

You write two functions. `decide` takes the current state and a command and returns what happened.
`fold` takes the state and an event and returns the new state. Both are pure. FCQRS handles the actors,
the sharding, the persistence, the replay, and keeping the two models in sync. You never write an actor
or take a lock. The same code reads almost the same in C#, using C# 15 `union` types, which FCQRS
serializes natively.

<img src="img/architecture.svg" alt="How FCQRS fits together" width="900"/>

## What you get

You write your business rules. FCQRS delivers the guarantees.

* ✅ **You never lose data** — every change is recorded permanently and can't be quietly overwritten.
* ✅ **A complete, trustworthy history of everything that happened** — a full audit trail by default,
  not something you bolt on later.
* ✅ **Rebuild any report or view from history** — fix a mistake by replaying the past, never by
  patching production data.
* ✅ **No race conditions to hunt down** — correct under concurrency by design; nothing to lock,
  nothing to tune.
* ✅ **You always read your own writes** — no stale data, no arbitrary waits, no guessing when it's
  ready.
* ✅ **You write rules, not infrastructure** — storage, recovery, and coordination are handled for you.
* ✅ **Test your logic instantly** — verify your decisions with no database and no running system.
* ✅ **The same product in F# and C#** — first-class in both languages; neither is an afterthought.
* ✅ **Fast, query-ready views kept up to date for you** — read models that are always current, without
  extra work.
* ✅ **Long-running processes that survive restarts** — multi-step work resumes exactly where it left
  off after a crash.
* ✅ **Reach outside services without extra machinery** — call an AI or a lookup and act on the answer,
  no ceremony.
* ✅ **See any workflow end to end** — built-in tracing and clear logs from day one.
* ✅ **Scale from a laptop to a cluster** — the same code grows with you; no rewrite when traffic does.
* ✅ **Up and running in minutes** — one line of setup and a connection string.

## Find your path

This documentation is organized by what you are trying to do:

* **[Get started](get-started.html)** — install the package and run a complete write-and-read loop in
  a few minutes. Start here if you just want to see it work.
* **[Tutorial](tutorial/index.html)** — build a small application step by step: an aggregate, a read
  model, a query, and a saga, wired together and running. Start here if you learn by doing.
* **[Concepts](concepts/index.html)** — the ideas behind the framework explained properly: why CQRS
  and event sourcing, what aggregates and sagas really are, how consistency and recovery work. Start
  here if you want to understand *why*, not just *how*.
* **[How-to guides](how-to/index.html)** — short, focused recipes for specific tasks: add a
  projection, write a saga, configure the database, consume FCQRS from C#, test your domain.
* **Reference** — the generated API documentation and the
  [configuration reference](configuration.html).

## When FCQRS is a good fit — and when it isn't

The honest answer is: more often than you would expect. FCQRS pays off wherever consistency, a full
audit trail, and reliable side effects matter. That covers business applications, financial systems,
multi-user systems, and anything where what happened, and in what order, is part of the value. You can
rebuild any read model by replaying the log, and fix a reporting bug by correcting a projection instead
of patching production data.

Two things pull far more projects into that group than developers admit.

The first is concurrency. It is real, and most code under-handles it. The moment two users touch the
same thing at once, something has to arbitrate, and hand-rolled locking is where the subtle bugs that
only show up in production live. The single-writer actor does that arbitration for you, from the first
day, not the day it finally bites.

The second is that "it will stay simple" rarely survives contact with a real business. Logic creeps
in. A project that started as a few forms grows rules, and if those rules have nowhere to live you end
up with an anemic domain model: bags of data wrapped in ever-fatter service classes. FCQRS gives the
rules a home, the aggregate, before that drift starts.

Where it is genuinely not worth it: a plain tabular app, the kind of thing a spreadsheet would do.
CRUD over rows, no real decisions to protect, no concurrency to speak of, no history worth keeping.
There the journal and the cluster are ceremony that will not pay you back. Everywhere else, the split
tends to earn its keep. The [concepts](concepts/index.html) section is honest about the trade-offs,
including the failure modes you still have to design around.
*)
