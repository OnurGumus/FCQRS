(**
---
title: 0. Save your first document
category: Learn FCQRS
categoryindex: 2
index: 2
---
*)

(*** hide ***)
#r "nuget: FCQRS, 6.3.0"
#load "../samples/getting-started-fsharp/Document.fs"
#load "../samples/getting-started-fsharp/Publication.fs"
#load "../samples/getting-started-fsharp/Checks.fs"

(**
# 0. Save your first document

DocStore saves a document titled **FCQRS notes** and reads its content back. Then it tries to create
that same document again with different content. The original survives.

Run it first. You need Git, the .NET 10 SDK selected by the repository's `global.json`, and basic F#
or C#. The first run restores packages, so it needs an internet connection. Both samples use stable
.NET 10 and create their own SQLite database; no separate database server is needed.

## Run a complete sample

If you already have the repository, open a terminal at its root. Otherwise:

```text
git clone https://github.com/OnurGumus/FCQRS.git
cd FCQRS
```

Choose a language. Use the same project and database throughout the course.

```text
dotnet run --project samples/getting-started-fsharp
```

<div class="cs-alt" data-fs="text" data-cs="text"></div>

```text
dotnet run --project samples/getting-started-csharp
```

Both programs print these result lines, followed by a generated document ID and the database path:

```text
stored version 1; query returned 'first event'
repeat reply version 1; document contains 'first event'
document id: <your generated id>
journal: <path to the sample database>
```

Keep the document ID for the restart experiment in chapter 2.

The second request tried to replace the content with `replacement attempt`. The version stayed at
`1` and the content stayed `first event`: a create request cannot overwrite an existing document.

## Follow the first request

Open the sample folder in your editor:

- [F# sample](https://github.com/OnurGumus/FCQRS/tree/main/samples/getting-started-fsharp)
- [C# sample](https://github.com/OnurGumus/FCQRS/tree/main/samples/getting-started-csharp)

`Document.fs` / `Document.cs` contains the document and its rules. `Program.fs` / `Program.cs` runs
the exercises. `Publication.fs` / `Publication.cs` adds the workflow introduced later, and `Checks.fs` / `Checks.cs`
contains runnable checks. Start with the create path; return to the other commands when the course
uses them. You do not need to assemble excerpts into a second project.

| In the program | What happens to this document |
|---|---|
| `CreateDocument` | Requests that FCQRS create **FCQRS notes**. A request is a **command**. |
| `decide` / `HandleCommand` | Checks whether this document already exists. |
| `PersistEvent` / `EventActions.Persist` | Stores `DocumentCreated` in SQLite. This recorded outcome is an **event**. |
| `fold` / `ApplyEvent` | Applies that event to the document's current state. |
| `HandleProjection` / `handleProjection` | Copies the stored document into a dictionary for lookup. This transformation is a **projection**. |
| `readModel[documentId]` | Reads the document from that dictionary, the **read model**. |

<pre>
CreateDocument
      |
      v
Does this document exist? -- yes --> reply with the existing document
      |
      no
      |
      v
store DocumentCreated --> apply it to aggregate state
      |
      v
projection updates dictionary --> query returns 'first event'
</pre>

The decision belongs to an **aggregate**: the state and rules for one document. FCQRS gives each
document its own actor, a runtime component that processes that document's commands one at a time.
Two commands for this document cannot race over its state. Commands for different documents can run
concurrently.

## Two waits, two different results

The sample waits for the aggregate's reply and then for the projection. They answer different questions:

| Wait | What it establishes here |
|---|---|
| `documents.Send` in F#, or `await documents(...)` in C# | The aggregate returned the matching reply. For the first create, the event was stored. |
| `projected.Task` | This projection has updated its dictionary and published the matching notification. The query can now see the document. |

The subscription is created **before** the command is sent. Otherwise the projection might finish
before the program starts listening. A **correlation id** connects this request with its notification.
It identifies the request; the document ID identifies the document across requests.

The repeated create returns a reply without storing another event. There is no new journal event for
the projection to process, so the sample does not wait for a projection notification for that reply.

## Try a change

In the program, change the initial content from `first event` to `my first document`. Keep
`replacement attempt` as it is. Before running, predict both content lines.

Run the same command again. Both lines now contain `my first document`, and both versions are `1`.
Each ordinary run uses a new document ID, so it begins a separate document history. The repeated create
still preserves that run's original content.

Restore `first event` before following the printed examples in the next chapters.

## If your run stops early

- **SDK error:** run `dotnet --version` at the repository root and check that .NET 10 is installed.
- **Restore error:** the first build needs access to NuGet. Resolve that error before investigating FCQRS.
- **Projection timeout:** the sample stops waiting after 30 seconds. A timeout means the confirmation
  did not arrive in time; it does not prove that the command failed or that the event was not stored.
- **SQLite locked:** let a previous run finish before running the same project again. The printed
  journal path identifies the file that this sample uses.

SQLite retains the event history after the program exits. The dictionary lives only in memory and is
rebuilt from that history on ordinary runs. This is suitable for the exercise; a durable read model
needs to save its progress with its updates, which chapter 2 explains.

## Next: explain the repeated create

Continue to [1. Make one document decision](tutorial/1-the-aggregate.html). You will trace the two
functions that make the second line stay at version `1`, then check them without a database.


The later editing and publication commands are already included in the sample. The next chapters
activate them one at a time using the document ID you just saved. The initial document, its history,
and its database remain the same.

*)

(*** hide ***)
Checks.documentChecks ()
printfn "Evaluated docs/get-started.fsx"
