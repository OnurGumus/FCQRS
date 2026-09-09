---
title: Define an aggregate
category: Apply
categoryindex: 4
index: 2
---

# Define an aggregate

An aggregate owns the rules and state required to decide commands for one entity. Choose the boundary
before writing the types: every rule that must be decided atomically needs to fit inside the state of
one aggregate instance. FCQRS processes its commands sequentially, eliminating races within that
boundary.

The [registration example](../get-started.html) gives each account one registered name. Define the
messages and the two functions in `Account.fs`, or the aggregate class in `Account.cs`:

<!-- sample: fsharp Account.fs messages -->
```fsharp
module Account

open FCQRS.Common
open FCQRS.FSharp

type RegisterUser = RegisterUser of name: string
type UserRegistered = UserRegistered of name: string
```

<div class="cs-alt"></div>

<!-- sample: csharp Account.cs messages -->
```csharp
using static FCQRS.Common;
using static FCQRS.CSharp;

public sealed record RegisterUser(string Name);
public sealed record UserRegistered(string Name);
public sealed record AccountState(string? Name = null);
```

<!-- sample: fsharp Account.fs rules -->
```fsharp
let decide (command: Command<RegisterUser>) (state: string option) =
    let (RegisterUser name) = command.CommandDetails
    persistIf state.IsNone (UserRegistered(defaultArg state name))

let fold (event: Event<UserRegistered>) (_state: string option) =
    let (UserRegistered name) = event.EventDetails
    Some name
```

<div class="cs-alt"></div>

<!-- sample: csharp Account.cs rules -->
```csharp
public sealed class Account : Aggregate<AccountState, RegisterUser, UserRegistered>
{
    public override string EntityName => "RegistrationCSharpAccount";
    public override AccountState InitialState => new();

    public override EventAction<UserRegistered> HandleCommand(
        Command<RegisterUser> command, AccountState state) =>
        EventActions.PersistConditionally(state.Name is null,
            new UserRegistered(state.Name ?? command.CommandDetails.Name));

    public override AccountState ApplyEvent(Event<UserRegistered> stored, AccountState state) =>
        new(stored.EventDetails.Name);
}
```

The condition persists the first registration and defers subsequent replies using the saved name.
The fold applies either outcome; replay applies only stored events.

[Register the aggregate with the runtime](../tutorial/2-running-it.html#Connect-it-to-FCQRS) before
sending commands. F# supplies the initial state and functions in the registration record; C# supplies
them through the `Aggregate<,,>` base class.

`Fcqrs.aggregate` registers the sharding region and returns an `AggregateHandle` with two members:

- **`.Send cid id command filter`:** send a command and await the first matching aggregate reply. This
  does not wait for a projection; use [Read your writes](read-your-writes.html) for that.
- **`.Factory`:** an entity-ref factory passed to a [saga](write-a-saga.html) so it can target this
  aggregate.

`Snapshots` and `Passivation` are the two operational fields. Both default to configuration, and
`Default` in each is the right answer until a measurement says otherwise: `Snapshots` sets how much
of the journal a recovery replays, `Passivation` how often a recovery happens at all. In C# they are
the overridable `SnapshotPolicy` and `PassivationPolicy` properties on `Aggregate<>`. See
[Configuration](../configuration.html) for the resolution order and the configuration-only forms.

## Choose the action

| Action | Stored | Folded into state | Returned to caller | Sent to projections |
|---|---:|---:|---:|---:|
| `PersistEvent event` | yes | yes | yes | yes |
| `DeferEvent reply` | no | yes, in memory | yes | no |
| `IgnoreEvent` | no | no | no | no |
| `UnhandledEvent` | no | no | handled as unhandled | no |

Persist a fact required to recover the aggregate. Defer a rejection or repeated verdict whose fold
leaves the current state unchanged. FCQRS folds the deferred event in the live actor, but recovery
cannot replay it. A state change caused only by a deferred event therefore disappears after restart.
A deferred reply never wakes a journal projection subscription.

[Deferring, snapshots, and passivation](../concepts/aggregate-lifecycle.html) explains why these
choices remain correct after the actor leaves memory and later recovers.

## Keep replay deterministic

`fold` runs both after persistence and during recovery. It must not read the clock, generate ids, call
services, or write to another store. Capture changing values before persistence and put them in the
event.

`decide` should also remain a deterministic domain function. It may read values already carried by the
command envelope, including `CreationDate`, but should not perform I/O. Use a
[saga](write-a-saga.html) for durable cross-boundary work or an
[async effect](dispatch-async-effects.html) for best-effort work that may be lost on restart.

## Keep identities stable

The aggregate `Name` / `EntityName` identifies its sharding and persistence type. Keep it stable after events have
been written. Each entity id identifies one aggregate instance, so route every command for the same
business entity with the same id.

See [Aggregates and the write side](../concepts/aggregates.html) for the reasoning, and
[Test your domain](test-your-domain.html) to test these two functions directly.
