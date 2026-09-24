---
title: Configuration
category: Reference
categoryindex: 5
index: 2
---

# Configuration

FCQRS starts from an embedded Akka.NET configuration and merges application configuration over it. This
page lists the defaults, FCQRS runtime keys, persistence providers, and the settings required to move
from one local process to a cluster.

## Minimal configuration

`Fcqrs.connect` supplies the persistence provider and connection string. An empty `IConfiguration`
accepts the rest of the embedded defaults:

```fsharp
open FCQRS.FSharp

let connection = Fcqrs.connect FCQRS.Actor.DBType.Sqlite "Data Source=app.db;"

// An empty IConfiguration accepts the embedded Akka.NET defaults.
let config = Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
let loggerFactory =
    Microsoft.Extensions.Logging.LoggerFactory.Create(fun _ -> ())

let api = Fcqrs.actor config loggerFactory (Some connection) "MyCluster"
```

<div class="cs-alt"></div>

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddFcqrs(
    connectionString: "Data Source=app.db;",
    clusterName: "MyCluster");
```

The supported `DBType` values are listed in [Configure the database](how-to/configure-the-database.html).
The C# host-builder overload creates the same setup with SQLite.

## What the defaults set up for you

The embedded configuration provides:

- a SQL journal for persisted events;
- a SQL read journal consumed by projections;
- a SQL snapshot store;
- automatic persistence-table initialization;
- FCQRS and Akka.NET serializers;
- the Akka.NET cluster actor provider and distributed pub/sub;
- cluster sharding with remembered entities;
- a localhost transport on a dynamic port;
- a one-node cluster formed by joining the process to itself.

The query journal polls for new events every 100 ms by default. Those persistence-plugin settings may
be overridden with HOCON.

## FCQRS runtime keys

The .NET configuration path uses colons. The equivalent HOCON path uses nested objects.

| .NET configuration key | Default | Purpose |
|---|---:|---|
| `config:akka:persistence:snapshot-version-count` | `30` | Snapshot interval used by `SnapshotPolicy.Default` |
| `config:akka:fcqrs:saga-start-timeout` | `30` | Maximum seconds allowed for the saga-start handshake before [fail-fast](concepts/process-termination.html) |
| `config:akka:fcqrs:command-timeout` | `30s` | Deadline from command subscription setup to its matching aggregate reply; nonmatching events do not extend it |
| `config:akka:fcqrs:notification-buffer` | `1024` | Maximum queued notifications per subscriber; a full subscriber queue drops its oldest notification |
| `config:akka:loglevel` | `OFF` | Akka.NET internal log level |
| `config:akka:stdout-loglevel` | `OFF` | Akka.NET standard-output log level |

The two timeout keys share one unit rule: a **bare number means seconds**. `command-timeout` also
accepts HOCON durations such as `500ms` or `1m`. HOCON's own duration parser would read a bare number
as milliseconds; FCQRS reads it as seconds, matching `saga-start-timeout`. The command timeout is a deadline that starts when the command subscription
accepts the command. Non-matching events on the same correlation topic do not restart it. The same
key bounds the projection wait in the F# facade's `sendAwaiting`: a projection that suppresses the
matching notification raises `TimeoutException` instead of hanging the caller.

The notification buffer is not a durable queue. Notifications without an active subscriber may be
dropped, which is correct for the request-scoped read-your-writes mechanism.

Transactional projections use `TransactionalProjectionOptions` for background discovery
(`PollInterval`, default 1s), per-identity batch size (`BatchSize`, default 500), and the complete
catch-up deadline (`CatchUpTimeout`, default 30s). These are registration options rather than HOCON
keys. Projections registered with `Fcqrs.projection` or `AddProjection` use the same defaults. An
aggregate that stores an event wakes the projections on its node before the next poll. See
[Catch up projections](how-to/catch-up-projections.html) for their transaction and snapshot
boundaries.

The saga-start handshake holds no thread: an aggregate waits for the sagas an event starts as
messages. FCQRS 6.9.0 removed `max-worker-threads` and `saga-batch-ttl`, which configured the
thread-pool floor and the coordinator of the earlier blocking handshake; both keys are now ignored. See
[Sagas: durable coordination](concepts/sagas.html#Waiting-costs-no-thread).

Snapshot policy resolves in this order:

1. the aggregate or saga's `Every n` or `NoSnapshots` setting;
2. the C# builder's `WithDefaultSnapshotPolicy` value;
3. `config:akka:persistence:snapshot-version-count`;
4. the fallback value `30`.

See [Deferring, snapshots, and passivation](concepts/aggregate-lifecycle.html) before tuning the cadence.
A snapshot changes replay cost, not the events that define recoverable state.

Set `config:akka:scheduler` to FCQRS's `ObservingScheduler` only in tests that control delayed saga
commands with a virtual clock.

## Passivation timing

An aggregate actor that receives no message for `akka.cluster.sharding.passivate-idle-entity-after`
is stopped and releases its in-memory state. Akka.NET's default is `120s`. Raise it for aggregates
whose replay is expensive relative to their idle memory, lower it for a large keyspace touched once,
and set `0` to disable idle passivation entirely.

```hocon
config.akka.cluster.sharding.passivate-idle-entity-after = 30m
```

Keys nested under the entity name override the shared block for that entity type alone:

```hocon
config.akka.cluster.sharding {
  passivate-idle-entity-after = 30m   # every aggregate type
  Account.passivate-idle-entity-after = 2h   # the Account aggregate only
  Session.passivate-idle-entity-after = 30s
}
```

The override key is the `Name` in the aggregate definition, the same string used to build the
persistence id, so renaming an aggregate moves this key along with its journal contract.

An aggregate whose idle policy belongs to the domain rather than to the deployment can carry it in
its definition, where it outranks both configuration levels:

```fsharp
Fcqrs.aggregate api
    { Name = "Account"
      Initial = initial
      Decide = decide
      Fold = fold
      Snapshots = Default
      Passivation = PassivationPolicy.After(TimeSpan.FromHours 2.0) }
```

<div class="cs-alt"></div>

```csharp
public sealed class Account
    : Aggregate<AccountState, AccountCommand, AccountEvent>
{
    public override PassivationPolicy PassivationPolicy =>
        PassivationPolicy.NewAfter(TimeSpan.FromHours(2));
}
```

`PassivationPolicy.Never` keeps the entity resident until the node stops or the shard moves.
`PassivationPolicy.Default` leaves configuration in charge, so the full order is:

1. the aggregate definition's `After` or `Never`;
2. `akka.cluster.sharding.<EntityName>.passivate-idle-entity-after`;
3. `akka.cluster.sharding.passivate-idle-entity-after`;
4. Akka.NET's `120s`.

Choosing `Never` means the entity holds memory for as long as the node runs. It bounds recovery
cost, not memory, so it suits a small, bounded set of hot aggregates rather than an open keyspace.

Two limits apply:

- Only messages routed through cluster sharding count as activity. Messages an entity sends to
  itself, and direct sends to a resolved `IActorRef`, do not reset the idle timer.
- Sagas are never idle-passivated. FCQRS starts saga regions with remembered entities, and Akka
  disables idle passivation whenever that is on. A saga stops when its workflow reaches `StopSaga`
  or aborts, so a saga that never terminates stays resident by design.

Passivation is not a per-instance setting: every entity of a type shares one timeout, whether it comes
from configuration or from the definition. An individual aggregate instance cannot be given its own.

[Deferring, snapshots, and passivation](concepts/aggregate-lifecycle.html) covers what passivation
does and does not discard. Passivation costs a replay, so tune it together with the snapshot cadence.

## Overriding with HOCON

Application configuration is added after the embedded HOCON, so matching application keys win. The
example below overrides the three SQLite persistence stores explicitly:

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

Load the file with `ConfigurationBuilder().AddHoconFile("config.hocon").Build()` and pass the result to
`Fcqrs.actor`, or add the same keys through another `IConfiguration` provider.

When overriding the database provider, change the journal, query journal, and snapshot store together.
Pointing them at different databases is possible but changes backup, recovery, and availability
behaviour and should be an explicit design choice.

## Logging and diagnostics

FCQRS emits a message-flow log through `ILogger` and spans through `ActivitySource`. Configure payload
visibility before handling sensitive data. [Observe your system](how-to/observability.html) lists the
categories, source names, switches, and fatal-flush hook.

Akka.NET internal logging defaults to `OFF`; FCQRS application-flow logs still use the supplied
`ILoggerFactory`. Enable Akka.NET internals with
`builder.WithAkkaLogging(AkkaLogLevel.Info)` from the hosting builder, or set `config:akka:loglevel`
in your `IConfiguration`.

## Scaling to a cluster

The default node listens on localhost and joins itself. A multi-node deployment must override the
remote hostname and port and configure seed-node discovery or another Akka.NET bootstrap mechanism.
Every node must reach the shared journal and use compatible serializers and event contracts.

Cluster sharding routes an aggregate or saga id to its current node, so domain definitions do not
change. Before deploying several nodes, verify rolling-version compatibility, shared storage, node
discovery, coordinated shutdown, and monitoring for cluster membership and unreachable nodes.

[Observe your system](how-to/observability.html) covers runtime diagnostics.
