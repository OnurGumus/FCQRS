---
title: Configure the database
category: Apply
categoryindex: 4
index: 10
---

# Configure the database

The journal, query journal, and snapshot store use the same database provider by default. In F#, choose
the provider with `Fcqrs.connect` and pass the connection to `Fcqrs.actor`.

```fsharp
open FCQRS.FSharp

let connection =
    Fcqrs.connect FCQRS.Actor.DBType.Sqlite "Data Source=app.db;"

// An empty IConfiguration accepts the embedded Akka.NET defaults.
let config = Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
let api = Fcqrs.actor config loggerFactory (Some connection) "MyCluster"
```

<div class="cs-alt"></div>

```csharp
// C#: this host-builder overload is SQLite-backed.
using Microsoft.Extensions.DependencyInjection;

services.AddFcqrs("Data Source=app.db;", "MyCluster");
```

The C# `AddFcqrs` overload and `ActorApi.Create` currently create a SQLite connection definition. To use
another provider from a C# host, override the three persistence plugin connection and provider settings
through configuration as shown in the [configuration reference](../configuration.html).

## Choose a provider

`DBType` supports these provider families:

| FCQRS value | Database |
|---|---|
| `Sqlite` | SQLite through Microsoft.Data.Sqlite |
| `PostgreSQL`, `PostgreSQL15` | PostgreSQL |
| `SqlServer2012` through `SqlServer2022` | Microsoft SQL Server |
| `MySql` | MySQL through MySqlConnector |
| `Oracle` | Oracle Database |
| `Firebird` | Firebird |
| `DB2` | IBM Db2 |

Changing the F# `DBType` configures all three persistence stores and enables table initialization:

```fsharp
let connection =
    Fcqrs.connect FCQRS.Actor.DBType.PostgreSQL
        "Host=localhost;Database=app;Username=app;Password=…"
```

Use SQLite for local development and deployments where one process owns the file. A multi-node cluster
needs a database reachable by every node.

Do not commit production credentials in HOCON or source code. Supply them through the application's
secret provider or environment-specific configuration.

## Verify the connection

On a new database, start the application and confirm that journal, query journal, and snapshot tables
are created. Send one command, stop the process, start it again, and send another command to the same
aggregate id. The returned event version should advance, proving that recovery read the existing
journal.

Then test the database account with automatic schema creation disabled if production policy requires
pre-provisioned tables. The account still needs the read and write permissions required by the Akka.NET
persistence plugins.

## Other persistence settings

Change snapshot cadence with `config:akka:persistence:snapshot-version-count`, or use an aggregate or
saga `SnapshotPolicy`. The default is every 30 persisted events.

FCQRS merges application configuration over its embedded HOCON. The
[Configuration reference](../configuration.html) lists the merge order, plugin settings, runtime keys,
and cluster overrides.
