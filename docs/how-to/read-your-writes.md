---
title: Read your writes
category: How-to
categoryindex: 5
index: 9
---

# Read your writes

After you send a command you often want to *read the result back* — render the page that shows the thing you
just created. But the read model is updated by a [projection](add-a-projection.html) that runs slightly
after the command is acknowledged, so a naive "send, then read" can read a stale model. Read-your-writes
means: **wait until the projection has processed your event, then read.**

## One call

`Fcqrs.sendAwaiting` does the whole dance safely: it subscribes on the correlation id *before* sending
(the ordering that makes the wait race-free), sends, and awaits the projection — **only if** the ack was
journaled:

```fsharp
let subs = Fcqrs.projection api (Projection.single 0 handle)   // the ISubscribe stream

let! ack =
    Fcqrs.sendAwaiting subs documents cid id (CreateOrUpdate doc) (function
        | Document.Updated _ -> true
        | _ -> false)
// on return, the read model already reflects the write — read it now
```

## Why "only if journaled"

An aggregate can answer a command two ways: it can **persist** an event (which flows to the journal and
then the projection), or it can **defer** one — a rejection, or an idempotent no-op — which is delivered to
you but *never journaled*, so the projection will never see it. Both arrive as the same `Event` shape, so a
caller cannot tell them apart by looking at the payload.

FCQRS stamps the delivered envelope with `Event.Journaled : bool option` — `Some true` for a journaled ack,
`Some false` for a deferred one, `None` for an envelope that never passed aggregate delivery. `sendAwaiting`
reads it and skips the wait on a deferred ack, so a rejection returns immediately instead of hanging on a
projection event that will never arrive.

If you are composing the wait by hand, subscribe **before** you send, and gate the await on
`ack.Journaled <> Some false`.
