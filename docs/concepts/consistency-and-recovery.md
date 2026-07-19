---
title: Consistency and recovery
category: Concepts
categoryindex: 4
index: 6
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

A **correlation id**, or CID, identifies the work caused by one request. FCQRS copies it from the
command to its event, through saga commands and their events, and into projection notifications.

The CID serves two distinct purposes:

- logs and traces can group activity belonging to one request;
- a caller can wait for the projection result caused by that request.

It is not an aggregate id. The aggregate id answers “which domain owner?” The correlation id answers
“which request flow?” One request can involve several aggregate identities while retaining one CID.

## Read-your-writes is explicit coordination

After sending a command, immediately querying a read model may return old data. Sleeping for an
estimated delay makes the race intermittent rather than correct.

FCQRS uses this sequence:

1. create a CID;
2. subscribe to the CID before sending;
3. send the command;
4. let the aggregate persist its event;
5. let the required projection commit its data and offset;
6. receive that projection's notification;
7. query its read model.

<img src="../img/cid-subscribe.svg" alt="A caller subscribes to a correlation id before sending, then queries after the projection commits" width="900"/>

Subscribing first matters because notifications are ephemeral. A notification can pass before a late
subscriber exists. The journal and projection offset are durable; the notification is only a
request-scoped coordination signal.

If the next response combines several read models, decide which must be current and wait for the
appropriate signal from each. One projection's notification proves nothing about another projection's
offset.

## Version, offset, and correlation id are not interchangeable

| Value | Scope | What it tells you |
|---|---|---|
| Aggregate version | one aggregate identity | how many persisted events that identity has applied |
| Projection offset | one projection stream position | how far that projection has committed |
| Correlation id | one request flow | which commands, events, and notifications belong together |

Every persisted aggregate event receives the next version. A deferred reply is not stored and does not
increment the persisted version. Its live fold must preserve recoverable state because replay cannot
reproduce it.

Projection offsets advance independently. Event version 8 for one order might appear at offset 52,413
in the global stream.

## Recovery rebuilds state from durable evidence

When an aggregate actor starts, FCQRS loads the latest snapshot if one exists and replays subsequent
events through the fold. Without a snapshot, it replays from the first event.

The journal is the durable evidence. In-memory actor state is reconstructed. That is why the fold must
be deterministic and free of side effects. Recovery may happen after a crash, after passivation, or
when sharding activates the identity on another node.

A projection recovers differently. It loads its committed offset and resumes the event stream after
that point. Its read-model data and offset must share a transaction or another explicit reliability
mechanism.

A saga recovers its stored state and then re-drives the current step. Because delivery to another
participant is outside the saga journal transaction, repeated commands must be safe.

## Snapshots change cost, not truth

Replaying thousands of events for one identity can increase activation latency. A **snapshot** stores
the folded state and version at a checkpoint.

Recovery then becomes:

```text
load snapshot at version 900
replay events 901 through 927
```

The resulting state must match replaying versions 1 through 927. A snapshot does not replace journal
history, change business semantics, or make a non-deterministic fold safe.

FCQRS supports default, disabled, and every-N snapshot policies for aggregates and sagas. The default
interval comes from configuration. Choose a cadence from measured recovery latency and storage cost,
not from an assumption that more snapshots always improve the system.

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
[Rebuild a read model](../how-to/rebuild-a-read-model.html), and
[Evolve persisted events](../how-to/evolve-events.html) for the corresponding procedures.
