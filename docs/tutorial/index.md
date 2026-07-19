---
title: Tutorial
category: Tutorial
categoryindex: 3
index: 1
---

# Tutorial: from first command to production

This course builds one application in stages. You begin with a pure function that decides whether a
document may change. You finish with a document aggregate, a slug aggregate, a read model, a
publication saga, recovery tests, and a production checklist.

The application is a document store. A document can be created and edited. Publishing requires a
unique URL slug, so a saga coordinates the document with the aggregate that owns that slug. The rule
is deliberately small enough to keep state transitions, safe startup, and recovery visible.

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

## Learn in three layers

Each chapter links three kinds of material. Use them together rather than treating the tutorial as an
isolated code walkthrough:

| Layer | Purpose | When to use it |
|---|---|---|
| Tutorial | Build one application in a deliberate sequence | First encounter with the framework |
| Concept | Understand the problem, guarantee, and limitation | Before or after the matching chapter |
| How-to | Repeat one implementation task quickly | While building your own application |

Every chapter begins with suggested concept and how-to companions, and ends by pointing to the next
useful references.

## The five stages

1. [Model the aggregate](1-the-aggregate.html): separate requests from recorded facts, define
   validated domain values, and write the `decide` and `fold` functions.
2. [Run the write and read paths](2-running-it.html): host the aggregate, store events, project them
   into query data, and wait for the projection before reading.
3. [Coordinate with a saga](3-adding-a-saga.html): reserve a unique URL slug and publish the document
   while keeping both consistency boundaries independent.
4. [Test and evolve the system](4-testing-and-evolution.html): test decisions, replay, retry
   behaviour, and changes to persisted event contracts.
5. [Prepare for production](5-production.html): configure durable storage, failure handling,
   observability, snapshots, backups, and cluster deployment.

Read the stages in order the first time. The [concept pages](../concepts/index.html) explain each model
in more depth, while the [how-to guides](../how-to/index.html) are shorter references for use after the
course.
