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

Ask a developer which kind of change they trust, and the answer is always the same: *adding* code.
A new function, a new file, a new handler — the worst case is that your new thing doesn't work yet.
What we dread is *changing* code that already works, because something else was quietly relying on
the old behaviour, and that is where regressions come from. Almost the whole of SOLID — and the
Open/Closed Principle by name — exists to arrange things so that tomorrow's feature is an *addition*,
not an *edit*.

A single, all-purpose data model is the enemy of that. On the left of the picture, everything reads
and writes the same shared, mutable object graph, so every new requirement means *changing* it: alter
the schema, then go fix every query and every write path that touched it. The one thing everyone
couples to is the one thing you keep having to modify — and every modification is a chance to break
something that already worked.

**CQRS** — Command Query Responsibility Segregation — splits that model in two so that growth moves to
the safe side:

* The **write side** records **events** — facts about what happened — to an append-only log. You never
  rewrite a fact; you append a new one. The most dangerous operation in software, mutating shared state
  in place, becomes the safest: adding a record. Your history is closed for modification by construction.
* The **read side** is built from **projections** — derived views shaped for exactly the screens and
  reports you serve. A new feature is usually a *new* projection over events that already exist: you add
  a reader instead of editing the writer.

And when you genuinely do have to change something, you change it on the read side — which is safe,
because read models are *derived, not the source of truth*. Get a projection wrong and nothing is lost:
fix the code, replay the log, and the view is correct again. Your **core domain — the actual business
rules on the write side — sits still.** That is the real promise: change happens where it is cheap and
reversible, and the code you cannot afford to break gets left alone.

## What FCQRS is

FCQRS is a small F# framework that does this split for you, on top of Akka.NET and usable from both F#
and C#. Its answer to *where the truth lives* is the interesting part.

Each aggregate — an `Order`, a `Customer` — is a **clustered, sharded actor**: think of it as a single
object that lives in memory and is the sole authority over its own entity. Two guarantees make that
real. It is a **true singleton across the whole cluster** — `Order #123` is one object no matter how
many nodes you run, and every command for it is routed to that one instance and handled one at a time,
so it is the final decision-taker with no locks and no two nodes ever disagreeing. And it is
**eternal** — you address it by id and it is simply *there*; behind the scenes it may be evicted when
idle or moved when the cluster rebalances, but to your code it never dies and never moves.

The **event log is not the truth — it is the ingredients.** When that actor needs to come back into
memory (a restart, a rebalance, the first request after it went idle) it replays its events and folds
them back into its current state. The facts are durable on disk; the living, authoritative object is
reconstituted from them on demand. Those same events are also *projected* into your **read models**, so
the query side is always derived from what actually happened rather than hand-maintained. **Sagas**
turn events into follow-up commands across aggregates, and a **correlation id** threads through each
request so a caller knows exactly when the read side has caught up.

You write two pure functions — a **decide** (given the current state and a command, what happened?) and
a **fold** (given the state and an event, what is the new state?) — and FCQRS carries the actors,
sharding, persistence, replay, and the machinery that keeps the two models in sync. You never write an
actor or take a lock. The same domain reads almost identically in C#, using C# 15 discriminated
`union` types, which the framework serializes natively.

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

Our honest answer is: more often than you'd expect. It shines wherever consistency, a complete audit
trail, and reliable side effects matter — business applications, financial systems, multi-user
systems, anything where *what happened and in what order* is part of the value. You can rebuild any
read model by replaying the log, and fix a reporting bug by correcting a projection rather than
patching production data.

Two things push far more projects into that bucket than developers admit:

* **Concurrency is real, and usually under-handled.** The moment two users touch the same thing at
  once you need something to arbitrate — and hand-rolled locking is where the subtle, only-in-prod
  bugs live. The single-writer actor gives you that arbitration for free, from day one, instead of on
  the day it finally bites.
* **"It'll stay simple" rarely survives contact with a business.** Logic creeps in. A project that
  began as a few forms grows rules, and with nowhere to put them you drift into an *anemic domain
  model* — data bags surrounded by ever-fatter service classes. FCQRS gives that logic an obvious home
  — the aggregate — before the drift starts.

Where it genuinely isn't worth it: a truly **tabular, Excel-like app** — CRUD over rows with no real
decisions to protect, no concurrency to speak of, and no history worth keeping. There the journal and
the cluster are ceremony you won't be paid back for. Outside of that, the split usually earns its
keep. The [concepts](concepts/index.html) section is honest about the trade-offs, including the
failure modes you still have to design around.
*)
