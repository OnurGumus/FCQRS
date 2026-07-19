---
title: Read your writes
category: How-to
categoryindex: 5
index: 6
---

# Read your writes

An aggregate can store an event before a projection updates its read model. If a request sends a command
and immediately queries, it may receive the previous view. Read-your-writes waits for the required
projection before performing that query.

> **Motivation:** Keep projections asynchronous for throughput and independence, then pay the waiting
> cost only for a request whose response must include its own change.

Read [Correlation IDs and read-your-writes](../concepts/correlation-ids.html) first if you need the
mental model behind the sequence, projection boundary, and ephemeral notification.

## Use the combined F# helper

`Fcqrs.sendAwaiting` subscribes before sending, sends the command, and waits for one projection
notification when the aggregate reply was journaled:

```fsharp
let subs = Fcqrs.projection api (Projection.single 0 handle)   // the ISubscribe stream

let! ack =
    Fcqrs.sendAwaiting subs documents cid id (CreateOrUpdate doc) (function
        | Document.Updated _ -> true
        | _ -> false)
// On return, this projection has published the matching event. Query its model now.
```

<div class="cs-alt"></div>

```csharp
// C# composes the same subscribe-before-send sequence explicitly.
using var projected = subscriptions.SubscribeForFirst(cid);

var reply = await documents(
    isExpectedReply,
    cid,
    documentId,
    new DocumentCommand.CreateOrUpdate(document));

if (reply.Journaled is not { Value: false })
    await projected.Task;

// This projection has now published the matching event. Query its model.
```

The helper waits for one notification. If a command persists a batch and the projection publishes
several events for the same CID, either filter notifications so only the final required update is
published or compose a subscription with the correct `take` count.

## Why "only if journaled"

An aggregate can persist an event or defer a reply. A deferred rejection or idempotent response is
returned to the caller but never enters the journal, so no projection will receive it.

FCQRS stamps the delivered envelope with `Event.Journaled : bool option`:

- `Some true`: the event was stored and can reach a projection;
- `Some false`: the reply was deferred or publish-only and will not reach a projection;
- `None`: the envelope predates or bypassed the delivery stamp.

`sendAwaiting` skips the projection wait for `Some false`. The C# sequence performs the equivalent
`Journaled` check explicitly.

## Compose the sequence manually

Use the explicit form when waiting for several notifications, adding cancellation, or applying a
notification filter:

```fsharp
use awaiter = subscriptions.Subscribe(cid, 1, cancellationToken = cancellationToken)

let! reply = documents.Send cid documentId command isExpectedReply

if reply.Journaled <> Some false then
    do! awaiter.Task |> Async.AwaitTask

// Query the model maintained by subscriptions.
```

The ordering is part of correctness. Subscribing after `.Send` creates a race in which the projection
can publish before the subscription exists.

<div class="cs-alt"></div>

```csharp
using var awaiter = subscriptions.SubscribeForFirst(cid);

var reply = await documents(
    isExpectedReply,
    cid,
    documentId,
    command);

if (reply.Journaled is not { Value: false })
    await awaiter.Task;

// Query the model maintained by subscriptions.
```

## Wait for the right projection

A notification means that the projection publishing it has completed its handler. It says nothing
about another projection with a different offset or deployment. If a response depends on several read
models, wait for a completion signal representing all of them.

Subscriptions are in-memory rendezvous points, not durable messages for disconnected clients. Create
the subscription as part of the active request, use a timeout or cancellation token, and decide how the
API reports a projection that does not catch up in time.
