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
let loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(fun _ -> ())

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
| `config:akka:fcqrs:saga-start-timeout` | `30` | Maximum seconds allowed for the saga-start handshake before fail-fast |
| `config:akka:fcqrs:command-timeout` | `30s` | Maximum idle wait for a command subscription's matching aggregate reply before a `TimeoutException` |
| `config:akka:fcqrs:notification-buffer` | `1024` | Buffer used for ephemeral projection notifications |
| `config:akka:loglevel` | `OFF` | Akka.NET internal log level |
| `config:akka:stdout-loglevel` | `OFF` | Akka.NET standard-output log level |

The two timeout keys share one unit rule: a **bare number means seconds**. `command-timeout` also
accepts HOCON durations such as `500ms` or `1m` (beware: a bare number would mean *milliseconds* to
HOCON's duration parser — FCQRS parses bare numbers as seconds deliberately, matching
`saga-start-timeout`). The command timeout is an idle timeout: receiving a non-matching event on the
same correlation topic restarts it. Correlation topics are quiet in practice, but it is not a hard
deadline. The same key bounds the projection wait in the F# facade's `sendAwaiting`: a projection
that suppresses the matching notification raises `TimeoutException` instead of hanging the caller.

The notification buffer is not a durable queue. Notifications without an active subscriber may be
dropped, which is correct for the request-scoped read-your-writes mechanism.

Snapshot policy resolves in this order:

1. the aggregate or saga's `Every n` or `NoSnapshots` setting;
2. the C# builder's `WithDefaultSnapshotPolicy` value;
3. `config:akka:persistence:snapshot-version-count`;
4. the fallback value `30`.

See [Deferring, snapshots, and passivation](concepts/aggregate-lifecycle.html) before tuning the cadence.
A snapshot changes replay cost, not the events that define recoverable state.

Set `config:akka:scheduler` to FCQRS's `ObservingScheduler` only in tests that control delayed saga
commands with a virtual clock.

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

The [production tutorial](tutorial/5-production.html) provides the operational checklist.
