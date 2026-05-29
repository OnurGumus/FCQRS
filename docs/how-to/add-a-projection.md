---
title: Add a projection
category: How-to
categoryindex: 5
index: 3
---

# Add a projection

A projection is a function called once per event, in order. Fold each event into your read model and
record the offset **in the same transaction**, so processing is exactly-once across a crash.

```fsharp
open FCQRS.Common
open FCQRS.Model.Data

let handleEventWrapper (loggerFactory: ILoggerFactory) (connString: string)
                       (offsetValue: int64) (event: obj) =
    use conn = new SqliteConnection(connString)
    conn.Open()
    use tx = conn.BeginTransaction()

    let emitted =
        match event with
        | :? Event<User.Event> as e ->
            match e.EventDetails with
            | User.RegisterSucceeded(u, _) ->
                conn.Execute("insert or replace into Users (Name) values (@u)", {| u = u |}, tx) |> ignore
                [ e :> IMessageWithCID ]          // re-publish so CID subscribers wake
            | _ -> []
        | _ -> []

    // advance the offset in the SAME transaction as the writes
    conn.Execute("update Offsets set N = @n where Name = 'Users'", {| n = offsetValue |}, tx) |> ignore
    tx.Commit()
    emitted
```

Start it from your bootstrap, resuming from the stored offset:

```fsharp
let lastOffset = readOffset connString          // your query for the saved offset
let subs = FCQRS.Query.init actorApi lastOffset (handleEventWrapper loggerFactory connString)
```

Two things matter most. **Return the events you handled** — those are re-published on the subscription
stream, which is what lets a caller know the read side has caught up (see
[read-your-writes](../concepts/consistency-and-recovery.html)). And **rebuild freely**: to fix a
projection bug, correct this function, delete the read model, reset the offset to 0, and replay — the
journal is untouched. Background: [The read side](../concepts/read-models.html).
