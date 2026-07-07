---
title: Add a projection
category: How-to
categoryindex: 5
index: 3
---

# Add a projection

A projection is a function called once per event, in order. Fold each event into your read model and
record the offset **in the same transaction**, so processing is exactly-once across a crash. With the
`FCQRS.FSharp` facade the common shape is `int64 -> obj -> unit` (`Projection.single`): you just update
the read model, and FCQRS publishes each aggregate event to subscribers for you — which is what wakes a
read-your-writes wait.

```fsharp
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

    // advance the offset in the SAME transaction as the writes
    conn.Execute(
        "update Offsets set OffsetCount = @n where OffsetName = 'DocumentProjection'",
        {| n = offset |}, tx)
    |> ignore
    tx.Commit()
```

<div class="cs-alt"></div>

```csharp
// C#: the same projection as a void method — just update the read model.
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

    // advance the offset in the SAME transaction as the writes
    conn.Execute(
        "update Offsets set OffsetCount = @n where OffsetName = 'DocumentProjection'",
        new { n = offset }, tx);
    tx.Commit();
}
```

Register it from your composition root, resuming from the stored offset:

```fsharp
let subscriptions =
    Fcqrs.projection api
        (Projection.single (int (Db.getLastOffset connString)) (handle connString))
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

> **Filtering which events wake subscribers.** `Projection.single` publishes *every* aggregate event.
> To wake read-your-writes on only some events — e.g. suppress an intermediate event so the caller
> unblocks on the final one — use `Projection.filtered` (return `Publish` / `Suppress` per event) or
> `Projection.multi` (return the exact `IMessageWithCID list` to publish). In C#, the `Notify`-returning
> and `IList<IMessageWithCID>`-returning `.AddProjection` overloads do the same.

Two things matter most. **Track the offset in the same transaction as your writes** — that's what makes
processing exactly-once across a crash. And **rebuild freely**: to fix a projection bug, correct this
function, delete the read model, reset the offset to 0, and replay — the journal is untouched.
Background: [The read side](../concepts/read-models.html).
