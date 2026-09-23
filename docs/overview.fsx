(**
---
title: Overview
category: Overview
categoryindex: 1
index: 1
---
*)

(**
# Why FCQRS

## One model for every job

Many .NET applications map their tables to one object graph, often with Entity Framework. A customer has
addresses, contacts, orders, and a cart. An order has items, an invoice, payments, and shipments. An item
points to a product and a discount. Every change and every screen goes through that one graph.

<a href="img/two-models.png"><img src="img/two-models.png" alt="Left: a traditional Entity Framework graph in which customers, orders, items, invoices, payments, carts, products, and discounts all reference each other. Right: the CQRS split, with separate Customer and Order aggregates on the command side and flat read models such as OrderSummaryDto and CustomerOrdersDto on the query side." width="1400"/></a>

The left side of the picture shows where this leads as the application grows:

- **Changes reach too far.** Cancelling an order loads part of the graph and saves whatever changed. The
  rule that a shipped order cannot be cancelled lives in one of those classes, and two requests can change
  the same order at the same time unless you add concurrency checks.
- **Screens pull in different directions.** The order list, the order page, and the customer page each
  need a different shape. They all join through the same graph, so changing it for one screen affects
  the others.

## Two models

CQRS, Command Query Responsibility Segregation, gives each job its own model. The right side of the
picture shows the split:

- The **command side** is a set of small **aggregates**. Each one owns its data and the rules that protect
  it, refers to other aggregates only by ID, and changes in a transaction of its own.
- The **query side** is a set of **read models**, each shaped for one screen or report. They are flat,
  duplicate data where that helps, and contain no business rules.

## Events connect the two sides

When an aggregate accepts a command, it records what happened as an **event**, such as `OrderShipped`.
Read models are updated from those events.

FCQRS also keeps the events as the aggregate's stored data, which is called **event sourcing**. The
database holds every change in order, and an aggregate's current state is rebuilt from its events. A new
read model can be built from the history that already exists.

## What FCQRS does

You write the rules of each aggregate: which event a command produces, and how an event changes the
state. FCQRS runs one instance per aggregate ID and hands it one command at a time. It stores the events,
rebuilds the state after a restart, and updates your read models. It also runs workflows that span
several aggregates, called **sagas**. FCQRS runs on Akka.NET and has F# and C# APIs.

## Learn it by building a bank

The tutorial applies this split to a small bank. Each account is an aggregate, and a statement is a read
model. Every step is a program you run.

1. [Open an account](tutorial/open-an-account.html): commands, events, and the journal.
2. [Withdraw money](tutorial/withdraw-money.html): rules, rejections, and one command at a time.
3. [Restart the bank](tutorial/restart-the-bank.html): loading an account and snapshots.
4. [Show a statement](tutorial/show-a-statement.html): read models and projections.
5. [Transfer money](tutorial/transfer-money.html): sagas and commands that are safe to repeat.
6. [Add a memo](tutorial/add-a-memo.html): changing events the journal already holds.

## When to use FCQRS

FCQRS is useful when several callers can change the same data, decisions depend on history, or workflows
must recover after a restart. It adds an event journal, read models that update asynchronously, and an
actor runtime to operate. For an application that only edits and reads rows, a conventional database
application may need less infrastructure.
*)
