---
title: Task guide index
category: Apply
categoryindex: 4
index: 1
---

# Apply FCQRS: task guides

Choose the task you need. For a first working application, start with [registration](../get-started.html).

## Choose the task

| Task | Guide |
|---|---|
| Model commands, events, decisions, and folds | [Define an aggregate](define-an-aggregate.html) |
| Expose registration and queries over HTTP | [Register over HTTP](../tutorial/http-api.html) |
| Project events into query data | [Add a projection](add-a-projection.html) |
| Wait before querying your own change | [Read your writes](read-your-writes.html) |
| Coordinate several aggregate owners | [Write a saga](write-a-saga.html) |
| Test decisions, replay, and retries | [Test your domain](test-your-domain.html) |
| Change stored event contracts safely | [Evolve persisted events](evolve-events.html) |
| Rebuild derived query data | [Rebuild a read model](rebuild-a-read-model.html) |
| Run non-durable asynchronous work | [Dispatch an async effect](dispatch-async-effects.html) |
| Choose journal and snapshot storage | [Configure the database](configure-the-database.html) |
| Add logs, traces, and failure signals | [Observe your system](observability.html) |
| Build a C# host and domain | [Use FCQRS from C#](use-from-csharp.html) |

[Consistency and recovery](../concepts/consistency-and-recovery.html) explains how aggregate persistence,
projection commits, saga progress, and external work fit together.
