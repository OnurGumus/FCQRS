---
title: How-to guides
category: How-to
categoryindex: 5
index: 1
---

# How-to guides

Use these guides after you understand the main FCQRS flow. Each page solves one task and states the
failure boundary that matters for that task. Follow the [tutorial](../tutorial/index.html) for a
continuous course or read [concepts](../concepts/index.html) for the underlying models.

## Model the write side

- [Define an aggregate](define-an-aggregate.html): choose commands, stored events, deferred replies,
  state, and snapshot policy.
- [Test your domain](test-your-domain.html): test decisions, folds, replay, and retry behaviour
  without starting Akka.NET.
- [Evolve persisted events](evolve-events.html): keep old journal entries readable while commands
  and event types change.

## Build the read side

- [Add a projection](add-a-projection.html): update a read model and its offset in one transaction.
- [Read your writes](read-your-writes.html): wait for the required projection before querying it.
- [Rebuild a read model](rebuild-a-read-model.html): replace derived query data without changing the
  journal.

## Coordinate work

- [Write a saga](write-a-saga.html): react to events, store progress, and issue commands across
  aggregate boundaries.
- [Dispatch async effects](dispatch-async-effects.html): run best-effort asynchronous work that may
  safely be lost during restart.

## Configure and operate

- [Configure the database](configure-the-database.html): choose a journal and snapshot provider.
- [Observe your system](observability.html): follow commands, events, saga transitions, and
  projections with logs and traces.
- [Use FCQRS from C#](use-from-csharp.html): define the same model with C# 15 unions and hosting APIs.
