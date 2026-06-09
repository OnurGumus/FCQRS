---
title: Add a projection
category: How-to
categoryindex: 5
index: 3
---

# Add a projection

A projection is a function called once per event, in order. Fold each event into your read model and
record the offset **in the same transaction**, so processing is exactly-once across a crash. With the
`FCQRS.FSharp` facade the function has the shape `int64 -> obj -> IMessageWithCID list`: the offset, the
event, and the read-model events to re-publish to subscribers.

```fsharp
open FCQRS.Common
open FCQRS.FSharp

let handle (connString: string) (offset: int64) (event: obj) : IMessageWithCID list =
    use conn = new SqliteConnection(connString)
    conn.Open()
    use tx = conn.BeginTransaction()

    let notify =
        match event with
        | :? Event<Document.Event> as e ->
            match e.EventDetails with
            | Document.Updated doc ->
                conn.Execute(
                    "insert or replace into Documents (Id, Title, Body) values (@Id, @Title, @Body)",
                    {| Id = doc.Id.ToString(); Title = doc.Title.Value; Body = doc.Content.Value |}, tx)
                |> ignore
                // re-publish so CID subscribers wake
                [ e :> IMessageWithCID ]
            | _ -> []
        | _ -> []

    // advance the offset in the SAME transaction as the writes
    conn.Execute(
        "update Offsets set OffsetCount = @n where OffsetName = 'DocumentProjection'",
        {| n = offset |}, tx)
    |> ignore
    tx.Commit()
    notify
```

<div class="cs-alt"></div>

```csharp
// C#: the same projection as a method returning the events to re-publish.
using static FCQRS.Common;        // Event<>
using static FCQRS.Model.Data;     // IMessageWithCID
using Dapper;
using Microsoft.Data.Sqlite;

public static IList<IMessageWithCID> HandleEventWrapper(
    string connString, long offset, object eventObj)
{
    using var conn = new SqliteConnection(connString);
    conn.Open();
    using var tx = conn.BeginTransaction();
    var notify = new List<IMessageWithCID>();

    if (eventObj is Event<DocumentEvent> e && e.EventDetails is DocumentEvent.Updated u)
    {
        conn.Execute(
            "insert or replace into Documents (Id, Title, Body) values (@Id, @Title, @Body)",
            new { Id = u.Document.Id.ToString(), Title = u.Document.Title.ToString(), Body = u.Document.Content.ToString() }, tx);
        notify.Add(e);   // re-publish so CID subscribers wake
    }

    // advance the offset in the SAME transaction as the writes
    conn.Execute(
        "update Offsets set OffsetCount = @n where OffsetName = 'DocumentProjection'",
        new { n = offset }, tx);
    tx.Commit();
    return notify;
}
```

Register it from your composition root, resuming from the stored offset:

```fsharp
let subscriptions =
    Fcqrs.projection api
        { LastOffset = int (Db.getLastOffset connString)
          Handle = handle connString }
```

<div class="cs-alt"></div>

```csharp
// C#: register via the DI host-builder, resuming from the stored offset.
services.AddProjection(
    handler: sp => (offset, evt) => HandleEventWrapper(connString, offset, evt),
    lastOffset: _ => (int)ServerQuery.GetLastOffset(connString));
```

`Fcqrs.projection` returns an `ISubscribe` — the same subscription stream `.Send` and the
read-your-writes wait use. (`subscriptions.Subscribe(cid, 1)` waits for one event with a given
correlation id.)

Two things matter most. **Return the events you handled** — those are re-published on the subscription
stream, which is what lets a caller know the read side has caught up (see
[read-your-writes](../concepts/consistency-and-recovery.html)). And **rebuild freely**: to fix a
projection bug, correct this function, delete the read model, reset the offset to 0, and replay — the
journal is untouched. Background: [The read side](../concepts/read-models.html).
