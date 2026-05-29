---
title: Tutorial
category: Tutorial
categoryindex: 3
index: 1
---

# Tutorial

The [Get started](../get-started.html) page drops the whole thing in your lap at once. This tutorial
builds the same kind of application up gradually, explaining each piece as it goes, and adds a saga at
the end so you see the write side, the read side, and orchestration fit together.

We build a small **user** service: people register and log in, a read model tracks them, and a saga
reacts to a registration. By the end you will have touched every part of FCQRS once.

You need the .NET 10 SDK and a new console project with the `FCQRS` package
(`dotnet add package FCQRS`). Each chapter's code is compiled against the live package as part of
building this site, so it stays correct.

## Chapters

1. **[The aggregate](1-the-aggregate.html)** — define the `User`: its commands, events, state, and the
   two pure functions that decide and fold.
2. **[Wiring and running it](2-running-it.html)** — create the actor system with no HOCON, send a
   command, and read your write back.
3. **[Adding a saga](3-adding-a-saga.html)** — react to a registration with a follow-up action,
   reliably.

If you want the concepts behind what you are building, each chapter links into the
[Concepts](../concepts/index.html) section.
