---
title: Tutorial
category: Tutorial
categoryindex: 3
index: 1
---

# Tutorial

The [Get started](../get-started.html) page drops a working loop in your lap at once. This tutorial is
the slow version: you build the same kind of application by hand, and at each step we separate the
**idea** (a CQRS/event-sourcing principle that's true in any language) from the **mechanism** (how
FCQRS happens to implement it). Where the machinery is overkill for a given app, we'll say so — the
point is judgment, not selling a framework.

What you'll build is a small **document store**, the same domain as the worked example
[`focument_fsharp`](https://github.com/OnurGumus/focument_fsharp): you create and edit versioned documents, a read
model tracks them, and — by the end — a quota saga gates new documents across a second aggregate.

## What you need

- The **.NET 10 SDK** (`dotnet --version` should print `10.*`).
- A scratch project. Create one now; every chapter adds to its `Program.fs`:

```bash
dotnet new console -lang F# -n DocStore
cd DocStore
dotnet add package FCQRS --prerelease   # the F# facade ships in the 6.0 previews
```

Run it at any point with `dotnet run`. The first run creates a SQLite file next to the binary (the
**journal** — your event log); delete `*.db*` if you ever want a clean slate.

> Each chapter's code uses the idiomatic-F# facade `FCQRS.FSharp`, and chapters 1–2 are compiled
> against the pinned package as part of building this site, so what you read here is what the compiler
> accepts.

## The three chapters

1. **[The aggregate](1-the-aggregate.html)** — the write side. What a command and an event really are,
   why they're *different* sets, and the two pure functions (`decide`, `fold`) that are the whole of
   your domain logic. No actors yet — just types you can unit-test.
2. **[Wiring and running it](2-running-it.html)** — turn those functions into a running, persistent
   actor with no HOCON, add a read model, send a command, and *watch the version climb across
   restarts* — your first real taste of event sourcing.
3. **[Adding a saga](3-adding-a-saga.html)** — a rule that spans two entities (a per-user quota) that an
   aggregate structurally *cannot* enforce alone, and the process manager that can.

Each chapter links into [Concepts](../concepts/index.html) when you want the long-form reasoning behind
a piece.
