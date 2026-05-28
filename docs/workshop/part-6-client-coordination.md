---
title: Part 6 · Client Coordination
category: Workshop
categoryindex: 4
index: 7
---

# Part 6 — Telling the client it is done

There is a gap at the heart of every CQRS system, and pretending it is not there is how people end
up disliking CQRS. The write side persists an event; a moment later the projection folds it into the
read model. Those are two steps, not one, and for the sliver of time between them the read model is
behind. A client that sends a command and immediately queries can read its own change as if it never
happened. This is *eventual consistency*, and it is real.

FCQRS does not pretend the gap away. It gives you a precise signal for when it has closed, built on
the correlation id from Part 2. The rule is one sentence: **subscribe to your correlation id before
you send the command, then wait for the subscription to fire.** The sequence looks like this.

![Read-your-writes — subscribe to the CID before you send](../img/cid-subscribe.svg)

## The shape of a write handler

Focument's `createOrUpdateDocument` is the canonical example. Read it for the *order* of operations
more than the details — the order is the whole lesson.

```fsharp
// focument/src/Server/Handlers.fs  (trimmed to the essential sequence)
let correlationId = cid ()

// 1. SUBSCRIBE FIRST — to this CID, for the event we care about, take 1
use awaiter =
    subs.Subscribe(correlationId,
                   (function DocEvent (Document.Approved _) -> true | _ -> false), 1)

// 2. send the command to the aggregate
let! _ =
    commandHandler.DocumentHandler
        (function Document.Approved _ -> true | _ -> false)   // event the write side waits for
        correlationId
        aggregateId                                           // routes to the right actor
        (Document.CreateOrUpdate document)

// 3. block until the projection has processed our event
do! awaiter.Task
return "Document received!"
```

```csharp
// focument-csharp/src/Server/Handlers.cs  (trimmed to the essential sequence)
var correlationId = getCid();

// 1. SUBSCRIBE FIRST — to this CID, take 1
using var awaiter = ISubscribeExtensions.SubscribeFor(subs, correlationId, 1);

// 2. send the command to the aggregate
var handler = commandHandler.DocumentHandler;
await handler(
    e => e is DocumentEvent.Approved,        // event the write side waits for
    correlationId,
    aggregateId,                             // routes to the right actor
    new DocumentCommand.CreateOrUpdate(document));

// 3. block until the projection has processed our event
await awaiter.Task;
return "Document received!";
```

The ordering of steps 1 and 2 is not stylistic; it is the bug fix. If you sent the command first and
subscribed afterwards, the event could be persisted and projected in the instant before your
subscription existed, and you would wait forever for a notification that already came and went.
Subscribing first means the subscription is in place to catch the event no matter how fast the rest
happens. The `use` / `using` on the awaiter matters too: it disposes the subscription when the
handler returns, so subscriptions do not pile up.

## Two waits, and why there are two

Look closely and you will notice the handler waits *twice*, on two different things, and they are not
redundant.

The first wait is inside `commandHandler.DocumentHandler`. That call sends the command and returns an
async result that completes when the *aggregate* emits an event matching the filter you pass it. That
is a **write-side** confirmation: "the command was processed and the fact was persisted." It tells
you nothing about the read model.

The second wait is `awaiter.Task`, the correlation-id subscription. It completes when the
**projection** republishes an event for this CID — which, as we saw at the end of Part 5, only
happens *after* the projection has committed its rows. That is the **read-side** confirmation: "the
view you are about to query is now current."

Together they give you true read-your-writes over an eventually-consistent read model: by the time
the handler returns `"Document received!"`, the command is durably persisted *and* the read model
reflects it, so the client's next query cannot be stale. No polling loop, no arbitrary `sleep`, no
"save and refresh and hope" — just a precise rendezvous on a correlation id.

## A detail worth flagging honestly

Both handlers filter for `Document.Approved`, not `Document.CreatedOrUpdated`. At first that looks
like a mistake — why wait for "approved" when you only created a document? The answer is specific to
Focument: it runs an approval saga that auto-approves every new document (we meet it next, in Part
7). So in this application `Approved` is the genuine "the whole workflow finished" signal, and
waiting for it is what makes the UI show an approved document. It is a design choice tailored to this
app's workflow, not a universal rule — if you forked Focument and removed the saga, you would change
these filters to wait for `CreatedOrUpdated` instead, or the request would hang waiting for an
approval that never comes. It is called out here because copying it blindly into an app *without*
such a saga is a classic way to make every write appear to time out.

This is also a good moment to appreciate how much the correlation id has been quietly doing. The same
CID was minted here, stamped onto the command, carried onto the aggregate's event, threaded through
the saga's follow-up commands and their events, and finally matched by this subscription when the
projection republished it. One value, one thread, from HTTP request to read-model confirmation — and
in distributed-tracing setups that same id doubles as the trace id, so the whole round trip shows up
as one correlated story in your telemetry.

We have now seen the command side, the event log, the read side, and the client rendezvous — the
full CQRS loop. In [Part 7](part-7-sagas.md) we give the side effects their due: the saga that turns
"a document was created" into "generate a code, request approval, and eventually approve," reliably
and with the restart-safety the version number hinted at back in Part 4.
