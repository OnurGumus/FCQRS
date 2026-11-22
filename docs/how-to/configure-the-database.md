---
title: Configure the database
category: Apply
categoryindex: 5
index: 21
---

# Configure the database

The journal, query journal, and snapshot store use the same database provider by default. In F#, choose
the provider with `Fcqrs.connect` and pass the connection to `Fcqrs.actor`.

```fsharp
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.FSharp

let connection =
    Fcqrs.connect FCQRS.Actor.DBType.Sqlite "Data Source=app.db;"

// An empty IConfiguration accepts the embedded Akka.NET defaults.
let config = ConfigurationBuilder().Build()
let loggerFactory = LoggerFactory.Create(fun _ -> ())
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

| FCQRS value | Database | Tested by FCQRS | Projections and schedules |
|---|---|---|---|
| `Sqlite` | SQLite through Microsoft.Data.Sqlite | yes | yes |
| `PostgreSQL`, `PostgreSQL15` | PostgreSQL | yes | yes |
| `SqlServer2012` through `SqlServer2022` | Microsoft SQL Server | yes | yes |
| `Oracle` | Oracle Database 19c or later | yes | yes |
| `MySql` | MySQL through MySqlConnector | no | no |
| `Firebird` | Firebird | no | no |
| `DB2` | IBM Db2 | no | no |

Changing the F# `DBType` configures all three persistence stores and creates their tables:

```fsharp isolated
open FCQRS.FSharp

let connection =
    Fcqrs.connect FCQRS.Actor.DBType.PostgreSQL
        "Host=localhost;Database=app;Username=app;Password=…"
```

FCQRS's tests store aggregates, sagas, and workflows on the first four, kill the process while
they write, and check the recovered state. They do not run on MySQL, Firebird, or Db2. On Firebird
and Db2, Akka.Persistence.Sql creates the journal without a unique key on a persistence ID's sequence
numbers, the key that stops two writers from storing the same event number of one aggregate; add it
before you use either.

Projections, workflows started from a projection, and schedules also read the journal with their
own SQL, which FCQRS writes for SQLite, PostgreSQL, SQL Server, and Oracle. With another provider,
registering a projection or starting the schedule dispatcher raises `ArgumentException`.

Use SQLite for local development and deployments where one process owns the file. FCQRS runs on one
node; [Scaling to a cluster](../configuration.html#Scaling-to-a-cluster) explains why.

## SQL Server

Create the database with a binary collation before the application starts:

```sql
CREATE DATABASE accounts COLLATE Latin1_General_100_BIN2_UTF8;
```

Then add the Microsoft.Data.SqlClient package to the application and connect with a `SqlServer` value:

```fsharp isolated
open FCQRS.FSharp

let connectionString =
    "Server=localhost;Database=accounts;User Id=app;Password=…;"
    + "TrustServerCertificate=True"

let connection =
    Fcqrs.connect FCQRS.Actor.DBType.SqlServer2022 connectionString
```

Akka.NET creates the journal and snapshot tables without a collation of their own, so the persistence
identity column takes the database's. A server's default collation usually ignores case, and under it
`Account/0/abc` and `Account/0/ABC` are one journal key: one aggregate could recover the other's events,
or fail to store its own. The actor system therefore refuses to start, with `InvalidOperationException`,
when the database's default collation is not binary (`_BIN` or `_BIN2`), or when an existing journal or
snapshot table's persistence identity column has a collation other than the database's.

A binary collation also makes SQL Server compare the names of tables, columns, and catalog views
exactly. Write a read model's SQL with the case its tables were created with, and write catalog views
in upper case, such as `INFORMATION_SCHEMA.COLUMNS`. Like every SQL Server collation, a binary one
ignores trailing spaces when it compares, so `abc` and `abc ` are still one key: do not end an
aggregate ID with a space.

Projections, workflows started from a projection, and schedules need SQL Server 2016 or later, with the
database at compatibility level 130 or higher, because FCQRS reads JSON lists with `OPENJSON`. A
projection refuses to start on an older level. Its name can be at most 128 characters.

SQL Server's default read committed isolation makes a projection's journal query wait for a journal
write that has not committed. With `READ_COMMITTED_SNAPSHOT` on, the default on Azure SQL Database, the
query reads past that write instead, as it does on PostgreSQL, and a later pass applies the event after
the write commits. Either way, a projection applies each aggregate's events in version order and skips
none.

## Oracle

Add the Oracle.ManagedDataAccess.Core package to the application and connect with the `Oracle`
value. The connection string names the user whose schema holds the tables:

```fsharp isolated
open FCQRS.FSharp

let connection =
    Fcqrs.connect FCQRS.Actor.DBType.Oracle
        "User Id=accounts;Password=…;Data Source=dbhost:1521/ACCOUNTS"
```

The user needs the CREATE SESSION, CREATE TABLE, CREATE SEQUENCE, and CREATE TRIGGER privileges
and a quota on its tablespace.

Akka.Persistence.Sql cannot create its Oracle tables safely: its creation fails with ORA-00955 at
the second start, and it adds no unique key on a persistence ID's sequence numbers. FCQRS therefore
creates the journal and snapshot tables itself before any actor writes, with that key and Akka's
indexes, and turns Akka's own creation off. On a journal that exists, it adds the key and indexes
the journal lacks. A journal that already holds one event number twice cannot take the key, and the
start fails with ORA-02299.

FCQRS also stores tags in the journal's own column, Akka's `tag-write-mode` and `tag-read-mode`
`Csv`, and never in a tag table. With a tag table, Akka's read journal wraps each ordered page of
events in a query that Oracle can return out of order, and a reader receives some events twice. The
actor system refuses to start when configuration turns `auto-initialize` on or sets a tag table for
an Oracle plugin.

Oracle compares strings by their bytes by default, so `Account/0/abc` and `Account/0/ABC` are two
journal keys. A projection refuses to start on a session whose `NLS_COMP` is not `BINARY`. Like
PostgreSQL, Oracle lets a projection's journal query read past a write that has not committed, and
a later pass applies the event after the write commits. A projection's name can be at most 128
characters.

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
