---
title: Sagas
category: Concepts
categoryindex: 4
index: 5
---

# Sagas: durable coordination

An aggregate can make a correct decision only from state it owns. An order aggregate may decide that
an order is ready to place, but it does not own warehouse stock, a payment account, or an e-mail
provider.

Combining every participant into one aggregate would create one enormous consistency boundary.
Calling the participants directly from the order's fold would repeat external work during recovery.
A **saga** coordinates the sequence while allowing each participant to keep its own state and rules.

## Start with a workflow that can stop halfway

Consider placing an order:

1. reserve stock;
2. take payment;
3. confirm the order;
4. release stock if payment fails.

The process may stop after the stock service accepts the reservation but before the application
records the response. Payment may time out. A node may restart while the saga is waiting. “Call these
three functions in order” is not enough because the progress itself must survive failure.

A saga turns that progress into persisted state.

## A saga is a state machine

<img src="../img/saga-lifecycle.svg" alt="A saga receives events, persists state transitions, and emits commands" width="760"/>

The order workflow might use these states:

```text
WaitingForStock
WaitingForPayment
Completed
Compensating
Failed
```

Events move the saga between states. Commands ask other owners to do the next piece of work.

```text
OrderPlaced       -> WaitingForStock   + send ReserveStock
StockReserved     -> WaitingForPayment + send TakePayment
PaymentAccepted   -> Completed         + send ConfirmOrder
PaymentRejected   -> Compensating      + send ReleaseStock
```

This makes the workflow inspectable. The saga can recover its last stored state and determine what it
was waiting for instead of restarting the whole procedure from memory.

## Aggregates and sagas have different authority

An aggregate owns a business invariant and decides a command from its own state. A saga owns only the
workflow's progress. It does not reach into aggregate state or make another aggregate's decision.

| Aggregate | Saga |
|---|---|
| Receives commands | Reacts to events |
| Protects rules inside one identity | Coordinates independent identities |
| Emits outcome events | Emits follow-up commands and stores transitions |
| Owns domain decision state | Owns process progress |

If a saga needs to ask whether stock is available, it sends a command to the stock aggregate. The stock
aggregate applies the rule and publishes its outcome. This keeps one owner for each decision.

## Starting is itself a race

The first event has to create the saga and also be delivered to it. Publishing the event before the
saga subscribes can lose the only message that starts the workflow.

FCQRS uses a saga starter handshake:

1. the application declares which event starts the saga;
2. the aggregate sends that event to the saga starter;
3. the starter creates and subscribes the saga instance;
4. the aggregate persists and publishes the event only after the subscription is ready.

<img src="../img/saga-starter.svg" alt="The saga starter creates and subscribes a saga before the aggregate publishes its starting event" width="940"/>

The handshake closes the startup race inside FCQRS. It does not make later external calls atomic with
the saga journal.

## Delivery and persistence cannot share one transaction

Suppose the saga stores `WaitingForPayment` and sends `TakePayment`. The process can fail at several
points:

- before the command is delivered;
- after delivery but before the payment service acts;
- after payment succeeds but before the response reaches the saga;
- after the saga receives the response but before its next transition is stored.

No local transaction can cover the saga journal and an independent actor or remote service. The
correct design assumes a command may be repeated.

Give operations stable idempotency keys, usually derived from the workflow and step identity. The
receiver should return the same logical outcome when it sees the same operation again instead of
charging twice or creating a second reservation.

## Recovery re-drives the current state

After replay, FCQRS calls the saga side-effect function with `recovering = true`. The saga knows its
stored state but cannot know whether the last outgoing command crossed the process boundary before the
failure.

Recovery should therefore make safe progress. Depending on the integration, it can resend an
idempotent command, query an external operation by idempotency key, or schedule a retry. Returning no
command for every recovered state can leave a saga waiting forever.

FCQRS version checks can detect an aggregate restart during a saga exchange. That protection does not
replace idempotency at external boundaries.

## Timeouts are domain events, not only infrastructure settings

Every wait needs an answer to “what happens if the event never arrives?” A timeout may retry, move the
saga to failure, start compensation, or alert an operator. The choice depends on the business meaning.

Delayed saga commands support backoff, but retry policy needs a limit and visibility. An endless retry
can be less safe than a failed state that an operator can inspect.

## Compensation is a new action

In a database transaction, rollback makes intermediate writes disappear. A distributed workflow
cannot generally erase completed actions. Releasing stock is not a rollback of reserving stock; it is
a new business action with its own possible failure. A sent e-mail may have no meaningful
compensation.

Model compensation explicitly:

- which completed steps require it;
- in which order compensations run;
- whether compensation is idempotent;
- what happens when compensation fails.

Some workflows should stop for human resolution rather than pretend every action is reversible.

## Keep the dependency graph understandable

Prefer a one-directional flow in which the saga coordinates participants. Circular workflows can
create hidden waits: saga A waits for an event produced only after saga B completes, while saga B waits
for saga A.

Name states after what the workflow is waiting for, store the identifiers needed to continue, and log
every transition with the correlation id. A useful saga can answer: where am I, what event moves me
forward, what command did I issue, and what is the timeout behaviour?

## Decide whether you need a saga

Use a saga when work crosses independent consistency boundaries and its progress must survive restart.
Use an ordinary aggregate command when one owner can decide the rule. Use an async effect for
best-effort work that may safely be lost and does not need durable progress.

Chapter 3 of the [tutorial](../tutorial/3-adding-a-saga.html) builds a quota workflow from two
aggregates. Use [Write a saga](../how-to/write-a-saga.html) for the API recipe and
[Dispatch async effects](../how-to/dispatch-async-effects.html) to compare the non-durable alternative.
