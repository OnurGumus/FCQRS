---
title: 5. Test changes and recovery
category: Learn FCQRS
categoryindex: 2
index: 7
---

# 5. Test changes and recovery

The same document has moved from creation through editing to publication. Its event history explains
its current content and publication status. Checks should protect those observable results when the
code changes.

## Run the checks you already have

```text
dotnet run --project samples/getting-started-fsharp -- --check
```

<div class="cs-alt" data-fs="text" data-cs="text"></div>

```text
dotnet run --project samples/getting-started-csharp -- --check
```

```text
All document, replay, and saga checks passed.
```

These checks run without an actor system. `Checks.fs` / `Checks.cs` tests decisions, folds, complete
histories, serialized values, and saga transitions. The helpers throw on a mismatch in Debug and Release.

| Check | Bug it catches |
|---|---|
| Create twice with different content | A repeated create overwrites the document. |
| Edit, then repeat that edit | An unchanged edit adds a duplicate event. |
| Replay creation, edit, request, and result | The fold rebuilds a different state. |
| Repeat `FinishPublication` | Saga recovery stores a second completion. |
| Exhaust a deadline, then deliver a late success | The saga wrongly treats an unknown outcome as rejection. |
| Round-trip every event, command, and saga state | A C# case is missing its JSON discriminator registration, or a persisted shape stops reading. |
| Read a saved creation fixture | A change breaks the earlier sample's event history. |

**Try it:** remove the C# `JsonDerivedType` registration for `DocumentEdited`, or change the F# fold
so a deferred rejection changes state. The checks should fail. Restore the code before continuing.

## Restart between the two owners

Create a third document with the ordinary quickstart command. Use its printed ID below. This exercise
pauses the sample after the slug reservation result is stored in the saga, before the saga reports
that result to the document.

```text
dotnet run --project samples/getting-started-fsharp -- --pause-publication THIRD_DOCUMENT_ID guides/recovery
```

<div class="cs-alt" data-fs="text" data-cs="text"></div>

```text
dotnet run --project samples/getting-started-csharp -- --pause-publication THIRD_DOCUMENT_ID guides/recovery
```

The program prints `publication paused after the reservation`, followed by the command arguments for
resuming, then stops its actor system. This pause is an explicit exercise hook; normal publication
sends the next command as soon as progress is stored.

Run the next command promptly, within the five-minute reporting deadline:

```text
dotnet run --project samples/getting-started-fsharp -- --publish THIRD_DOCUMENT_ID guides/recovery
```

<div class="cs-alt" data-fs="text" data-cs="text"></div>

```text
dotnet run --project samples/getting-started-csharp -- --publish THIRD_DOCUMENT_ID guides/recovery
```

```text
publication version 3; guides/recovery -> Published
query returned 'first event'
```

The third document was not edited, so it has three events. The recovered saga re-drives
`FinishPublication` from its stored `ReportingResult` state. It does not reserve a different slug or
start the workflow from scratch. The command remains safe if a previous attempt was already delivered.

If the persisted deadline has elapsed, the saga moves to `ReportUncertain`. It still accepts a late
matching completion, but the sample does not automatically restart its retry budget or declare failure.
Its CLI wait can time out. The pure checks exercise both this escalation and the late-answer path.

## Check the complete path against real storage

With Python 3 installed, run the repository's end-to-end check from its root:

```text
python3 scripts/check-learning-path.py
```

The script runs both projects in temporary databases, using the same CLI commands as the course. It
checks output, versions, repeated requests, competing documents, a paused saga's recovery, and both old
journal and old snapshot recovery. It rebuilds the in-memory projection from mixed old and new events
and verifies that the old creation row remains byte-for-byte unchanged.

The `DOCSTORE_DATABASE` environment variable selects those isolated test stores. Normal runs use the
database path printed by each sample.

## Keep the recorded contracts readable

Adding editing preserves `DocumentCreated` and adds `DocumentEdited`; it does not reinterpret a create
as an edit. The document's entity name and identity remain stable across the whole course.

F# retains the `Program.DocumentEvent` union's existing creation case and fields. `Document.fs` keeps
its original `Program` module name because earlier journal manifests include that name. Publication
state is an optional addition, so an old snapshot without it recovers with no publication requested.

The original C# sample stored `Event<DocumentCreated>`. The expanded sample handles the common
`Event<DocumentEvent>` envelope. `LegacyCreationReader.cs` adapts the old envelope when replaying,
retaining its payload, ID, version, timestamp, correlation id, and metadata. The projection uses the
same reader for old query-journal entries. The old `DocumentCreated` record and its fields remain
present. The adapter leaves stored rows untouched; its use follows Akka.NET's
[event-adapter mechanism](https://getakka.net/articles/persistence/event-adapters.html).

This reader solves this specific envelope change. It is not a general payload migration. A deployment
with old and new application versions running together must introduce compatible readers before
writing the new event cases. Keep fixtures from each retained format, then check mixed histories,
aggregate recovery, projection rebuilds, and saga recovery before release.

The four fixtures in `samples/fixtures` were captured from the previous samples' serialized events
and snapshots. New-format round-trip checks complement those fixtures; they do not replace them.

## Rebuild query data without rewriting history

The sample rebuilds its dictionary from offset zero at startup. A durable projection needs to commit
its data updates and source offset together when they share a store. To change a projection, replay
into a separate view and compare its results before switching queries to it. Keep the journal intact.

Stable journal names can protect future CLR type moves. They do not migrate changed fields or turn
one envelope type into another. [Evolve persisted events](../how-to/evolve-events.html) explains those
choices, and [Add a projection](../how-to/add-a-projection.html) covers transactional progress.

Continue to [6. Preparing for production](5-production.html). It applies the same document, slug,
and publication workflow to storage, monitoring, recovery, and deployment decisions.
