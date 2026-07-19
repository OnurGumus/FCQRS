---
title: Task guide index
category: Apply
categoryindex: 4
index: 1
---

# Apply FCQRS: task guides

This section is a working reference, not a second tutorial. Each guide assumes the numbered learning
path has already introduced its model, solves one task, and states the failure boundary that matters.
If a prerequisite is unfamiliar, return to the listed course stage rather than following several
cross-links at once.

## Choose the task

| Task | Guide | Learn it first |
|---|---|---|
| Model commands, events, decisions, and folds | [Define an aggregate](define-an-aggregate.html) | Chapter 1 |
| Project events into query data | [Add a projection](add-a-projection.html) | Chapter 2 |
| Wait before querying your own change | [Read your writes](read-your-writes.html) | Chapter 2 |
| Coordinate several aggregate owners | [Write a saga](write-a-saga.html) | Chapter 3 |
| Test decisions, replay, and retries | [Test your domain](test-your-domain.html) | Chapter 4 |
| Change stored event contracts safely | [Evolve persisted events](evolve-events.html) | Chapter 4 |
| Rebuild derived query data | [Rebuild a read model](rebuild-a-read-model.html) | Chapter 4 |
| Run non-durable asynchronous work | [Dispatch an async effect](dispatch-async-effects.html) | Chapter 3 |
| Choose journal and snapshot storage | [Configure the database](configure-the-database.html) | Chapter 5 |
| Add logs, traces, and failure signals | [Observe your system](observability.html) | Chapter 5 |
| Build a C# host and domain | [Use FCQRS from C#](use-from-csharp.html) | Quickstart and chapter 1 |

## If the task crosses several rows

Return to [Consistency and recovery](../concepts/consistency-and-recovery.html) before combining the
recipes. That page places aggregate persistence, saga progress, projection commits, and external work
on one timeline, which makes the correct composition easier to see.
