---
title: Read your writes
category: Apply
categoryindex: 4
index: 5
---

# Read your writes

Deposit 100 into Alice's account and immediately show her statement. With an asynchronous projection
the statement can still end before the deposit: the account has stored `Deposited 100`, but the
statement projection has not applied it yet. Nothing is broken, and a later query would show the
deposit. A response that must include the caller's own change cannot ship "refresh in a moment", so
read-your-writes closes the gap by waiting for the required projection before the query runs.

> **Motivation:** Keep projections asynchronous for throughput and independence, then pay the waiting
> cost only for a request whose response must include its own change.

Read [Correlation IDs and read-your-writes](../concepts/correlation-ids.html) first if you need the
mental model behind the sequence, projection boundary, and ephemeral notification.

To wait for every event already committed across the journal, use a transactional projection's
`CatchUpAsync`. [Catch up projections](catch-up-projections.html) shows the registration and the
snapshot boundary. A matching correlation notification alone does not establish that boundary.

## Use the combined F# helper

`Fcqrs.sendAwaiting` subscribes before sending, sends the command, and waits for one projection
notification when the aggregate reply was journaled:

```fsharp
// The ISubscribe stream of the statement projection.
let statement = Fcqrs.projection api (Projection.single 0 handle)

// The last argument selects the account's reply: Deposited or Rejected.
let! reply =
    Fcqrs.sendAwaiting statement accounts cid alice (Deposit 100m)
        (fun _ -> true)
// On return, the statement projection has handled the deposit. Query it now.
```

<div class="cs-alt"></div>

```csharp
// C# composes the same subscribe-before-send sequence explicitly.
using var projected = statement.SubscribeForFirst(cid);

// The first argument selects the account's reply: Deposited or Rejected.
var reply = await accounts(_ => true, cid, alice, new Deposit(100m));

if (reply.Journaled?.Value != false)
    await projected.Task.WaitAsync(TimeSpan.FromSeconds(30));

// The statement projection has now handled the deposit. Query it.
```

The helper waits for one notification. If a command persists a batch and the projection publishes
several events for the same CID, either filter notifications so only the final required update is
published or compose a subscription with the correct `take` count.

The F# wait is bounded: if no matching notification arrives within `akka.fcqrs.command-timeout`,
`sendAwaiting` raises `TimeoutException`. The default is 30s. A bare number means seconds, and HOCON
durations such as `500ms` also work. A projection that suppresses or filters out the matching event therefore surfaces
as a timeout instead of hanging the request. See
[Configuration](../configuration.html).

## Why "only if journaled"

An aggregate can persist an event or defer a reply. A deferred rejection or idempotent response is
returned to the caller but never enters the journal, so no projection will receive it.

FCQRS stamps the delivered envelope with `Event.Journaled : bool option`:

- `Some true`: the event was stored and can reach a projection;
- `Some false`: the reply was deferred, like `Rejected`, or publish-only and will not reach a
  projection;
- `None`: the envelope predates or bypassed the delivery stamp.

`sendAwaiting` skips the projection wait for `Some false`. The C# sequence performs the equivalent
`Journaled` check explicitly.

## Compose the sequence manually

Use the explicit form when waiting for several notifications, adding cancellation, or applying a
notification filter:

```fsharp
use awaiter = statement.Subscribe(cid, 1, cancellationToken = cancellationToken)

let! reply = accounts.Send cid alice command (fun _ -> true)

if reply.Journaled <> Some false then
    do! awaiter.Task |> Async.AwaitTask

// Query the statement.
```

The ordering is part of correctness. Subscribing after `.Send` creates a race in which the projection
can publish before the subscription exists.

`Subscribe` registers the listener before returning. Disposing or cancelling an awaitable subscription
before it receives the requested number of notifications cancels its task; disposal does not report
that the projection has caught up.

<div class="cs-alt"></div>

```csharp
using var awaiter = statement.SubscribeForFirst(cid);

var reply = await accounts(_ => true, cid, alice, command);

if (reply.Journaled?.Value != false)
    await awaiter.Task.WaitAsync(cancellationToken);

// Query the statement.
```

## Wait for the right projection

A notification means that the projection publishing it has completed its handler. It says nothing
about another projection with a different offset or deployment: a statement notification does not
mean a monthly report built by another projection includes the deposit. If a response depends on several read
models, wait for a completion signal representing all of them.

Subscriptions are in-memory rendezvous points, not durable messages for disconnected clients. Create
the subscription as part of the active request, and decide how the API reports a projection that does
not catch up in time.

The timeout story differs by API. The F# `sendAwaiting` helper is bounded by
`akka.fcqrs.command-timeout` (default 30s) and raises `TimeoutException`. Raw `Subscribe` awaiters and
the C# `SubscribeForFirst` awaiter are **not** bounded by that key. Bound them with `WaitAsync` in C#,
as the examples on this page do, or with a cancellation token in F#.
