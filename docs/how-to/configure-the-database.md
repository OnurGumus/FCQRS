---
title: Configure the database
category: How-to
categoryindex: 5
index: 5
---

# Configure the database

You do not need a HOCON file. Build a `Connection` with `Fcqrs.connect` and pass it to `Fcqrs.actor`;
the framework supplies the rest of the Akka.NET configuration from built-in defaults.

```fsharp
open FCQRS.FSharp

let connection =
    Fcqrs.connect FCQRS.Actor.DBType.Sqlite "Data Source=app.db;"

// an empty IConfiguration is fine
let config = Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
let api = Fcqrs.actor config loggerFactory (Some connection) "MyCluster"
```

<div class="cs-alt"></div>

```csharp
// C#: the DI host-builder takes the connection string and cluster name; config
// and logger come from the container. (The host-builder is SQLite-backed — for
// other providers, override via HOCON as in "Override anything else" below.)
using Microsoft.Extensions.DependencyInjection;

services.AddFcqrs("Data Source=app.db;", "MyCluster");
```

`Fcqrs.connect` takes the provider type and the raw connection string and hides the `ShortString`
wrapping; `Fcqrs.actor` takes the cluster name as a plain string.

**Switch providers** by changing the `DBType` — `Sqlite`, `PostgreSQL`, `SqlServer2022`, `MySql`,
`Oracle`, and more. The journal, query, and snapshot stores all follow, and tables auto-initialize on
first run. Only the connection string changes alongside it:

```fsharp
let connection =
    Fcqrs.connect FCQRS.Actor.DBType.PostgreSQL
        "Host=localhost;Database=app;Username=app;Password=…"
```

**Change snapshot frequency** with `config:akka:persistence:snapshot-version-count` (default 30).

**Override anything else** by supplying a HOCON file (or any configuration source) as `config` —
FCQRS merges it over its defaults, so you only specify what differs. Full details and a HOCON example
are in the [Configuration reference](../configuration.html).
