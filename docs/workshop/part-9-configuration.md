---
title: Part 9 · Configuration
category: Workshop
categoryindex: 4
index: 10
---

# Part 9 — Configuration and running it

The workshop's domain is done; this final part is the operator's view. Where does the Akka.NET
configuration come from, which settings will you actually touch, and what changes when you move from
"runs on my laptop" to "runs in a cluster"? The good news, and the theme of this part, is that FCQRS
ships with sensible defaults, so you configure by *exception* rather than from scratch.

## You usually do not write a HOCON file

Akka.NET is configured with HOCON, and you could hand-write a full configuration — but for the common
case you do not have to. FCQRS carries an embedded default (`src/FCQRS/default.hocon`) and, inside
`Actor.api`, merges it with whatever `IConfiguration` you pass in. The two values you must supply —
the database connection string and the provider type — are substituted into the template, so the
minimum viable configuration is just the call you already saw in Part 8:

```fsharp
let connection = { Actor.DBType = Actor.Sqlite
                   Actor.ConnectionString = connectionString |> ValueLens.TryCreate |> Result.value }
let actorApi = Actor.api builder.Configuration logf (Some connection) clusterName
```

```csharp
var actorApi = ActorApi.Create(builder.Configuration, logf, connectionString, "FocumentCluster");
```

This is why Focument has no `.hocon` file at all: it passes the standard ASP.NET `builder.Configuration`
and a SQLite connection string, and lets the embedded defaults supply everything else. `DBType` is a
closed list — `Sqlite`, `PostgreSQL`, several `SqlServer` versions, `MySql`, `Oracle`, and more — and
choosing a different one swaps the journal, query, and snapshot stores over to that database with no
other change on your part.

## What the defaults actually set up

It is worth reading the embedded configuration once, because it demystifies what "the framework
handles it" means. The defaults wire up three persistence stores against your chosen database — the
**journal** (the event log), the **query journal** (the read-side event stream the projection
consumes), and the **snapshot store** — all with `auto-initialize = true`, so the tables are created
for you on first run.

```hocon
akka.persistence {
    journal.sql          { class = "...SqlWriteJournal..."        connection-string = "${connection-string}"  provider-name = "${db-type}" }
    query.journal.sql    { class = "...SqlReadJournalProvider..." connection-string = "${connection-string}"  provider-name = "${db-type}" }
    snapshot-store.sql   { class = "...SqlSnapshotStore..."       connection-string = "${connection-string}"  provider-name = "${db-type}" }
}
```

The defaults also register the serializers — and here is where a thread from earlier in the workshop
ties off. Two serializers are bound: Akka's Newtonsoft JSON for general objects, and FCQRS's own
`STJSerializer` for anything implementing `FCQRS.Common+ISerializable`. That second binding is what
routes your commands and events — and, on the C# side, your C# 15 `union` types — through FCQRS's
System.Text.Json serializer when they are written to the journal or sent across the cluster.

```hocon
akka.actor {
    serializers          { json = "Akka...NewtonSoftJsonSerializer"   stj = "FCQRS.ActorSerialization+STJSerializer, FCQRS" }
    serialization-bindings { "System.Object" = json   "FCQRS.Common+ISerializable, FCQRS" = stj }
}
```

Finally, the defaults select the cluster actor provider and configure sharding (`state-store-mode =
ddata`, `remember-entities-store = eventsourced`) and a local TCP transport on `port = 0`, which
means "pick a free port." That last choice is what lets the samples form a one-node cluster on a
laptop with no setup.

## The knobs you will actually turn

Three settings cover almost everything you will want to change, and all of them live under the
`config:akka:...` path in your `IConfiguration` (which maps onto the HOCON `config { akka { … } }`).

The **database** is the one you must set, through the connection and `DBType` shown above. Point it
at a file for SQLite or a server for PostgreSQL; nothing else in your code changes.

The **snapshot frequency** is `config:akka:persistence:snapshot-version-count`, which defaults to 30.
This is the *N* from Part 4 — take a snapshot every *N* persisted events. Raise it if your snapshots
are large and infrequent recovery is fine; lower it if entities accumulate many events and you want
faster startup.

The **scheduler** is the interesting one for testing. `saga_sample` overrides
`config:akka:scheduler` to point at FCQRS's `ObservingScheduler`, a test scheduler whose virtual
clock you advance by hand:

```hocon
config { akka { scheduler { implementation = "FCQRS.Scheduler+ObservingScheduler, FCQRS" } } }
```

That single line is what lets the sample drive a saga with a ten-second delay to completion in
milliseconds, deterministically, with no real waiting — invaluable when you want repeatable tests of
time-dependent saga behaviour. In production you simply leave it at the default real scheduler.

When you *do* want to override more, you provide it the ordinary .NET way: a HOCON file loaded into
configuration (as `saga_sample` does with `AddHoconFile`), or any other configuration source. FCQRS
merges your settings over its defaults, so you only specify the deltas.

## From a laptop to a cluster

Out of the box you get a single-node cluster: the process joins itself, sharding runs locally, and
everything just works for development. Scaling out is a matter of Akka.NET configuration rather than
FCQRS code — you give nodes a real hostname and a set of seed nodes to find each other, and cluster
sharding then distributes your aggregates and sagas across them automatically. The aggregate code
from Part 3 does not change at all; an entity simply might now live on another machine, and the
framework routes commands to wherever it is.

Focument's repository shows one concrete production shape: a multi-stage Dockerfile producing a
non-root container, and a Kubernetes deployment behind an nginx ingress with TLS. It is also a useful
cautionary example. In that demo deployment the SQLite database lives on a memory-backed volume,
which makes the **event store ephemeral** — fine for a throwaway demo, but a reminder that since the
journal is your source of truth (Part 4), its durability is the single most important thing to get
right when you deploy for real. Put the journal on storage you would trust your business data to,
because that is exactly what it is.

## Where to go next

You have now seen FCQRS whole: the argument for it, the cast of characters, an aggregate, event
sourcing, the read side, client coordination, sagas, a full request traced end to end, and the
configuration that runs it. The two example applications remain the best next step — open
[`saga_sample`](https://github.com/onurgumus/FCQRS/tree/main/saga_sample) for the smallest complete
picture, and [`focument`](https://github.com/onurgumus/focument) / `focument-csharp` for a realistic
one in the language of your choice. Change something, watch the events accumulate in the journal,
delete the read model and watch it rebuild. The model rewards poking at it.

Return to the [workshop index](README.md) for the full table of contents.
