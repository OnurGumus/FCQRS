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
- a projection later handles the event and records its progress;
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
  -> read model and progress committed
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

## Version, projection progress, and correlation id are not interchangeable

These three values recur at every recovery boundary, so it pays to restate them together (they are
introduced individually with [the read side](read-models.html) and
[correlation ids](correlation-ids.html)):

| Value | Scope | What it tells you |
|---|---|---|
| Aggregate version | one aggregate identity | how many persisted events that identity has applied |
| Projection progress | one projection, per aggregate | the last version of each aggregate that projection has handled |
| Correlation id | one request flow | which commands, events, and notifications belong together |

Every persisted aggregate event receives the next version. A deferred reply is not stored and does not
increment the persisted version. Its live fold must preserve recoverable state because replay cannot
reproduce it. [Deferring, snapshots, and passivation](aggregate-lifecycle.html) follows those paths in
detail.

Each projection keeps its own progress. One projection may have handled version 8 of Alice's account
while another is still at version 5.

## Each component recovers from different evidence

An aggregate loads its latest snapshot if one exists and replays subsequent journal events through the
fold. Without a snapshot, it replays from the first event.

The journal is the durable evidence. In-memory actor state is reconstructed. That is why the fold must
be deterministic and free of side effects. Recovery may happen after a crash, after passivation, or
when sharding activates the identity on another node. Snapshots change how much history is replayed,
not the state that recovery must produce.

An [event upcaster](../how-to/evolve-events.html) can adapt historical journal payloads before replay.
It does not recompute state already loaded from a snapshot. A change to how old events affect state
therefore needs compatible snapshot state or a tested plan to recover the complete history.

A projection recovers differently. It loads the last version it handled for each aggregate and
continues after it, or reads the journal from the start when it keeps its progress in memory. Its
read-model data and stored progress must share a transaction, or its handler must tolerate an event it
has already handled.

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

Some failures leave no caller to report to, such as an exception in `fold` or a stored event that no
longer reads. FCQRS stops the process for those, so recovery starts again from the journal.
[When FCQRS stops the process](process-termination.html) lists every case.

## Reason from the last durable boundary

For every step, ask: what is the last fact known to be durable, and what could have happened after it?
That question leads to the correct recovery action more reliably than assuming a process stopped
between two convenient source-code lines.

The [tutorial's first step](../tutorial/open-an-account.html#Run-it-again) shows recovery after a restart.
[Write a saga](../how-to/write-a-saga.html) covers workflow retries. Use
[Read your writes](../how-to/read-your-writes.html),
[Define an aggregate](../how-to/define-an-aggregate.html),
[Rebuild a read model](../how-to/rebuild-a-read-model.html), and
[Evolve persisted events](../how-to/evolve-events.html) for the corresponding procedures.
