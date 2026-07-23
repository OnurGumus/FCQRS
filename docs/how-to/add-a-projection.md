---
title: Add a projection
category: Apply
categoryindex: 4
index: 3
---

# Add a projection

A projection receives journal events in order and updates data designed for queries. It also records an
offset identifying the last event it committed. This page's single most important rule: commit the
read-model update and the new offset in the same database transaction.

The rule matters because a crash can land between any two separate writes, and each ordering fails
differently:

- **Offset first, then data.** A crash in between advances the bookmark past an event that never
  reached the read model. On restart the projection resumes after it. Nothing errors; the view is
  simply missing that update and stays wrong until the read model is rebuilt.
- **Data first, then offset.** A crash in between leaves the bookmark behind, so the event is applied
  again on restart. An insert-or-replace absorbs the repeat; a counter or an append does not, and the
  view drifts.
- **Same transaction.** The crash commits both or neither. Retrying the uncommitted event gives
  exactly-once updates within that store.

> **Motivation:** Storing data and offset together removes ambiguity after a restart. The projection
> either committed the event and moves past it, or committed neither and can safely try it again.

Create the read model and one offset row for this projection:

```sql
create table if not exists Documents (
    Id text primary key,
    Title text not null,
    Body text not null
);

create table if not exists Offsets (
    OffsetName text primary key,
    OffsetCount integer not null
);

insert or ignore into Offsets (OffsetName, OffsetCount)
values ('DocumentProjection', 0);
```

## Handle every event transactionally

The handler receives the journal offset and an `obj` because the stream contains events from every
aggregate and saga. Match the envelope types this projection needs. Advance the offset for every event,
including event types that do not change this read model.

```fsharp
// NuGet: Dapper, Microsoft.Data.Sqlite
open Dapper
open Microsoft.Data.Sqlite
open FCQRS.Common
open FCQRS.FSharp

let handle (connString: string) (offset: int64) (event: obj) : unit =
    use conn = new SqliteConnection(connString)
    conn.Open()
    use tx = conn.BeginTransaction()

    match event with
    | :? Event<Document.Event> as e ->
        match e.EventDetails with
        | Document.Updated doc ->
            conn.Execute(
                "insert or replace into Documents (Id, Title, Body) values (@Id, @Title, @Body)",
                {| Id = doc.Id.ToString(); Title = doc.Title.Value; Body = doc.Content.Value |}, tx)
            |> ignore
        | _ -> ()
    | _ -> ()

    // Advance for every event, in the same transaction as the read-model write.
    conn.Execute(
        "update Offsets set OffsetCount = @n where OffsetName = 'DocumentProjection'",
        {| n = offset |}, tx)
    |> ignore
    tx.Commit()
```

<div class="cs-alt"></div>

```csharp
// C#: the same projection as a void method.
using static FCQRS.Common;   // Event<>
using Dapper;
using Microsoft.Data.Sqlite;

public static void HandleEventWrapper(string connString, long offset, object eventObj)
{
    using var conn = new SqliteConnection(connString);
    conn.Open();
    using var tx = conn.BeginTransaction();

    if (eventObj is Event<DocumentEvent> { EventDetails: DocumentEvent.Updated u })
        conn.Execute(
            "insert or replace into Documents (Id, Title, Body) values (@Id, @Title, @Body)",
            new { Id = u.Document.Id.ToString(), Title = u.Document.Title.ToString(), Body = u.Document.Content.ToString() }, tx);

    // Advance for every event, in the same transaction as the read-model write.
    conn.Execute(
        "update Offsets set OffsetCount = @n where OffsetName = 'DocumentProjection'",
        new { n = offset }, tx);
    tx.Commit();
}
```

## Resume from the stored offset

Read `DocumentProjection` from `Offsets` during startup and pass that value to the projection:

```fsharp
let getLastOffset (connString: string) : int64 =
    use conn = new SqliteConnection(connString)
    conn.Open()

    conn.ExecuteScalar<int64>(
        "select OffsetCount from Offsets where OffsetName = 'DocumentProjection'")

let subscriptions =
    Fcqrs.projection api
        (Projection.single (getLastOffset connString) (handle connString))
```

<div class="cs-alt"></div>

```csharp
// C#: resolve both the handler and last offset from application services.
static long GetLastOffset(string connString)
{
    using var conn = new SqliteConnection(connString);
    conn.Open();
    return conn.ExecuteScalar<long>(
        "select OffsetCount from Offsets where OffsetName = 'DocumentProjection'");
}

services.AddProjection(
    handler: sp => (offset, evt) => HandleEventWrapper(connString, offset, evt),
    lastOffset: _ => GetLastOffset(connString));
```

`Fcqrs.projection` returns an `ISubscribe`. A client can subscribe to a correlation id and wait until
this handler commits the matching event. Aggregate `.Send` waits only for the aggregate reply; the
projection subscription is the separate read-side confirmation.

The C# host builder supports one projection per FCQRS runtime: a second `AddProjection` call throws
`InvalidOperationException` at registration. A handler may update several read models in the same
process, and a side-by-side rebuild runs as a separate process (see
[Rebuild a read model](rebuild-a-read-model.html)). Independently deployed projection consumers should
keep independent offsets.

## Choose which events notify callers

| F# helper | C# handler result | Subscription behaviour |
|---|---|---|
| `Projection.single` | `void` | publish every aggregate event after handling |
| `Projection.filtered` | `Notify` | publish or suppress the handled aggregate event |
| `Projection.multi` | `IMessageWithCID list` | publish the exact notification list returned |

Use filtering when one command produces several events but a caller should wake only after the event
that completes all required read-model updates. The notification must be published only after those
updates commit.

## Handle failures visibly

Do not catch a storage exception and advance the offset. Let the handler fail. FCQRS terminates the
process when a projection handler fails so the stream cannot stop silently while the host appears
healthy. The process supervisor can restart it from the last committed offset after the storage problem
or handler bug is corrected.

A projection writing to several stores cannot use one local transaction for all updates. Make each
destination idempotent and record enough progress to retry safely.

To correct derived data, follow [Rebuild a read model](rebuild-a-read-model.html). Do not edit the event
journal to repair a projection. Background: [The read side](../concepts/read-models.html).
