---
title: Learning path
category: Learn FCQRS
categoryindex: 2
index: 1
---

# Learn FCQRS: one path from zero to production

This is the beginner path. Stay inside these numbered pages on the first pass. Each stage introduces
the vocabulary and reasoning it needs, builds on the previous stage, and ends with one clear next
step. You do not need to open a concept page or how-to guide to complete the course.

The course builds one document application. Documents can be created and edited. Publishing requires
a unique URL slug, so the application eventually adds a second aggregate and a resumable saga. The
domain is intentionally small so state, events, projection progress, retries, and recovery remain
visible.

<pre>
0. See one complete request
        |
1. Model one decision
        |
2. Persist, project, and query
        |
3. Coordinate two owners
        |
4. Prove replay and compatibility
        |
5. Prepare the system for failure
</pre>

## Follow the stages in order

| Stage | Question answered | What you build |
|---:|---|---|
| [0. Quickstart](../get-started.html) | What does one FCQRS request do end to end? | One command, stored event, projection, and query |
| [1. The aggregate](1-the-aggregate.html) | Where does a business decision live? | Validated values, command, event, `decide`, and `fold` |
| [2. Wiring and running it](2-running-it.html) | How does durable state become query data? | SQLite journal, actor runtime, projection, and read-your-writes |
| [3. Adding a saga](3-adding-a-saga.html) | How do independent owners coordinate safely? | Slug aggregate and resumable publication workflow |
| [4. Testing and evolution](4-testing-and-evolution.html) | How can this system change without breaking history? | Decision, replay, retry, and compatibility tests |
| [5. Preparing for production](5-production.html) | What must survive or become visible in production? | Storage, recovery, diagnostics, backup, and deployment checklist |

Start at stage 0 even if you know CQRS. It gives the names used throughout the rest of the course. If
you already ran one of the repository samples, stage 0 will be a short review.

## What you need

- The **.NET 10 SDK**. `dotnet --version` should print `10.*`.
- A scratch F# project for the executable course:

```bash
dotnet new console -lang F# -n DocStore
cd DocStore
dotnet add package FCQRS --prerelease
```

Run the project at any point with `dotnet run`. Chapter 2 creates `tutorial.db`, which contains the
event journal and snapshots. Delete `tutorial.db*` only when you intentionally want to discard that
history and begin again.

Every teaching block has an adjacent F# and C# tab. The executable F# is checked during the docs build.
The fuller C# examples use preview discriminated unions; the stable .NET 10
[C# sample](https://github.com/OnurGumus/FCQRS/tree/main/samples/getting-started-csharp) uses ordinary
concrete message types. The course explains the architectural model identically in both languages.

## Use the other sections after the course introduces a topic

- **Understand** explains guarantees and failure boundaries in greater depth. These pages are optional
  during the first pass.
- **Apply** contains short task recipes for work in your own application. Use it after the matching
  course stage.
- **Reference** lists configuration keys and API details. Consult it when choosing exact options.

This separation is deliberate: the learning path teaches in dependency order; the other sections help
you deepen or reuse something you have already encountered.

Continue to [0. Quickstart](../get-started.html).
