---
title: Configuration
category: Reference
categoryindex: 6
index: 2
---

# Configuration

Akka.NET is configured with HOCON, and FCQRS lets you ignore that almost entirely. This page explains
what you must supply, what you get for free, and how to override the defaults when you need to.

## You usually do not write a HOCON file

FCQRS carries an embedded default configuration and merges it with whatever `IConfiguration` you pass
to `Fcqrs.actor`. The only thing you must supply is a database `Connection` — a connection string and a
provider type, built with `Fcqrs.connect` — which is substituted into the defaults. So the minimal
setup is the call from [Get started](get-started.html):

```fsharp
open FCQRS.FSharp

let connection = Fcqrs.connect FCQRS.Actor.DBType.Sqlite "Data Source=app.db;"

let api = Fcqrs.actor config loggerFactory (Some connection) "MyCluster"
```

An empty `ConfigurationBuilder().Build()` is a perfectly good `config`. `DBType` is a closed list —
`Sqlite`, `PostgreSQL`, several `SqlServer` versions, `MySql`, `Oracle`, and more — and switching it
points the journal, query, and snapshot stores at that database with no other change.

## What the defaults set up for you

The embedded defaults wire three persistence stores against your chosen database — the **journal**
(the event log), the **query journal** (the read-side stream a projection consumes), and the
**snapshot store** — all auto-initialized, so tables are created on first run. They also register the
serializers (Akka's JSON for general objects, and FCQRS's System.Text.Json serializer for messages,
including C# 15 unions — see [C# interop](concepts/csharp-interop.html)), select the cluster actor
provider, and configure sharding and a local transport so a single process forms a one-node cluster
with no setup.

## The settings you will actually change

All live under the `config:akka:…` path of your `IConfiguration` (which maps onto the HOCON
`config { akka { … } }`).

- **Database** — via the `Connection` and `DBType` above. This is the one you must set.
- **Snapshot frequency** — `config:akka:persistence:snapshot-version-count`, default **30**. Take a
  snapshot every *N* persisted events (see [Consistency and recovery](concepts/consistency-and-recovery.html)).
  Raise it for large, infrequently-recovered state; lower it for entities with many events.
- **Scheduler** — point `config:akka:scheduler` at FCQRS's `ObservingScheduler` to drive a virtual
  clock by hand in tests (so a delayed saga step completes in milliseconds, deterministically). Leave
  it at the default for production.

## Overriding with HOCON

When you do want full control — a custom provider, bespoke Akka settings, real clustering — provide a
HOCON file (or any other configuration source) and FCQRS merges your settings over its defaults, so
you only specify the deltas. A SQLite example:

```hocon
config {
  connection-string = "Data Source=app.db;"
  akka {
    persistence {
      journal.sql {
        connection-string = ${config.connection-string}
        provider-name = "SQLite.MS"
        auto-initialize = true
      }
      query.journal.sql {
        connection-string = ${config.connection-string}
        provider-name = "SQLite.MS"
        auto-initialize = true
      }
      snapshot-store.sql {
        connection-string = ${config.connection-string}
        provider-name = "SQLite.MS"
        auto-initialize = true
      }
    }
  }
}
```

Load it the usual .NET way (for example `ConfigurationBuilder().AddHoconFile("config.hocon").Build()`)
and pass the result as the `config` argument.

## Logging and diagnostics

**The message-flow log is on by default.** From `6.0.0-preview28`, FCQRS narrates your application's
messages at `Information` level — no tracing pipeline required. Every aggregate command logs the
effect it yielded, every persisted event logs its new version, and sagas log each event they pick up,
each state transition, and each command they send (with target and delay). Every line carries the
correlation ID, so grepping for one CID replays an entire workflow:

```text
info: FCQRS.MessageFlow
      Command Register ("alice", "pw") to aggregate alice (v0) yielded PersistEvent (VerificationRequested ...) [cid: 00-...]
info: FCQRS.MessageFlow
      Aggregate alice persisted event VerificationRequested ("alice", "pw") (v1) [cid: 00-...]
info: FCQRS.MessageFlow
      Saga alice~Saga~00-... received VerificationRequested ("alice", "pw"), decided StateChangedEvent (UserDefined GeneratingCode) [cid: 00-...]
info: FCQRS.MessageFlow
      Saga alice~Saga~00-... changed state to GeneratingCode [cid: 00-...]
info: FCQRS.MessageFlow
      Saga alice~Saga~00-... sent command SetVerificationCode "327470" to alice [cid: 00-...]
```

These are your application's messages, not FCQRS internals, so the switch lives in FCQRS rather than
only in logging configuration. Turn it off process-wide:

```fsharp
FCQRS.Common.Telemetry.MessageFlowLogging <- false
```

or, with the C# hosting builder, `builder.WithMessageFlowLogging(false)`. All lines go to the
dedicated **`FCQRS.MessageFlow`** logger category, so standard filtering also works — for example
`"Logging:LogLevel:FCQRS.MessageFlow": "None"` in `appsettings.json` — and when the category is
filtered out (or the switch is off) the log lines are never even formatted.

**Akka's own logging ships OFF** — it is chatty, and FCQRS's logs (including the message flow) go
through your `ILoggerFactory` regardless. To see Akka's internals, use
`builder.WithAkkaLogging(AkkaLogLevel.Info)` from the hosting builder, or set `config:akka:loglevel`
in configuration.

**Distributed traces** are emitted alongside the logs: aggregates, sagas, and the projection each
have an `ActivitySource`, and spans parent onto whatever trace was current when the command was sent.
Register them with your OpenTelemetry pipeline in one call:
`tracing.AddSource(FCQRS.Common.Telemetry.AllActivitySources)`. Spans cost nothing until a listener
is attached, so leaving this unwired is fine.

## Scaling to a cluster

Out of the box you get a single-node cluster — the process joins itself and sharding runs locally.
Scaling out is Akka.NET configuration, not FCQRS code: give nodes a real hostname and seed nodes, and
cluster sharding distributes your aggregates and sagas across them automatically. The aggregate and
saga code does not change; an entity simply might live on another machine, and commands are routed to
wherever it is. Because the journal is the source of truth, the one thing to get right in production
is its durability — put it on storage you would trust your business data to.
