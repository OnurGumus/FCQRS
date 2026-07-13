---
title: Use FCQRS from C#
category: How-to
categoryindex: 5
index: 7
---

# Use FCQRS from C#

The model is identical to F#; the C# interop layer gives you typed entry points so you never touch an
F# function value. Commands and events are **C# 15 `union` types** (the framework serializes them
natively).

> **Compiler requirement.** The `union` keyword is a preview language feature: you need `net10.0`+
> with `<LangVersion>preview</LangVersion>` until C# 15 ships it as stable. The feature only affects
> compilation — the compiled unions are ordinary structs, so the runtime and your deployment targets
> need nothing special. If your team cannot enable a preview `LangVersion`, put the domain (commands,
> events, aggregates, sagas) in a small F# project — that is the fully supported stable-compiler path
> today — and keep the host, endpoints, and projections in C#.

## Commands and events as unions

```csharp
public union UserCommand(UserCommand.Register, UserCommand.Login)
{
    public record Register(string Username, string Password);
    public record Login(string Password);
}

public union UserEvent(
    UserEvent.Registered, UserEvent.AlreadyRegistered,
    UserEvent.LoginSucceeded, UserEvent.LoginFailed)
{
    public record Registered(string Username, string Password);
    public record AlreadyRegistered;
    public record LoginSucceeded;
    public record LoginFailed;
}
```

## The aggregate

State is a `record`; the two functions are `switch` expressions; `EventActions` builds the action.

```csharp
public record UserState(string? Username = null, string? Password = null)
{
    public static readonly UserState Initial = new();
}

public EventAction<UserEvent> HandleCommand(
    Command<UserCommand> cmd, UserState state) =>
    (cmd.CommandDetails, state) switch
    {
        (UserCommand.Register r, { Username: null }) =>
            EventActions.Persist<UserEvent>(
                new UserEvent.Registered(r.Username, r.Password)),
        (UserCommand.Register, _) =>
            EventActions.Defer<UserEvent>(new UserEvent.AlreadyRegistered()),
        (UserCommand.Login l, { Username: not null, Password: { } pw })
            when l.Password == pw =>
            EventActions.Persist<UserEvent>(new UserEvent.LoginSucceeded()),
        _ => EventActions.Defer<UserEvent>(new UserEvent.LoginFailed())
    };

public UserState ApplyEvent(Event<UserEvent> evt, UserState state) =>
    evt.EventDetails switch
    {
        UserEvent.Registered e =>
            state with { Username = e.Username, Password = e.Password },
        _ => state
    };

public EntityFac<object> Init(IActor actorApi) =>
    IActorExtensions.InitActor<UserState, UserCommand, UserEvent>(
        actorApi, UserState.Initial, "User", HandleCommand, ApplyEvent);
```

## Create the system and send a command

```csharp
var actorApi = ActorApi.Create(
    builder.Configuration, loggerFactory, "Data Source=app.db;", "app");
var factory = entityId => Init(actorApi).RefFor(DEFAULT_SHARD, entityId);

var cid = Helpers.NewCID();
var aggregateId = Helpers.CreateAggregateId("alice");

// subscribe BEFORE sending
using var awaiter = ISubscribeExtensions.SubscribeFor(subs, cid, 1);
await IActorExtensions.SendCommandAsync(
    actorApi, factory, cid, aggregateId,
    new UserCommand.Register("alice", "s3cret"),
    e => e is UserEvent.Registered or UserEvent.AlreadyRegistered);
await awaiter.Task;                  // read side is current
```

The read side uses `QueryApi.InitWithList`, and sagas use `SagaBuilderCSharp.InitSimple` with
`SagaCommands.ToOriginator` / `SagaEventActions` / `SagaTransitions`. A complete C# application —
aggregate, saga, projection, HTTP API, and deployment — is the
[`focument-csharp`](https://github.com/onurgumus/focument) sample. Background:
[C# interop and serialization](../concepts/csharp-interop.html).
