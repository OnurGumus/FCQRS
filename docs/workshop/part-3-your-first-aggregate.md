---
title: Part 3 · Your First Aggregate
category: Workshop
categoryindex: 4
index: 4
---

# Part 3 — Your first aggregate

We have talked enough. In this part we build a real aggregate and read every line of it. The example
is the `User` from `saga_sample` (`saga_sample/User.fs`) — small enough that nothing is hidden, but
complete enough to show the whole shape. The F# is the actual code in the repository; the C# beside
it is the same aggregate expressed with C# 15 `union` types, in the idiom the `focument-csharp`
application uses, so you can see that the two languages tell the same story.

An aggregate is made of four things: the **commands** it accepts, the **events** it emits, the
**state** it folds those events into, and two functions — one that *decides* and one that *applies*.
We take them in that order.

## The commands and events

A command is a request, phrased in the imperative. An event is a fact, phrased in the past tense.
The `User` accepts a handful of commands and can emit a handful of events. Notice that the two sets
are not the same: `Register` is a command, but the *fact* it might produce is `VerificationRequested`
or `AlreadyRegistered`. The command is what was asked; the event is what actually happened.

```fsharp
// saga_sample/User.fs
type Command =
    | Login of string
    | Verify of string
    | Register of string * string
    | SetVerificationCode of string

type Event =
    | LoginSucceeded
    | LoginFailed
    | AlreadyRegistered
    | VerificationRequested of string * string
    | VerificationCodeSet of string
    | Verified
```

```csharp
// the same, as C# 15 unions
public union UserCommand(
    UserCommand.Register, UserCommand.Verify,
    UserCommand.Login, UserCommand.SetVerificationCode)
{
    public record Register(string Username, string Password);
    public record Verify(string Code);
    public record Login(string Password);
    public record SetVerificationCode(string Code);
}

public union UserEvent(
    UserEvent.VerificationRequested, UserEvent.VerificationCodeSet,
    UserEvent.Verified, UserEvent.LoginSucceeded,
    UserEvent.LoginFailed, UserEvent.AlreadyRegistered)
{
    public record VerificationRequested(string Username, string Password);
    public record VerificationCodeSet(string Code);
    public record Verified;
    public record LoginSucceeded;
    public record LoginFailed;
    public record AlreadyRegistered;
}
```

In F# these are discriminated unions; in C# they are `union` types whose cases are nested records.
They mean the same thing — a closed set of alternatives the compiler can check exhaustively — and
FCQRS serialises both to the event journal without any mapping code on your part.

## The state

State is what the aggregate folds its events into. For the `User` it is just three optional fields:
once we know the username and password we can authenticate; once we know the verification code we
can check it. Everything is optional because a brand-new user has none of it yet.

```fsharp
// saga_sample/User.fs
type State =
    { Username: string option
      VerificationCode: string option
      Password: string option }
```

```csharp
public record UserState(
    string? Username = null,
    string? Password = null,
    string? VerificationCode = null)
{
    public static readonly UserState Initial = new();
}
```

Hold on to one idea from Part 2 as you look at this: the state is *not* the source of truth, and it
is *not* what gets stored. It is a convenience the aggregate keeps in memory, rebuilt from events
every time the actor wakes up. We will see exactly how in Part 4.

## The decide function: `handleCommand`

This is the heart of the aggregate. It receives the incoming command and the current state, and
returns an *action* — usually "persist this event," sometimes "emit this rejection without storing
it," sometimes "do nothing." It never performs a side effect; it only decides.

```fsharp
// saga_sample/User.fs
let handleCommand (cmd: Command<_>) state =
    match cmd.CommandDetails, state with
    | Register(userName, password), { Username = None } ->
        VerificationRequested(userName, password) |> PersistEvent
    | SetVerificationCode code, _ ->
        VerificationCodeSet code |> PersistEvent
    | Register _, { Username = Some _ } ->
        AlreadyRegistered |> DeferEvent
    | Verify code, { VerificationCode = Some vcode } when code = vcode ->
        Verified |> PersistEvent
    | Login password1, { Username = Some _; Password = Some password2 }
        when password1 = password2 -> LoginSucceeded |> PersistEvent
    | Verify _, _
    | Login _, _ -> LoginFailed |> DeferEvent
```

```csharp
public EventAction<UserEvent> HandleCommand(Command<UserCommand> cmd, UserState state) =>
    (cmd.CommandDetails, state) switch
    {
        (UserCommand.Register r, { Username: null }) =>
            EventActions.Persist<UserEvent>(
                new UserEvent.VerificationRequested(r.Username, r.Password)),
        (UserCommand.SetVerificationCode c, _) =>
            EventActions.Persist<UserEvent>(new UserEvent.VerificationCodeSet(c.Code)),
        (UserCommand.Register, _) =>
            EventActions.Defer<UserEvent>(new UserEvent.AlreadyRegistered()),
        (UserCommand.Verify v, { VerificationCode: { } code }) when v.Code == code =>
            EventActions.Persist<UserEvent>(new UserEvent.Verified()),
        (UserCommand.Login l, { Username: not null, Password: { } pw }) when l.Password == pw =>
            EventActions.Persist<UserEvent>(new UserEvent.LoginSucceeded()),
        _ => EventActions.Defer<UserEvent>(new UserEvent.LoginFailed())
    };
```

Read the cases as a conversation. *"Register, and I have no username yet?"* — then the fact is
`VerificationRequested`, and we want it persisted. *"Register, but I already have a username?"* —
that is a rejection: `AlreadyRegistered`, and crucially it is **deferred**, not persisted. *"Verify
with a code that matches the one I stored?"* — `Verified`, persisted. *"Login with the right
password?"* — `LoginSucceeded`. Anything else is a `LoginFailed`, deferred.

The pattern that the command arrives wrapped — `cmd.CommandDetails` in F#, `cmd.CommandDetails` in
C# — is worth noticing. FCQRS wraps your command in an envelope (`Command<'T>`) that also carries the
correlation id, a timestamp, and metadata. Your decision logic only cares about the payload, so it
reaches through to `CommandDetails`; the envelope is there for the framework's plumbing and for the
tracing we will use later.

### The vocabulary of actions

`handleCommand` returns an `EventAction`, and three of its cases cover almost everything you will
write. This is the same picture as the diagram below: the action you return decides which path the
command takes.

![The write path](../img/write-path.svg)

**PersistEvent** is the normal, happy path. The event is appended to the journal, the aggregate's
version increments by one, the event is applied to produce the new state, and the event is published
so the read side and any sagas can react. This is what you return when something genuinely happened.

**DeferEvent** emits an event *without storing it*. The state does not change, the version does not
move, nothing is written to the journal — but the event is still published, so the caller waiting on
it gets an answer. This is exactly right for rejections. `AlreadyRegistered` and `LoginFailed` are
real things the caller needs to hear about, but they are not facts that change the user; storing
them would pollute the history with non-events. Deferring lets you answer "no" without lying to the
log.

**IgnoreEvent** does nothing at all — no event, no state change, no reply. You reach for it when a
command simply does not apply in the current state and the caller does not need to be told.

There are a few more actions for advanced cases — stashing a message to handle later, for instance —
but Persist, Defer, and Ignore are the ones you will use day to day.

## The apply function: `applyEvent`

If `handleCommand` is where decisions are made, `applyEvent` is where they take effect on the state.
It takes an event and the current state and returns the new state. It is a pure fold step, and it is
deliberately dull — no decisions, no validation, no branching on anything but the event. Dullness is
the point: this same function runs both when a fresh event is persisted *and* when old events are
replayed to rebuild state after a restart, so it must do exactly one thing and do it the same way
every time.

```fsharp
// saga_sample/User.fs
let applyEvent event state =
    match event.EventDetails with
    | VerificationRequested(userName, password) ->
        { state with Username = Some userName; Password = Some password }
    | VerificationCodeSet code ->
        { state with VerificationCode = Some code }
    | _ -> state
```

```csharp
public UserState ApplyEvent(Event<UserEvent> evt, UserState state) =>
    evt.EventDetails switch
    {
        UserEvent.VerificationRequested e =>
            state with { Username = e.Username, Password = e.Password },
        UserEvent.VerificationCodeSet e =>
            state with { VerificationCode = e.Code },
        _ => state
    };
```

Notice that several events — `LoginSucceeded`, `LoginFailed`, `Verified` — fall through to the
`_ -> state` case and change nothing. That is fine and common. An event can matter to the read side
or to a saga without changing the aggregate's own decision-making state. `LoginSucceeded` is worth
recording and worth telling the caller about, but it does not alter what the `User` will decide
next, so the fold leaves the state untouched.

## Wiring it up

Finally, the aggregate is registered with the framework. You hand `InitializeActor` the initial
state, a name for the entity type, and your two functions. What you get back is a factory that can
produce a reference to any individual user by id.

```fsharp
// saga_sample/User.fs
let init (actorApi: IActor) =
    let initialState = { Username = None; Password = None; VerificationCode = None }
    actorApi.InitializeActor initialState "User" handleCommand applyEvent

let factory actorApi entityId =
    (init actorApi).RefFor DEFAULT_SHARD entityId
```

```csharp
public EntityFac<object> Init(IActor actorApi) =>
    IActorExtensions.InitActor<UserState, UserCommand, UserEvent>(
        actorApi, UserState.Initial, "User", HandleCommand, ApplyEvent);
```

That is a complete aggregate. There is no base class to inherit, no interface to implement, no
attributes to sprinkle — just three types and two functions, wired in with one call. Everything
else from Part 2 — the one-at-a-time processing, the sharding, the passivation — is provided by the
framework around these functions, not by anything you had to write.

In [Part 4](part-4-event-sourcing.md) we follow the events after they leave `handleCommand`: how
replaying them rebuilds the state, what snapshots and versions are really for, and how the framework
keeps its footing across a restart.
