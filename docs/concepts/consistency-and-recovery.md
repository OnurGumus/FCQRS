---
title: Consistency and recovery
category: Concepts
categoryindex: 4
index: 6
---

# Consistency and recovery

A command can be stored before its projection updates the read model. A process can also stop while an
aggregate or saga is active. FCQRS uses correlation ids, versions, event replay, and snapshots to
handle these two parts of the system.

## The correlation id

A **correlation id (CID)** is created when a request begins. FCQRS copies it from the command to its
events, then to any saga commands and their events. Logs and traces can use the id to group the work
started by one request. A client can use the same id to wait for a projection.

## Read-your-writes over an eventually-consistent read side

The write side and the projection run as separate steps. After the write side stores an event, a query
can still see the old read model until the projection commits its update.

Subscribe to the command's correlation id before sending the command. When the projection commits the
update, it publishes the matching event to the subscription stream. The client can then query the read
model. Subscribing after the command creates a race because the notification may already have passed.

This signal applies to the projection that publishes it. If several projections must be current, wait
for a suitable notification from each one.

<img src="../img/cid-subscribe.svg" alt="Read-your-writes: subscribe to the CID before sending" width="900"/>

## Versions

Every persisted aggregate event increments the aggregate's **version**, and the event carries that
version. A deferred rejection is published and folded without being stored and does not increment the
persisted version. Its fold should preserve state because recovery cannot replay the deferred event.
FCQRS uses versions to order stored changes and to detect a restarted aggregate during saga
coordination.

## Snapshots

Replaying a long event history takes time. Every *N* events FCQRS saves a **snapshot** containing the
current state and version. Recovery loads the latest snapshot and replays the events stored after it.
Without a snapshot, recovery replays from the first event. Snapshots change recovery time, not the
resulting state. *N* defaults to 30 and is configurable in the
[configuration reference](../configuration.html). Aggregates and sagas use the same mechanism.

## Restart detection

During a saga conversation, the saga may hold an event produced before the aggregate restarted. FCQRS
compares the event version with the aggregate's current version. A mismatch stops the saga from acting
on that stale exchange.

## What FCQRS does not save you from

Actors process one message at a time, and Akka.NET preserves message order between a given sender and
receiver. These guarantees do not cover every part of a distributed workflow.

A saga needs a timeout when an expected event may never arrive. An external HTTP or e-mail operation
needs its own timeout and usually idempotency, because the remote operation and the saga's stored state
cannot share one transaction. Compensation is a domain decision and is not available for every
action. Circular saga dependencies can wait on each other, and bounded mailboxes can overflow under
load. The application must design for these cases.
