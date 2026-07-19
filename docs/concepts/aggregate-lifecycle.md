---
title: Deferring, snapshots, and passivation
category: Concepts
categoryindex: 4
index: 7
---

# Deferring, snapshots, and passivation

An active aggregate has state in memory, but memory is not the source of truth. FCQRS can stop the
actor, move its identity to another node, or restart the process and still rebuild the same state.

Deferring, snapshotting, and passivation make sense once their different jobs are separated:

- **deferring** returns an outcome without adding it to history;
- **snapshotting** creates a recovery checkpoint from durable history;
- **passivation** stops an actor and releases its in-memory state.

Only persisted events define recoverable aggregate state.

## Three forms of state

It helps to distinguish three layers:

| Layer | Durable? | Purpose |
|---|---:|---|
| Active actor state | no | decide the next command quickly |
| Snapshot | yes | begin recovery from a later checkpoint |
| Journal events | yes | record the facts from which state is derived |

The actor's state is a working copy. A snapshot is an optimization. The journal is the history that
explains how the aggregate reached its state.

## Deferring returns an outcome without recording it

A command does not always create a new fact. Cancelling an already shipped order may need to return
`OrderAlreadyShipped`, but that verdict does not change the order.

`DeferEvent` follows this path:

```text
command -> decide -> deferred reply -> caller
                         |
                         +-> folded in the active actor

no journal append
no persisted version increment
no journal projection event
```

FCQRS folds the deferred reply in the active actor, so its fold must preserve recoverable state. If a
deferred reply changes state, that change exists only in memory and disappears after passivation or a
restart.

Use a deferred reply for a rejection or idempotent verdict whose meaning is “nothing new happened.”
Persist the outcome instead when it must become part of history, change future decisions, or reach
journal projections.

For example:

```fsharp
let decide command state =
    match command, state with
    | CancelOrder, Shipped -> OrderAlreadyShipped |> DeferEvent
    | CancelOrder, Cancelled -> AlreadyCancelled |> DeferEvent
    | CancelOrder, _ -> OrderCancelled |> PersistEvent

let fold event state =
    match event with
    | OrderCancelled -> Cancelled
    | OrderAlreadyShipped
    | AlreadyCancelled -> state
```

The same rule applies in C#: `EventActions.Defer(...)` returns a non-journaled reply, and
`ApplyEvent` must leave state unchanged for that reply.

## Snapshots shorten recovery

Without a snapshot, activating an aggregate replays every stored event from version 1. That produces
the correct state, but a long history can make activation slow.

A snapshot stores the already-folded state and the event version it represents:

```text
full recovery:      replay events 1 through 927
snapshot recovery:  load snapshot at 900, replay events 901 through 927
```

Both paths must produce identical state. A snapshot does not replace journal events, change business
rules, or repair a non-deterministic fold. It changes recovery cost, not truth.

FCQRS provides three cadence policies on aggregate and saga definitions:

- `Default` uses the configured interval, falling back to every 30 persisted versions;
- `Every n` creates a checkpoint every `n` persisted versions;
- `NoSnapshots` stops creating new snapshots, although recovery can still use an older saved snapshot.

`PersistAndSnapshot` persists one event and requests an immediate checkpoint after it becomes durable.
Use it for a meaningful manual checkpoint, not as a substitute for choosing a measured cadence.

Snapshot more frequently when measured replay time makes activation too slow. Snapshot less often when
histories are short or snapshot storage and serialization cost outweigh the replay saving. Test
recovery both with and without a recent snapshot because later events are always replayed through the
fold.

## Passivation releases memory, not identity

**Passivation** is the Akka.NET term for stopping a sharded entity actor when it does not need to stay
active. FCQRS aggregate actors may be passivated after the sharding system's configured idle period.
Completed sagas also passivate because their workflow is finished.

Passivation does not delete the aggregate, its journal, or its snapshots. Callers still address the
same aggregate id. When another command arrives, cluster sharding creates an actor for that identity,
and FCQRS recovers its state before deciding the command.

```text
active actor -> idle -> passivated -> next command -> recover -> active actor
                            journal and snapshots remain
```

This is why application code must not treat actor memory as permanent storage. Process restarts, node
movement, and passivation all exercise the same recovery rules.

## See the three mechanisms on one timeline

Consider this order history:

```text
v1  OrderPlaced       persisted
v2  OrderPaid         persisted, snapshot saved at v2
v3  OrderShipped      persisted
    CancelOrder       returns deferred OrderAlreadyShipped, no v4
    actor becomes idle and passivates
    another command arrives
    load snapshot v2, replay v3, recover Shipped
```

The deferred reply does not reappear during recovery, which is safe because its fold left the order
unchanged. The snapshot avoids replaying v1 and v2. Passivation removes only the working state that can
be reconstructed from the snapshot and journal.

## What survives?

| Thing | Restart or passivation | Reaches journal projections | Changes persisted version |
|---|---:|---:|---:|
| Persisted event | yes | yes | yes |
| Deferred reply | no | no | no |
| Snapshot | yes | no | no |
| Active actor state alone | no | no | no |

If future decisions depend on a value, make that value recoverable from persisted events. If the value
only appears in a deferred fold or a mutable object held by the actor, it is not durable domain state.

## Put it into practice

Chapter 1 of the [tutorial](../tutorial/1-the-aggregate.html) introduces persisted and deferred actions.
Chapter 2 demonstrates recovery after running the application again. Use [Define an
aggregate](../how-to/define-an-aggregate.html) for the complete action table and
[Configuration](../configuration.html) for snapshot cadence and Akka.NET settings. [Consistency and
recovery](consistency-and-recovery.html) places aggregate recovery beside projections, sagas, and
external services.
