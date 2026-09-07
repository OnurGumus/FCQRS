(**
---
title: 2. Restart, project, and query
category: Learn FCQRS
categoryindex: 2
index: 4
---
*)

(*** hide ***)
#r "nuget: FCQRS, 6.3.0"
#load "../../samples/getting-started-fsharp/Document.fs"
#load "../../samples/getting-started-fsharp/Publication.fs"
#load "../../samples/getting-started-fsharp/Checks.fs"

(**
# 2. Restart, project, and query

The getting-started program has exited, but SQLite still holds its `DocumentCreated` event. Start a
new process and ask it to create the **same document ID** with different content.

## Run the restart experiment

Use the ID printed by your previous run, replacing `PASTE_DOCUMENT_ID` below. Keep the same language,
build configuration, and project: each sample keeps its own database beside its executable.

```text
dotnet run --project samples/getting-started-fsharp -- --recover PASTE_DOCUMENT_ID
```

<div class="cs-alt" data-fs="text" data-cs="text"></div>

```text
dotnet run --project samples/getting-started-csharp -- --recover PASTE_DOCUMENT_ID
```

Before running, predict the content and version. The recovery branch sends a create with
`replacement attempt`. For the document saved in the quickstart, the result is:

```text
recovery reply version 1; document contains 'first event'
```

If you changed the initial content in the quickstart, that original content appears instead.

The process began with empty memory. FCQRS recovered the original document from its persisted history
before handling this command. The decision found an existing document and returned it with
`DeferEvent`, so the version stayed at `1`.

**Try it:** run the recovery command again with the same ID. The answer stays the same.

If you see `replacement attempt`, check the ID and printed journal path. An ID with no stored history
is a new aggregate: this create stores its first event. `--recover` is an experiment that sends a
command, not a read-only lookup. An ordinary run prints a fresh ID and starts a fresh document.

## Aggregate state and query data are separate

The recovery experiment prints the **aggregate reply**. The ordinary run also reads a dictionary.
These are different paths:

<pre>
                              DocumentCreated in SQLite
                                /                 \
                               v                   v
                      fold / ApplyEvent     handleProjection
                               |                   |
                               v                   v
                       aggregate state       query dictionary
                       used for decisions    used for lookups
</pre>

An aggregate's state contains what its decisions need. A **read model** contains what a query needs.
The sample uses the same document shape for both so the data movement is visible. A different
projection could produce a list of document titles or a count, using the same recorded events.

Separating command handling from querying is **CQRS** (Command Query Responsibility Segregation).
Here the command decides and stores an outcome; the query reads a separately maintained view.

## Follow an event into the dictionary

The projection receives events from the journal and updates an in-memory dictionary. The dictionary
is concurrent because projection updates and application reads can happen on different threads.

The projection handles `DocumentCreated` by placing the document into the dictionary. In the same
handler, `DocumentEdited` replaces its content after checking that creation was already projected.
Open `handleProjection` / `HandleProjection` in `Program` to follow both cases.

The callback accepts `obj` (`object`) because a journal can contain events for different aggregates.
The type check selects this sample's events. `_offset` is the event's position in the projection's
source stream; it is not the version of one document.

The sample registers this handler at offset `0`. Each ordinary run starts with an empty dictionary
and replays the stored history. Saving an offset while discarding the dictionary would skip the very
events needed to rebuild it.

## Wait for the view you are about to read

The new document ID in an ordinary run ensures the first create stores an event. The request path is:

```fsharp
let correlationId = Fcqrs.newCid ()
use projected = subscriptions.Subscribe(correlationId, 1)
let! stored =
    documents.Send correlationId aggregateId (CreateDocument document) (fun _ -> true)
do! projected.Task.WaitAsync(TimeSpan.FromSeconds 30.) |> Async.AwaitTask
let queried = readModel[documentId]
```

<div class="cs-alt"></div>

```csharp
var correlationId = Values.NewCID();
using var projected = subscriptions.SubscribeForFirst(correlationId);
var stored = await documents(_ => true, correlationId, aggregateId, new CreateDocument(document));
await projected.Task.WaitAsync(TimeSpan.FromSeconds(30));
var queried = readModel[documentId];
```

Read these lines in order:

1. Create a correlation id for this request.
2. Subscribe to the projection's notification **before sending**. Subscribing later can miss it.
3. Send the create and await the aggregate reply. The `true` predicate accepts all reply types. The runner then inspects the reply; the editing chapter handles success and
   rejection separately.
4. Wait until this projection has applied the event and published its notification.
5. Query the dictionary.

This is **read-your-writes** for the selected projection. It does not establish that every projection
or external service is current. The subscription is in-memory coordination, not a durable client queue.

**Predict:** remove the projection wait. Will the query always fail? No. It might find the document,
or it might run before the dictionary contains it. One successful run would not prove that the
ordering is safe. Keep the wait in the program.

The repeated create has no new journal event. Waiting on its new correlation id would time out even
though the aggregate replied successfully. That is why the repeated and recovery paths inspect the
aggregate reply directly.

## Find the runtime setup in the sample

Now return to the host setup in the complete program:

| Setting or call | Role in this run |
|---|---|
| SQLite connection | Stores the event journal and snapshots in the printed database file. |
| `Fcqrs.actor` / `AddFcqrs` | Starts the actor runtime with the framework's defaults. |
| Aggregate registration | Connects the initial state, decision, fold, and persisted entity name. |
| `Fcqrs.wireSagaStarters` / `AddSaga` | Registers the publication workflow used in chapter 4. A create does not start it. |
| `Projection.single 0` / `AddProjection(..., lastOffset: 0)` | Rebuilds the dictionary and publishes a notification after its handler returns. |
| `api.Stop()` / `host.StopAsync()` | Stops the runtime after the experiment. |

The aggregate is registered before commands are sent. In an ordinary run the projection is also
registered before the first request. The recovery command inspects the aggregate reply directly.

Snapshots can shorten aggregate replay; they do not replace or repair the journal. The sample uses
the default snapshot and passivation settings. [Aggregate lifecycle](../concepts/aggregate-lifecycle.html)
explains these options when you need to choose them.

## Make a read model survive a crash

The dictionary is a teaching choice. For a durable read model, store its update and the source offset
in the **same transaction** when they share a database. A transaction commits both or neither.

| If the process crashes between separate writes | What the restart can do |
|---|---|
| Offset saved, data not saved | Skip an event whose update is missing. |
| Data saved, offset not saved | Apply an event again; a counter could increment twice. |

The quickstart avoids a saved-offset mismatch by rebuilding its entire in-memory view from zero.
As history grows, a durable view with transactional progress avoids that full rebuild on every run.
See [Add a projection](../how-to/add-a-projection.html) for the implementation.

## Check your understanding

- **Same ID after restart:** the existing document returns and no new creation is stored.
- **New ID:** a new aggregate starts at version `1`.
- **Dictionary lost:** replay can rebuild it from the journal.
- **Journal lost:** a dictionary is not a substitute for the missing event history.
- **Projection wait timed out:** the outcome is uncertain; do not assume the command failed.

Your document is still at version `1`. Continue to [3. Edit your document](3-edit-your-document.html)
to change its content deliberately, using the same ID and database.

*)

(*** hide ***)
Checks.documentChecks ()
printfn "Evaluated docs/tutorial/2-running-it.fsx"
