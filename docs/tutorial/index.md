---
title: Tutorial
category: Tutorial
categoryindex: 3
index: 1
---

# Tutorial: from first command to production

This course builds one application in stages. You begin with a pure function that decides whether a
document may change. You finish with two aggregates, a read model, a saga, recovery tests, and a
production checklist.

The application is a document store. A document can be created and edited. A user aggregate owns a
creation quota, and a saga coordinates the document and user when a new document is requested. The
same domain is available as a complete application in
[`focument_fsharp`](https://github.com/OnurGumus/focument_fsharp).

By the end, you will be able to:

- choose an aggregate boundary and explain which races it eliminates;
- model commands, persisted events, deferred replies, and derived state;
- build a projection and use its offset correctly;
- wait for a specific projection before querying it;
- coordinate aggregates without sharing their state;
- make commands safe under retries and keep replay deterministic;
- evolve persisted message types without treating events like ordinary DTOs;
- configure storage, snapshots, diagnostics, and cluster deployment.

## What you need

- The **.NET 10 SDK**. `dotnet --version` should print `10.*`.
- A scratch project. Each practical chapter adds to its `Program.fs`:

```bash
dotnet new console -lang F# -n DocStore
cd DocStore
dotnet add package FCQRS --prerelease
```

Run the project at any point with `dotnet run`. The first running chapter creates `tutorial.db`, which
contains the event journal and snapshots. Delete `tutorial.db*` only when you intentionally want to
discard the tutorial history and begin again.

The F# code in the executable chapters is checked while this documentation site is built. C# versions
of the important types and functions appear beside the F# examples.

## The five stages

1. **[Model the aggregate](1-the-aggregate.html):** separate requests from recorded facts, define
   validated domain values, and write the `decide` and `fold` functions.
2. **[Run the write and read paths](2-running-it.html):** host the aggregate, store events, project them
   into query data, and wait for the projection before reading.
3. **[Coordinate with a saga](3-adding-a-saga.html):** enforce a user quota across document and user
   aggregates while keeping each consistency boundary independent.
4. **[Test and evolve the system](4-testing-and-evolution.html):** test decisions, replay, retry
   behaviour, and changes to persisted event contracts.
5. **[Prepare for production](5-production.html):** configure durable storage, failure handling,
   observability, snapshots, backups, and cluster deployment.

Read the stages in order the first time. The [concept pages](../concepts/index.html) explain each model
in more depth, while the [how-to guides](../how-to/index.html) are shorter references for use after the
course.
