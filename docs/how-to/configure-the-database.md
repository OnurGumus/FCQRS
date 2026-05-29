---
title: Configure the database
category: How-to
categoryindex: 5
index: 5
---

# Configure the database

You do not need a HOCON file. Pass a `Connection` to `FCQRS.Actor.api` and the framework supplies the
rest of the Akka.NET configuration from built-in defaults.

```fsharp
open FCQRS.Actor
open FCQRS.Model.Data

let connection =
    Some { ConnectionString = "Data Source=app.db;" |> ValueLens.TryCreate |> Result.value
           DBType = DBType.Sqlite }

// an empty IConfiguration is fine
let config = Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
let actorApi = FCQRS.Actor.api config loggerFactory connection clusterName
```

**Switch providers** by changing `DBType` — `Sqlite`, `PostgreSQL`, `SqlServer2022`, `MySql`,
`Oracle`, and more. The journal, query, and snapshot stores all follow, and tables auto-initialize on
first run. Only the connection string changes alongside it:

```fsharp
let connection =
    Some { ConnectionString = "Host=localhost;Database=app;Username=app;Password=…"
                              |> ValueLens.TryCreate |> Result.value
           DBType = DBType.PostgreSQL }
```

**Change snapshot frequency** with `config:akka:persistence:snapshot-version-count` (default 30).

**Override anything else** by supplying a HOCON file (or any configuration source) as `config` —
FCQRS merges it over its defaults, so you only specify what differs. Full details and a HOCON example
are in the [Configuration reference](../configuration.html).
