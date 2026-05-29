---
title: Aggregates and the write side
category: Concepts
categoryindex: 4
index: 3
---

# Aggregates and the write side

An **aggregate** is the write side's unit of consistency. In FCQRS it is an Akka.NET actor: there is
exactly one live instance per entity — one `User`, one `Order` — and it processes its messages one at
a time. That single fact removes a whole category of bugs. Because only one command is ever in flight
inside a given aggregate, there are no locks to take and no races to lose, yet different entities run
fully in parallel across the cluster.

## Command in, events out

The shape of an aggregate is always the same. A **command** arrives — a *request*, in the imperative:
`Register`, `Withdraw`, `CreateOrUpdate`. The aggregate looks at the command and at its own current
state, makes a decision, and emits zero or more **events** — *facts*, in the past tense:
`Registered`, `Withdrawn`, `InsufficientFunds`. The command is a wish; the event is what actually
happened. They are deliberately not the same set: a single `Register` command might produce either a
`Registered` fact or an `AlreadyRegistered` one.

You express this as a pure function, `handleCommand : Command -> State -> EventAction`. It returns an
**action**, and three cover almost everything you write:

- **Persist** — the normal, happy path. The event is appended to the journal, the aggregate's version
  increments, the event is applied to produce the new state, and the event is published to the read
  side and any sagas. Return this when something genuinely happened.
- **Defer** — emit an event *without storing it*. The state and version do not move, nothing is
  written to the journal, but the event is still published so a waiting caller gets an answer. This
  is exactly right for rejections: `AlreadyRegistered` is something the caller needs to hear, but it
  is not a fact that changed the entity, so it should not pollute the history.
- **Ignore** — do nothing at all: no event, no state change, no reply.

<img src="../img/write-path.svg" alt="The path a command takes through an aggregate" width="900"/>

## State is folded, never stored

The aggregate's state is *not* the source of truth and is *not* what gets persisted. It is a
convenience held in memory and rebuilt from events by a second pure function,
`applyEvent : Event -> State -> State`. That function is deliberately dull — it only folds an event
into the state — because it runs in two situations that must produce identical results: once when a
new event is persisted, and many times when old events are replayed to reconstruct state after a
restart. Keeping the fold pure is what makes replay trustworthy. ([Recovery](consistency-and-recovery.html)
covers replay and snapshots in detail.)

## Why an aggregate may not do anything else

The discipline that makes all this safe is what the aggregate is *forbidden* to do: it does not send
e-mail, call an HTTP API, or write to a database. It only decides and emits events. Two reasons. A
pure decision can be retried and replayed safely, because running it again produces the same events
and no duplicate side effects — there were none. And because events are the only output, replaying
them rebuilds the state exactly. Side effects belong to [sagas](sagas.html), which is precisely why
they exist.

## Why an actor, underneath

Riding on Akka.NET gives the design its properties almost for free. One-message-at-a-time processing
is a clean transaction boundary with no locking. Cluster **sharding** lets the same logical aggregate
live on any node and move between them, so the system scales horizontally without you addressing
machines by hand. **Passivation** puts idle entities to sleep and recovers them on demand, so a
million entities cost only as much memory as the active ones. FCQRS hides that machinery behind the
two functions you write.

Both functions are ordinary, pure F# (or C#) with no dependency on Akka.NET, so they are trivially
unit-testable in isolation. The [tutorial](../tutorial/index.html) builds one from scratch; the
[how-to guide](../how-to/define-an-aggregate.html) is the quick recipe.
