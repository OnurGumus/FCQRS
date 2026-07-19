---
title: Consistency and recovery
category: Understand
categoryindex: 3
index: 8
---

# Consistency and recovery

“Is the system consistent?” is too broad a question. FCQRS has several boundaries that become durable
or visible at different times:

- an aggregate serializes decisions for one identity;
- the journal durably stores an accepted event;
- a projection later commits query data and its offset;
- a saga stores workflow progress between independent participants;
- an external service has its own durability and retry rules.

Understanding recovery means putting those boundaries on one timeline.

> **Motivation:** “The system is consistent” hides the question that matters during failure. Name the
> last boundary that became durable, then reason about everything that may have happened after it.

## One command has several observable moments

For a successful command, the sequence is approximately:

```text
command received
  -> decision made
  -> event appended to journal
  -> aggregate state folded in memory
  -> event published
  -> projection handles event
  -> read model and offset committed
  -> projection notification published
```

Once the journal append succeeds, the write is durable. The read model may still show the previous
value until its projection commits. This is eventual consistency between write and read sides, not a
race inside the aggregate.

## Correlation ids connect the flow

A correlation id follows the work caused by one request across commands, events, saga steps, logs, and
projection notifications. It also lets an active caller wait until the projection it will query has
committed that request's event.

The signal is explicit coordination across the eventual-consistency gap. It is not a durable queue or
a promise that every read model is current. [Correlation IDs and
read-your-writes](correlation-ids.html) develops the complete model, including why callers subscribe
before sending and why deferred replies must skip the wait.

## Version, offset, and correlation id are not interchangeable

| Value | Scope | What it tells you |
|---|---|---|
| Aggregate version | one aggregate identity | how many persisted events that identity has applied |
| Projection offset | one projection stream position | how far that projection has committed |
| Correlation id | one request flow | which commands, events, and notifications belong together |

Every persisted aggregate event receives the next version. A deferred reply is not stored and does not
increment the persisted version. Its live fold must preserve recoverable state because replay cannot
reproduce it. [Deferring, snapshots, and passivation](aggregate-lifecycle.html) follows those paths in
detail.

Projection offsets advance independently. Event version 8 for one order might appear at offset 52,413
in the global stream.

## Each component recovers from different evidence

An aggregate loads its latest snapshot if one exists and replays subsequent journal events through the
fold. Without a snapshot, it replays from the first event.

The journal is the durable evidence. In-memory actor state is reconstructed. That is why the fold must
be deterministic and free of side effects. Recovery may happen after a crash, after passivation, or
when sharding activates the identity on another node. Snapshots change how much history is replayed,
not the state that recovery must produce.

A projection recovers differently. It loads its committed offset and resumes the event stream after
that point. Its read-model data and offset must share a transaction or another explicit reliability
mechanism.

A saga recovers its stored state and then re-drives the current step. Because delivery to another
participant is outside the saga journal transaction, repeated commands must be safe.

## Restarts reveal uncertain delivery

Imagine a saga sends a command and the target aggregate processes it, but the saga node stops before
recording the response. On recovery, the saga cannot infer whether delivery happened. It must repeat or
inspect the operation safely.

FCQRS also compares versions during saga coordination to detect a stale exchange after an aggregate
restart. This catches one class of invalid continuation. It does not create an exactly-once network or
transaction across services.

## Failure boundaries to design explicitly

FCQRS provides useful local guarantees, but the application must decide what happens when:

- a projection repeatedly fails on one event;
- an expected saga event never arrives;
- a remote operation succeeds but its response is lost;
- a retry reaches a non-idempotent handler;
- a mailbox or notification buffer reaches capacity;
- old events no longer deserialize after deployment;
- a backup contains the journal but not a compatible snapshot or read-model schema;
- two deployed versions disagree about message contracts.

Timeouts, idempotency keys, dead-letter and failure monitoring, compatibility tests, backups, and
rehearsed rebuilds are application responsibilities around the framework.

## Reason from the last durable boundary

For every step, ask: what is the last fact known to be durable, and what could have happened after it?
That question leads to the correct recovery action more reliably than assuming a process stopped
between two convenient source-code lines.

Chapter 2 of the [tutorial](../tutorial/2-running-it.html) demonstrates aggregate and projection
recovery. Chapters 3 and 4 cover saga retry and compatibility. Use
[Read your writes](../how-to/read-your-writes.html),
[Define an aggregate](../how-to/define-an-aggregate.html),
[Rebuild a read model](../how-to/rebuild-a-read-model.html), and
[Evolve persisted events](../how-to/evolve-events.html) for the corresponding procedures.
