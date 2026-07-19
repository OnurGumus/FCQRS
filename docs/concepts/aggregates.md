---
title: Aggregates and the write side
category: Concepts
categoryindex: 4
index: 3
---

# Aggregates and the write side

An **aggregate** owns the rules and state needed to make decisions about one entity. In FCQRS, each
aggregate is an Akka.NET actor. Commands for order 123 go to the actor for order 123 and are handled one
at a time. Commands for other orders can run at the same time on other actors.

Sequential command processing eliminates race conditions within the aggregate. Rules involving
several aggregates still require coordination, which is the job of a [saga](sagas.html).

## Command in, events out

Suppose the aggregate receives `CancelOrder`. It checks the current state and returns an event such as
`OrderCancelled`, or a rejection such as `OrderAlreadyShipped`. The command expresses what the caller
wants. The event records the outcome.

You express this as a pure function, `handleCommand : Command -> State -> EventAction`. Three common
actions are:

- **Persist:** the event is appended to the journal, the aggregate's version
  increments, the event is applied to produce the new state, and the event is published to the read
  side and any sagas. Use it when the aggregate's state changes.
- **Defer:** publish and fold an event *without storing it* or incrementing the persisted version. This
  fits a rejection such as `OrderAlreadyShipped`, whose fold returns the existing order unchanged. A
  state change made only by a deferred event disappears on recovery because the journal cannot replay
  that event.
- **Ignore:** produce no event, state change, or reply.

<img src="../img/write-path.svg" alt="The path a command takes through an aggregate" width="900"/>

## State is folded, never stored

The aggregate state is held in memory and rebuilt by a second pure function,
`applyEvent : Event -> State -> State`. FCQRS calls this function after storing a new event and while
replaying old events after recovery. The same event sequence must produce the same state in both cases,
so `applyEvent` should contain no I/O or other side effects. [Consistency and
recovery](consistency-and-recovery.html) covers replay and snapshots.

## Keep recovery free of side effects

Keep e-mail, HTTP calls, and writes to other databases outside the aggregate's decision and fold
functions. Replaying an event must rebuild state without repeating an external action. Work that must
continue across aggregate or service boundaries belongs in a [saga](sagas.html). FCQRS also supports
ephemeral asynchronous effects for work that may safely be lost on restart.

## Actor lifecycle and location

Akka.NET cluster **sharding** routes an aggregate id to its current actor, wherever that actor is
running. **Passivation** stops an idle actor and recovers it from stored events when the next command
arrives. Application code addresses the aggregate by id rather than by server location.

The decision and fold functions have no dependency on Akka.NET. The
[tutorial](../tutorial/index.html) builds an aggregate from scratch, and
[Define an aggregate](../how-to/define-an-aggregate.html) provides the shorter recipe.
