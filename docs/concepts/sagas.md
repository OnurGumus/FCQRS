---
title: Sagas
category: Concepts
categoryindex: 4
index: 5
---

# Sagas

An order actor can decide that an order was placed, but it does not own stock or payments. Reserving
stock and taking payment require commands to other actors or services. A **saga** stores and coordinates
that sequence. An aggregate turns commands into events; a saga reacts to events by issuing commands.

## A small, durable state machine

<img src="../img/saga-lifecycle.svg" alt="A saga as a state machine: events in, commands out" width="760"/>

A saga starts from an event, moves through a sequence of states, and issues commands along the way. An
order saga might send `ReserveStock`, wait for `StockReserved`, then send `TakePayment`. Its state is
persistent, so FCQRS can recover the saga after a process restart.

The saga records progress before moving to the next step. Commands can be delayed to implement retry
backoff. External operations still need idempotency because a process can fail after the external
service accepts a request but before the application records the result.

A saga definition provides functions that handle incoming events, choose commands and transitions,
and carry data between steps. The [tutorial](../tutorial/index.html) builds one, and
[Write a saga](../how-to/write-a-saga.html) lists the API.

## Starting a saga safely

The event that starts a saga must not be published before the saga subscribes to it. FCQRS uses an
internal **saga starter** to enforce that order. The aggregate sends the event to the starter, the
starter creates and subscribes the saga, and the aggregate then persists and publishes the event. The
application declares which event starts the saga; the framework performs the handshake.

<img src="../img/saga-starter.svg" alt="The handshake that starts a saga safely" width="940"/>

## Recovery and repeated commands

After replay, FCQRS calls the side-effect function with `recovering = true` to re-drive the saga's
current state. The process may have stopped before or after the previous command was delivered, so the
target must handle a repeated command safely. The flag allows recovery-specific behaviour, such as
querying an external operation by idempotency key before retrying it. Returning no command for every
recovered state can leave a saga waiting forever.

Version checks also detect when an aggregate has restarted during a saga conversation. External
handlers still need idempotency because their operation and the saga journal cannot share one
transaction.

## Where sagas fit

Use a saga when an event starts work across aggregates or external services and that progress must
survive a restart. Keep the dependency graph one-directional and add timeouts for events that may never
arrive. [Consistency and recovery](consistency-and-recovery.html) describes the remaining failure
modes.
