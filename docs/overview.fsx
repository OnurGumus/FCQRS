(**
---
title: Overview
category: Overview
categoryindex: 1
index: 1
---
*)

(**
# FCQRS

<figure style="margin: 1.5rem 0;">
  <img src="img/two-models.png" alt="On the left, one tangled Entity Framework object graph where every entity references every other; on the right, the same domain split into small command-side aggregates that each enforce their own invariants, plus flat query-side read models shaped for fast queries." style="width: 100%; height: auto; border: 1px solid var(--line, #d7e1ef); border-radius: 12px;"/>
  <figcaption style="margin-top: .6rem; font-size: .9rem; color: var(--muted, #4d5f7d); text-align: center;">The same domain represented by one shared model and by separate write and read models.</figcaption>
</figure>

## Why two models?

The information you need to make a decision is usually not the same information you want to show on a
screen.

Take an order cancellation. To decide whether an order can be cancelled, the system may only need to
know whether it has been paid, shipped, or cancelled already.

The order page needs something different. It may show the customer's name, the products they bought,
prices, delivery details, payment status, and a timeline of everything that happened.

These are two different jobs. One model helps the system make correct decisions. The other helps people
find and understand information.

You can force both jobs into one model. Many systems begin that way because it looks simpler. Over time,
every new screen and every new rule pulls that model in a different direction. Queries become tangled
with business rules, and changing one side risks breaking the other.

CQRS separates them.

The **write model** handles decisions and protects the rules of the system. The **read model** presents
the information people and applications need. Each can then stay simple in its own way.

## What FCQRS is

FCQRS is an F# framework for building the two models with Akka.NET actors and event sourcing. Its APIs
are also available from C#.

Every command for an order goes to one **actor** identified by that order's id. The actor handles one
command at a time. If two cancellation requests arrive together,
one is decided first and changes the order's state before the second is considered. The cancellation
rule therefore has one place to run and one current state to inspect. Other orders are handled by other
actors and can proceed at the same time, on the same server or elsewhere in the cluster.

The decision is an ordinary function. It receives the command and the current state, then returns an
event such as `OrderCancelled` or a rejection such as `OrderAlreadyShipped`. A second function folds a
stored event into the state. These functions contain the business rules but no database or actor code,
so they can be tested on their own.

Stored events form the order's history. If its actor has been passivated or the process restarts, FCQRS
replays those events to recover the current state. The state held by the actor is derived from the
events; it is not a row that the actor overwrites.

The same events feed **projections** on the read side. One projection can maintain the order page while
another maintains a customer history or a reporting table. A projection chooses the data structure
that fits its query. It does not have to copy the shape of the write model.

A **saga** coordinates work that crosses actor boundaries. For example, an `OrderPlaced` event can
start a process that sends commands to reserve stock and take payment. The saga stores its progress so
the process can continue after a restart.

Every command and event carries a **correlation id**. A client can subscribe to that id before sending
a command and wait until the projection publishes the matching event. The signal tells the client when
the new data is available to query, without polling or an arbitrary delay.

FCQRS supplies the actor lifecycle, cluster sharding, event storage and replay, saga coordination,
projection stream, and correlation subscriptions around the functions that define the domain.

<img src="img/architecture.svg" alt="How FCQRS fits together" width="900"/>

## What FCQRS handles

* **One command at a time per aggregate.** Decisions about the same entity do not run concurrently
  inside the aggregate, eliminating race conditions within that boundary.
* **An append-only event history.** Persisted changes can be replayed to recover state or build a new
  read model.
* **Independent read models.** Each projection can store data in the shape its queries need.
* **Read-your-writes coordination.** Correlation subscriptions provide a signal when a projection has
  handled a command's event.
* **Durable workflows.** Sagas record their progress while coordinating commands across aggregates.
* **Isolated domain tests.** Decision and fold functions can be tested without starting the actor
  system or a database.
* **Local and clustered operation.** The same aggregate code runs in a single-node or multi-node Akka.NET
  cluster.
* **F# and C# APIs.** Both languages can define aggregates, projections, sagas, and subscriptions.

## Find your path

This documentation is organized by what you are trying to do:

* [Get started](get-started.html): install the package and run one complete write-and-read loop.
* [Tutorial](tutorial/index.html): progress from one aggregate through read models, sagas, event
  evolution, failure testing, and production operation.
* [Concepts](concepts/index.html): understand CQRS, event sourcing, aggregates, projections,
  consistency, recovery, and sagas.
* [How-to guides](how-to/index.html): find focused instructions for a specific task.
* **Reference:** use the generated API documentation and the
  [configuration reference](configuration.html) while writing code.

## When FCQRS is a good fit, and when it isn't

FCQRS is useful when the system must protect business rules, preserve the order of changes, or keep a
history that may be queried again later. It also fits when several users or processes can issue
commands for the same entity, or when one action starts a workflow that must survive a restart.

The cost is an event journal, separate read models, asynchronous projections, and an actor system to
operate. That cost has little return for data with no behaviour beyond create, read, update, and delete.
A small internal directory or a table maintained by one user may be clearer as a conventional database
application.

The choice depends on the rules and failure modes in the application, not its current number of users.
The [concepts](concepts/index.html) section explains the guarantees and the responsibilities that remain
with the application.
*)
