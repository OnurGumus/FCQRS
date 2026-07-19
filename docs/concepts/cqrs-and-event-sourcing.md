---
title: CQRS and event sourcing
category: Concepts
categoryindex: 4
index: 2
---

# CQRS and event sourcing, from the beginning

Start with a familiar application. An `Orders` table stores one row per order. An endpoint loads a
row, checks a rule, changes `Status`, and saves it. Another endpoint loads the same row with customer,
product, payment, and delivery joins to render a page.

That design is often the right place to begin. It becomes difficult when the model used to make
decisions and the model used to answer questions are pulled in different directions.

> **Motivation:** CQRS is a response to incompatible change pressures, not a goal by itself. Separate
> the models when keeping them together makes either business decisions or queries harder to express.

## One model is doing two jobs

Consider cancelling an order.

The **decision** needs a small, precise set of facts: whether the order exists, whether it has shipped,
and whether it was cancelled already. It must protect the rule that a shipped order cannot be
cancelled, even if two requests arrive at the same time.

The **order page** needs a different shape: customer name, product descriptions, totals, address,
payment status, delivery estimate, and a timeline. A warehouse list and a revenue report need still
other shapes.

Adding every query field and relationship to the decision model makes the rules depend on a large
object graph. Shaping every query around the decision model produces joins and transformations on
every read. The two jobs change for different reasons.

## CQRS separates those responsibilities

**CQRS** means Command Query Responsibility Segregation.

- The **write model** receives commands, protects business rules, and records outcomes.
- The **read side** answers queries from data shaped for those queries.

This does not mean every application needs two services or two databases. The separation is about
models and responsibilities. A small system can run both sides in one process and one database while
keeping the code and data flows distinct.

| Write side | Read side |
|---|---|
| Accepts commands | Accepts queries |
| Shaped around invariants | Shaped around screens and consumers |
| One consistency boundary per decision | Joins and duplicates data freely |
| Produces events | Consumes events |
| Rejects invalid changes | Returns available information |

## Commands and events describe different moments

A **command** is a request: `CancelOrder`. It is named in the imperative because the outcome is not
known yet. The system can reject it.

An **event** is a recorded outcome: `OrderCancelled` or a non-persisted reply such as
`OrderAlreadyShipped`. It is named in the past tense because the decision has been made.

Keeping the types separate prevents a request from being mistaken for a fact. It also makes the domain
decision visible:

```text
CancelOrder + current order state
    -> persist OrderCancelled
    -> or reply OrderAlreadyShipped
```

FCQRS puts the decision inside an [aggregate](aggregates.html). The aggregate does not return a mutated
database row. It returns an action describing the event outcome.

## Event sourcing records change instead of overwriting it

CQRS and event sourcing are separate ideas. You can use different command and query models while
storing only current state. You can also event-source a model without creating several read models.
FCQRS deliberately combines them.

With ordinary state storage, an update replaces `Status = Paid` with `Status = Shipped`. The database
retains the new value but not necessarily the reason or sequence that produced it.

With **event sourcing**, the journal appends facts:

```text
1  OrderPlaced
2  PaymentReceived
3  OrderShipped
```

Current state is derived by folding those events in order:

```text
empty
  |> apply OrderPlaced       = awaiting payment
  |> apply PaymentReceived   = paid
  |> apply OrderShipped      = shipped
```

The fold is an ordinary deterministic function. Given the same initial state and event sequence, it
must always produce the same result. Recovery relies on that property.

## Follow one request through the system

An FCQRS request takes this path:

1. The client creates a correlation id and sends `CancelOrder` to the order aggregate's identity.
2. The aggregate loads its current state, recovering from stored events if necessary.
3. Its decision function checks the command against that state.
4. If allowed, FCQRS appends `OrderCancelled` to the journal.
5. The aggregate folds the event into its in-memory state and publishes it.
6. One or more projections consume the event and update read models.
7. The client queries the read model, optionally after waiting for the required projection.

<img src="../img/architecture.svg" alt="The complete command, journal, projection, saga, and query flow" width="900"/>

The write side never exposes its internal state as the query contract. Both sides agree on the facts,
not on one shared object graph.

## What the history makes possible

The journal supports more than auditing.

- An inactive aggregate can recover its current state by replaying its events.
- A corrected projection can rebuild a read model from the original facts.
- A new read model can answer a question that did not exist when the events were recorded.
- Tests can describe a decision as “given these past events, when this command arrives, expect this
  outcome.”

History also creates responsibilities. Persisted event shapes become long-lived contracts. Storage
grows with the number of changes. Rebuilds and migrations must be rehearsed. Sensitive data cannot be
treated as casually deletable once it is copied into an append-only history.

## What this architecture does not imply

CQRS does not require microservices, a message broker, or one database per model. Event sourcing does
not make every operation globally consistent. FCQRS serializes decisions inside one aggregate, while
projections and cross-aggregate workflows remain asynchronous.

Use this model when history, concurrency rules, recovery, or multiple query shapes justify the extra
moving parts. For behaviour that is only create, read, update, and delete, a conventional application
is usually clearer.

## Check your understanding

For one feature in your domain, write down the command, the minimum state needed to decide it, the
persisted event, and two different queries derived from that event. If the decision state and query
shapes are identical and the history has no value, CQRS and event sourcing may not earn their cost.

Next, learn how [aggregates](aggregates.html) protect one decision boundary. Chapter 1 of the
[tutorial](../tutorial/1-the-aggregate.html) implements the command, event, decision, and fold.
