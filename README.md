# FCQRS

FCQRS is a small F# framework for building applications with **CQRS** and **event sourcing** on top
of Akka.NET actors, usable from both F# and C#. You write pure decision and fold functions; the
framework supplies the actors, sharding, persistence, sagas, and client coordination.

![How FCQRS fits together](docs/img/architecture.svg)

Each entity is an **aggregate** — an actor that processes one command at a time, decides what
happened, and emits **events**. Events are appended to a journal (the source of truth) and flow out to
**read models** shaped for querying and to **sagas** that turn events into follow-up commands. A
**correlation id** threads through a request so a caller knows exactly when the read side has caught
up. The same domain reads almost identically in C#, using C# 15 discriminated `union` types.

## Get set, go

Build a tiny FCQRS app from scratch — a `User` aggregate first, then a read model. (Every snippet below
is copied from a project that builds and runs; needs the **.NET 11 preview SDK** for C# 15 `union`
types.)

### Step 1 — Create the project

Create a console app and add the framework:

```bash
dotnet new console -n MyApp && cd MyApp
dotnet add package FCQRS --prerelease
dotnet add package Microsoft.Extensions.Hosting
```

In `MyApp.csproj`, target .NET 11 and turn on C# 15 unions:

```xml
<TargetFramework>net11.0</TargetFramework>
<LangVersion>preview</LangVersion>
```

### Step 2 — Model the commands, events, and state

A `User` that can register and log in. Commands and events are **C# 15 `union` types**; the state is a
plain record. Put these in **`User.cs`**:

```csharp
public union UserCommand(UserCommand.Register, UserCommand.Login)
{
    public record Register(string Username, string Password);
    public record Login(string Password);
}

public union UserEvent(UserEvent.Registered, UserEvent.AlreadyRegistered,
                       UserEvent.LoginSucceeded, UserEvent.LoginFailed)
{
    public record Registered(string Username, string Password);
    public record AlreadyRegistered;
    public record LoginSucceeded;
    public record LoginFailed;
}

public record UserState(string? Username = null, string? Password = null)
{
    public static readonly UserState Initial = new();
}
```

### Step 3 — Write the aggregate (decide & fold)

The aggregate is two pure functions — **decide** (`HandleCommand`) turns a command + current state into
an action, and **fold** (`ApplyEvent`) folds an event into the next state. The `Aggregate<>` base
supplies the actor, persistence and sharding:

```csharp
using static FCQRS.Common;   // Command<>, Event<>, EventAction<>
using static FCQRS.CSharp;    // Aggregate<>, EventActions

public sealed class UserAggregate : Aggregate<UserState, UserCommand, UserEvent>
{
    public override UserState InitialState => UserState.Initial;
    public override string EntityName => "User";

    // decide: a command + the current state -> what happened
    public override EventAction<UserEvent> HandleCommand(Command<UserCommand> cmd, UserState state) =>
        (cmd.CommandDetails, state) switch
        {
            (UserCommand.Register r, { Username: null }) =>
                EventActions.Persist<UserEvent>(new UserEvent.Registered(r.Username, r.Password)),
            (UserCommand.Register, _) =>                       // already taken — answer, don't store
                EventActions.Defer<UserEvent>(new UserEvent.AlreadyRegistered()),
            (UserCommand.Login l, { Password: { } pw }) when l.Password == pw =>
                EventActions.Persist<UserEvent>(new UserEvent.LoginSucceeded()),
            _ => EventActions.Defer<UserEvent>(new UserEvent.LoginFailed())
        };

    // fold: an event -> the next state (pure; runs on persist AND on replay)
    public override UserState ApplyEvent(Event<UserEvent> evt, UserState state) =>
        evt.EventDetails switch
        {
            UserEvent.Registered e => state with { Username = e.Username, Password = e.Password },
            _ => state
        };
}
```

### Step 4 — Run it

Wire it and send one command. The aggregate's resulting event comes **straight back** — no read model
needed yet. **`Program.cs`**:

```csharp
using FCQRS;
using static FCQRS.CSharp;   // Values, Handler
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services
    .AddFcqrs("Data Source=myapp.db;", "MyCluster")    // SQLite-backed actor system
    .AddAggregate<UserAggregate, UserState, UserCommand, UserEvent>();

using var app = builder.Build();
await app.StartAsync();

var send = app.Services.GetRequiredService<Handler<UserCommand, UserEvent>>();

var ev = await send(
    e => e is UserEvent.Registered or UserEvent.AlreadyRegistered,   // the event to wait for
    Values.NewCID(),
    Values.CreateAggregateId("alice"),
    new UserCommand.Register("alice", "s3cret"));

Console.WriteLine(ev.EventDetails is UserEvent.Registered ? "registered alice" : "alice already taken");
await app.StopAsync();
```

```bash
dotnet run
# registered alice      (run again → "alice already taken", state rebuilt from the journal)
```

That's the whole write side — a command in, an event out, state folded from events, persisted for you.

### Step 5 — Add a read model (projection)

The aggregate reacts to one command at a time. To *query* your data, add a **projection** that folds
events into a read model. Add SQLite + Dapper:

```bash
dotnet add package Microsoft.Data.Sqlite
dotnet add package Dapper
```

A projection runs once per event, folds it into a table, and advances its offset in the **same
transaction**. Return the events to re-publish — that's what lets you wait until the read side is
current (read-your-writes). **`Projection.cs`**:

```csharp
using Dapper;
using Microsoft.Data.Sqlite;
using static FCQRS.Common;        // Event<>
using static FCQRS.Model.Data;     // IMessageWithCID

public static class Projection
{
    public static void EnsureTables(string conn)
    {
        using var c = new SqliteConnection(conn); c.Open();
        c.Execute("CREATE TABLE IF NOT EXISTS Users   (Username TEXT PRIMARY KEY)");
        c.Execute("CREATE TABLE IF NOT EXISTS Offsets (OffsetName TEXT PRIMARY KEY, OffsetCount INTEGER)");
        c.Execute("INSERT OR IGNORE INTO Offsets VALUES ('UserProjection', 0)");
    }

    public static IList<IMessageWithCID> HandleEventWrapper(string conn, long offset, object evt)
    {
        using var c = new SqliteConnection(conn); c.Open();
        using var tx = c.BeginTransaction();
        var notify = new List<IMessageWithCID>();

        if (evt is Event<UserEvent> { EventDetails: UserEvent.Registered e } userEvent)
        {
            c.Execute("INSERT OR IGNORE INTO Users (Username) VALUES (@u)", new { u = e.Username }, tx);
            notify.Add(userEvent);   // re-publish → wakes the read-your-writes waiter
        }

        c.Execute("UPDATE Offsets SET OffsetCount = @o WHERE OffsetName = 'UserProjection'", new { o = offset }, tx);
        tx.Commit();
        return notify;
    }
}
```

Register it (`.AddProjection`), subscribe before sending, then query the table. **`Program.cs`**:

```csharp
using FCQRS;
using Dapper;
using Microsoft.Data.Sqlite;
using static FCQRS.CSharp;   // Values, Handler
using static FCQRS.Query;     // ISubscribe
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

const string conn = "Data Source=myapp.db;";
Projection.EnsureTables(conn);

var builder = Host.CreateApplicationBuilder(args);
builder.Services
    .AddFcqrs(conn, "MyCluster")
    .AddAggregate<UserAggregate, UserState, UserCommand, UserEvent>()
    .AddProjection(
        handler:    sp => (offset, evt) => Projection.HandleEventWrapper(conn, offset, evt),
        lastOffset: _  => 0);

using var app = builder.Build();
await app.StartAsync();

var send = app.Services.GetRequiredService<Handler<UserCommand, UserEvent>>();
var subs = app.Services.GetRequiredService<ISubscribe>();

var cid = Values.NewCID();
using (var awaiter = subs.SubscribeForFirst(cid))    // subscribe BEFORE sending
{
    await send(e => e is UserEvent.Registered or UserEvent.AlreadyRegistered,
               cid, Values.CreateAggregateId("alice"),
               new UserCommand.Register("alice", "s3cret"));
    await awaiter.Task;                              // read model is now up to date
}

using var db = new SqliteConnection(conn);
Console.WriteLine("users: " + string.Join(", ", db.Query<string>("SELECT Username FROM Users")));
await app.StopAsync();
```

```bash
dotnet run
# users: alice
```

That's the full loop — command → event → journal → projection → read-your-writes — from two pure
functions and one DI registration. A **saga** (a second aggregate enforcing a cross-aggregate rule, like
a per-user quota) is the natural next step.

**Want F#, a web front end, or the long walkthrough?** The
**[docs](https://onurgumus.github.io/FCQRS/)** build all of this up gradually (F# and C#), and
**[focument_workshop](https://github.com/OnurGumus/focument_workshop)** is a full runnable web app.

## Documentation

The full documentation lives at **[onurgumus.github.io/FCQRS](https://onurgumus.github.io/FCQRS/)**,
organized by what you're trying to do:

- **Get started** — install and run a complete write-and-read loop in minutes.
- **Tutorial** — build an app step by step: aggregate, read model, query, saga.
- **Concepts** — the why behind each piece: CQRS & event sourcing, aggregates, the read side, sagas,
  consistency & recovery, C# interop.
- **How-to guides** — focused recipes for specific tasks.
- **Reference** — the generated API docs and the configuration reference.

## Examples

- **[`sample/`](sample/)** — the smallest complete picture: a `User` that registers and logs in.
- **[`saga_sample/`](saga_sample/)** — adds a verification saga that sends an e-mail.
- **[`focument_workshop`](https://github.com/OnurGumus/focument_workshop)** (C#) — a runnable web app: a
  document store with versioning, restore, and a per-user quota enforced by an approval saga.
- **[`focument_fsharp`](https://github.com/OnurGumus/focument_fsharp)** (F#) and
  **[`focument-csharp`](https://github.com/OnurGumus/focument-csharp)** (C#) — the same domain as full
  applications.

## License

See [LICENSE.md](LICENSE.md).
