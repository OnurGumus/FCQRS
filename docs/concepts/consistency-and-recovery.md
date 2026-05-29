---
title: Consistency and recovery
category: Concepts
categoryindex: 4
index: 6
---

# Consistency and recovery

Two questions hang over any event-sourced system: *how does a client know when its change is
visible?* and *what happens when a process restarts mid-flight?* FCQRS answers both with mechanisms
you mostly do not have to think about, but should understand.

## The correlation id

A **correlation id (CID)** is minted when a request begins and copied onto the command, onto every
event the command produces, onto a saga's follow-up commands, and onto their events in turn. Follow
one CID and you can watch a single user action ripple through the entire system. In distributed-tracing
setups the same id doubles as the trace id, so the whole round trip shows up as one correlated story
in your telemetry.

## Read-your-writes over an eventually-consistent read side

There is a real gap in CQRS: the write side persists an event, and a moment later the projection folds
it into the read model. Those are two steps, and in between, the read model is behind. A client that
sends a command and immediately queries can read stale data.

FCQRS does not pretend the gap away; it gives you a precise signal for when it closes. The rule is one
sentence: **subscribe to your correlation id before sending the command, then wait.** When the
projection republishes an event carrying that CID (see [the read side](read-models.html)), the
subscription fires — and only then do you read. Sending first and subscribing afterwards is the bug:
the event could be processed in the instant before your subscription exists. Subscribe first, and the
confirmation cannot be missed. The result is read-your-writes with no polling and no arbitrary
`sleep` — just a clean rendezvous on an id.

<img src="../img/cid-subscribe.svg" alt="Read-your-writes: subscribe to the CID before sending" width="900"/>

## Versions

Every time an aggregate persists an event, its **version** increments by one, and the version travels
on the event. Deferred events (rejections) do not bump it — the version counts *what happened to the
entity*, not how many commands it declined. Beyond bookkeeping, the version is load-bearing for
consistency across restarts, as below.

## Snapshots

Rebuilding state by replaying every event is correct but, for a long-lived entity, slow. Every *N*
events FCQRS saves a **snapshot** — the full current state, tagged with its version. On recovery it
loads the most recent snapshot and replays only the events after it, arriving at exactly the same
state a full replay would. A snapshot is therefore pure optimization: delete them all and recovery
falls back to replaying from the first event with an identical result. *N* defaults to 30 and is
configurable (see the [configuration reference](../configuration.html)); both aggregates and sagas
use the same mechanism.

## Restart detection

When a saga and an aggregate are mid-conversation and the aggregate is restarted underneath them, the
saga might still hold an event from the aggregate's previous life. FCQRS catches this by comparing
versions: if the version the saga carries no longer matches the aggregate's current version, the
framework concludes a restart happened and aborts that saga rather than letting it act on stale
information. You write none of this; it is part of the saga handshake.

## What FCQRS does not save you from

Honesty matters here. The actor model gives strong guarantees — sequential processing, FIFO between a
pair of actors, no in-aggregate races — but it does not abolish distributed-systems hazards. A saga
waiting on an event that never comes will wait forever unless you add a timeout. External calls
(HTTP, e-mail) live outside Akka's guarantees and need their own timeouts and circuit breakers.
Circular dependencies between sagas can deadlock. Mailboxes can overflow under load. The framework
removes a large class of failures and makes recovery automatic for the ones it owns; the rest are
design responsibilities. Build timeouts into every external wait, design for idempotency, give every
action a compensating undo, and avoid circular orchestration, and these systems are extremely robust.
