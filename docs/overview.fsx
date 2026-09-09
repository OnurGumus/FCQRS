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

FCQRS runs event-sourced applications on Akka.NET, with F# and C# APIs. Your code decides which events
to save and how they change state. FCQRS stores those events and rebuilds state after a restart.

**[Register a user](get-started.html)** shows the code and runs it with SQLite on .NET 10.

## Follow one registration

1. `RegisterUser("Alice")` asks one account to register a name. This request is a **command**.
2. The account's rule checks its current state and returns `UserRegistered("Alice")`, an **event**.
3. FCQRS stores that event in the **journal**, the account's event history, and applies it to state.
4. A **projection** reads the saved event and fills a query view. The sample queries that view for Alice's name.

The account's state and rules form an **aggregate**. Its commands run one at a time; different accounts
can run independently. A query view updates asynchronously, so the sample waits for that view before
reading it.

<img src="img/architecture.svg" alt="A command enters an aggregate; stored events rebuild its state and feed projections and sagas; queries read a projection's view." width="900"/>

## Continue with a task

- [Try another registration](tutorial/1-the-aggregate.html): change the name and account ID.
- [Query a registered user](tutorial/2-running-it.html): see the projection and runtime setup.
- [Test your domain](how-to/test-your-domain.html): check registration and replay without a database.
- [Register over HTTP](tutorial/http-api.html), optional: add POST and GET endpoints.
- [Task guides](how-to/index.html): add durable query storage, workflows, or operational configuration.
- [Concepts](concepts/index.html): understand the guarantees and their boundaries.

## When to use FCQRS

FCQRS is useful when several callers can change the same entity, decisions depend on its history,
or workflows must recover after a restart. It adds an event journal, asynchronous query views, and an
actor runtime to operate. For an application that only edits and reads rows, a conventional database
application may need less infrastructure.
*)
