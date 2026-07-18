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

FCQRS is a small F# framework for building applications with **Command Query Responsibility
Segregation (CQRS)** and **event sourcing**, on top of Akka.NET actors. It is usable from both F#
and C#, and it is built on one bet: that the reliability and clarity of an event-sourced,
actor-based system are worth having from day one — even for ordinary applications — and that the
framework, not you, should carry the distributed-systems machinery.

<img src="img/architecture.svg" alt="How FCQRS fits together" width="900"/>

## What you actually write

Each entity is an **aggregate** — an Akka.NET actor that processes one command at a time, decides
what happened, and emits **events**. Those events are appended to a journal (the source of truth) and
flow out to **read models** shaped for querying and to **sagas** that turn events into follow-up
commands. A **correlation id** threads through the whole request so a caller knows exactly when the
read side has caught up. You write pure decision and fold functions; FCQRS supplies the actors,
sharding, persistence, and coordination.

The same domain reads almost identically in C#, using C# 15 discriminated `union` types for commands
and events — which the framework serializes natively.

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
