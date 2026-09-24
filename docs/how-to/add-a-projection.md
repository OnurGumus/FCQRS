---
title: Add a projection
category: Apply
categoryindex: 4
index: 4
---

# Add a projection

A projection hands each stored event to a handler that updates data shaped for queries. This page
keeps each account's balance for a list of accounts. FCQRS follows each aggregate's versions, so the
handler receives every stored event, including one whose write committed after later writes.
[The read side](../concepts/read-models.html#Track-progress-per-aggregate) explains why that matters.
Projections require a SQLite or PostgreSQL journal.

## Choose where the read model lives

Where the read model lives decides where the projection keeps its progress, and what a crash means
for the handler:

| Read model | Register with | Progress | After a crash or restart |
|---|---|---|---|
| In memory | `Projection.single FromStart` or `AddProjection(handler)` | in memory | the whole journal is read again |
| In the journal's SQL database | `Fcqrs.transactionalProjection` or `AddTransactionalProjection` | committed with each change | each event is applied once |
| Elsewhere, such as a search index | `Projection.single (Named "...")` or `AddProjection(handler, name: "...")` | stored after the handler returns | the handler can receive an event again |

For a SQL read model in the journal's database, follow [Catch up projections](catch-up-projections.html).
Its handler writes through a transaction that FCQRS commits together with the progress.

## Keep a read model in memory

The handler receives an `obj` because the journal holds events from every aggregate and saga. Match the
event types this projection needs and ignore the rest:

```fsharp
open System.Collections.Concurrent

// Each account's balance, rebuilt from the journal at every start.
let balances = ConcurrentDictionary<string, decimal>()

let handle (message: obj) =
    match message with
    // Sender is the ID of the account that stored the event.
    | :? Event<AccountEvent> as event ->
        let id = string event.Sender.Value
        match event.EventDetails with
        | Opened _ -> balances[id] <- 0m
        | Deposited amount -> balances[id] <- balances[id] + amount
        | Withdrawn amount -> balances[id] <- balances[id] - amount
        | _ -> ()
    | _ -> ()
```

Register it with its progress in memory:

```fsharp
let balanceView = Fcqrs.projection api (Projection.single FromStart handle)
```

<div class="cs-alt"></div>

```csharp
using System.Collections.Concurrent;
using static FCQRS.Common;   // Event<>

// Each account's balance, rebuilt from the journal at every start.
var balances = new ConcurrentDictionary<string, decimal>();

void Handle(object message)
{
    // Sender is the ID of the account that stored the event.
    if (message is not Event<AccountEvent> { Sender: { } sender } stored)
        return;
    var id = sender.Value.ToString();
    switch (stored.EventDetails)
    {
        case Opened: balances[id] = 0m; break;
        case Deposited deposited: balances[id] += deposited.Amount; break;
        case Withdrawn withdrawn: balances[id] -= withdrawn.Amount; break;
    }
}

builder.Services.AddFcqrs(connectionString, "accounts")
    .AddAggregate<Account>()
    .AddProjection(Handle);
```

The handler runs for one event at a time. It receives one account's events in version order, and
events of different accounts in no particular order relative to each other. With its progress in
memory, the projection reads the whole journal each time it starts, so start-up takes longer as the
journal grows. Keep the read model somewhere durable when that time matters.

## Keep a read model outside the journal database

Give the projection a name to store its progress in the journal database. After a restart, it resumes
after the last event it recorded:

```fsharp
let search =
    Projection.single (Named "balance-search") updateSearch
    |> Fcqrs.projection api
```

<div class="cs-alt"></div>

```csharp
builder.Services.AddFcqrs(connectionString, "accounts")
    .AddAggregate<Account>()
    .AddProjection(UpdateSearch, name: "balance-search");
```

FCQRS records an event as handled after the handler returns. If the process stops in between, the
handler receives that event again after the restart. Make the write idempotent: for example, store each
account's last applied `Version` with its data, and ignore an event whose version is not newer. A
handler that writes to several stores needs that for each of them.

The name identifies the projection's stored progress. A new name reads the whole journal once. Reusing
a name resumes that projection, so do not reuse one for a different read model.

## Choose which events notify callers

| F# helper | C# handler result | Subscription behaviour |
|---|---|---|
| `Projection.single` | `void` | publish every aggregate event after handling |
| `Projection.filtered` | `Notify` | publish or suppress the handled aggregate event |
| `Projection.multi` | `IMessageWithCID list` | publish the exact notification list returned |

Use filtering when one command produces several events but a caller should wake only after the event
that completes all required read-model updates.

## Wait for the projection

`Fcqrs.projection` returns an `IProjection`; the C# host registers it for dependency injection. A
client can subscribe to a correlation id and wait until this handler has handled the matching event, or
call `CatchUpAsync` to wait until it has handled every event stored before the call.
[Read your writes](read-your-writes.html) shows both. Aggregate `.Send` waits only for the aggregate
reply; the projection is the separate read-side confirmation.

The C# host builder supports one projection per FCQRS runtime: a second `AddProjection` call throws
`InvalidOperationException` at registration. A handler may update several read models in the same
process, and a side-by-side rebuild runs as a separate process (see
[Rebuild a read model](rebuild-a-read-model.html)).

## Handle failures visibly

Do not catch an exception and carry on. A handler that throws terminates the process, so the
projection cannot stop silently while the host appears healthy. The process supervisor restarts it:
a projection with its progress in memory reads the journal again, and a named one resumes after the
last event it recorded. [When FCQRS stops the process](../concepts/process-termination.html) explains
the policy.

A failure to read the journal or to store progress is handled differently: FCQRS logs the error and
retries with a backoff that starts at 1 second and grows to 30 seconds, plus up to 20 percent random
delay. Monitor that error log; the read model stays behind until the database is reachable again.
Missing journal history, such as a deleted journal row, terminates the process, because reading again
cannot bring it back.

To correct derived data, follow [Rebuild a read model](rebuild-a-read-model.html). Do not edit the event
journal to repair a projection. Background: [The read side](../concepts/read-models.html).
