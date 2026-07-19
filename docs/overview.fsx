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

The left side is where most applications start: one object graph that tries to do everything. Every
entity references every other, a single save can touch half the graph, and each query drags in
relationships it never needed. Worst of all, the **invariants** — the rules that must always hold,
like *"an order's total matches its lines"* — have no single home. They scatter across services, or
go quietly assumed until the day they're violated.

**CQRS** — Command Query Responsibility Segregation — splits that one model in two, because writing
and reading want opposite things:

* The **command side** breaks the graph into small **aggregates**: a `Customer`, an `Order`. Each
  aggregate is a *consistency boundary* — it owns its invariants, changes in a single transaction,
  and refers to other aggregates only by id. The tangle is gone, and every rule has an obvious owner.
* The **query side** is built from **read models**: flat, denormalized shapes — an `OrderSummary`, a
  `CustomerOrders` view — tuned for exactly the reads your screens make. No domain behaviour and no
  joins at read time; just fast answers.

The payoff is correctness (invariants live in one place), speed (reads never contend with writes),
and clarity (each side does one job). The one new cost is keeping the two sides in sync — and that is
exactly the work you hand to the framework.

## What FCQRS is

FCQRS is a small F# framework for exactly this split, on top of Akka.NET actors and usable from both
F# and C#. Each aggregate is an actor that handles one command at a time, decides what happened, and
emits **events**. Those events are appended to a **journal** — the source of truth — and then
*projected* into your read models, so the query side is always derived from what actually happened
instead of hand-maintained. **Sagas** turn events into follow-up commands across aggregates, and a
**correlation id** threads through each request so a caller knows exactly when the read side has
caught up.

You write two pure functions — a decision and a fold — and FCQRS carries the actors, sharding,
persistence, and the machinery that keeps the two models in sync. The same domain reads almost
identically in C#, using C# 15 discriminated `union` types, which the framework serializes natively.

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

It shines wherever consistency, a complete audit trail, and reliable side effects matter: business
applications, financial systems, multi-user systems, anything where "what happened and in what
order" is part of the value. Because the journal is the source of truth, you can rebuild any read
model by replaying history, and fix a reporting bug by correcting a projection rather than patching
production data.

It is less suited to throwaway prototypes, or to purely stateless transformations where there is no
domain to protect and no history worth keeping. The [concepts](concepts/index.html) section is
honest about the trade-offs, including the failure modes you still have to design around.
*)
