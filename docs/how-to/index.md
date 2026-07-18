---
title: How-to guides
category: How-to
categoryindex: 5
index: 1
---

# How-to guides

Short, focused recipes for specific tasks. Each assumes you already understand the idea behind it —
if you don't, the [Concepts](../concepts/index.html) section explains the why, and the
[Tutorial](../tutorial/index.html) builds a full app step by step.

- **[Define an aggregate](define-an-aggregate.html)** — commands, events, state, and the two functions.
- **[Add a projection](add-a-projection.html)** — fold events into a read model and track the offset.
- **[Write a saga](write-a-saga.html)** — react to events and issue commands.
- **[Dispatch async effects](dispatch-async-effects.html)** — a "mini saga": run a short async read (AI, lookup) and feed the result back, no persistence ceremony.
- **[Read your writes](read-your-writes.html)** — wait until the read model reflects your command, then read.
- **[Configure the database](configure-the-database.html)** — pick a provider; HOCON is optional.
- **[Test your domain](test-your-domain.html)** — unit-test decisions and folds without Akka.
- **[Use FCQRS from C#](use-from-csharp.html)** — the same model with C# 15 unions.
- **[Observe your system](observability.html)** — the message-flow log, distributed traces, and keeping payloads out.
