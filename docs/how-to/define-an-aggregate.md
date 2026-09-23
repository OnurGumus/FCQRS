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

The tutorial's bank account must never pay out more than its balance. Every withdrawal decision needs
the current balance, so one account is one aggregate instance. Define the messages and the two
functions in `Account.fs`, or the aggregate class in `Account.cs`:

<!-- sample: accounts/2-withdraw-money/fsharp Account.fs messages -->
```fsharp
module Account

open FCQRS.Common

// What a caller can ask an account to do.
type AccountCommand =
    | Open of owner: string
    | Deposit of amount: decimal
    | Withdraw of amount: decimal

// What the account replies. Rejected is a reply only: it is never stored.
type AccountEvent =
    | Opened of owner: string
    | Deposited of amount: decimal
    | Withdrawn of amount: decimal
    | Rejected of reason: string

// What the account knows now, rebuilt from its events.
type AccountState = { Owner: string option; Balance: decimal }

// The state before the account's first event.
let initial = { Owner = None; Balance = 0m }
```

<div class="cs-alt"></div>

<!-- sample: accounts/2-withdraw-money/csharp Account.cs messages -->
```csharp
// What a caller can ask an account to do.
public union AccountCommand(Open, Deposit, Withdraw);
public sealed record Open(string Owner);
public sealed record Deposit(decimal Amount);
public sealed record Withdraw(decimal Amount);

// What the account replies. Rejected is a reply only: it is never stored.
public union AccountEvent(Opened, Deposited, Withdrawn, Rejected);
public sealed record Opened(string Owner);
public sealed record Deposited(decimal Amount);
public sealed record Withdrawn(decimal Amount);
public sealed record Rejected(string Reason);

// What the account knows now, rebuilt from its events.
public sealed record AccountState(string? Owner = null, decimal Balance = 0m);
```

<!-- sample: accounts/2-withdraw-money/fsharp Account.fs rules -->
```fsharp
// Chooses what to do with a command, based on the current state.
let decide (command: Command<AccountCommand>) (state: AccountState) =
    match command.CommandDetails, state.Owner with
    | Open _, Some _ -> DeferEvent(Rejected "The account is already open")
    | Open owner, None -> PersistEvent(Opened owner)
    | _, None -> DeferEvent(Rejected "The account is not open")
    | (Deposit amount | Withdraw amount), _ when amount <= 0m ->
        DeferEvent(Rejected "The amount must be positive")
    | Deposit amount, _ -> PersistEvent(Deposited amount)
    | Withdraw amount, _ when amount > state.Balance ->
        DeferEvent(Rejected $"Insufficient funds: {state.Balance} available")
    | Withdraw amount, _ -> PersistEvent(Withdrawn amount)

// Applies one event to the state. A rejection changes nothing.
let fold (event: Event<AccountEvent>) (state: AccountState) =
    match event.EventDetails with
    | Opened owner -> { state with Owner = Some owner }
    | Deposited amount -> { state with Balance = state.Balance + amount }
    | Withdrawn amount -> { state with Balance = state.Balance - amount }
    | Rejected _ -> state
```

<div class="cs-alt"></div>

<!-- sample: accounts/2-withdraw-money/csharp Account.cs rules -->
```csharp
public sealed class Account
    : Aggregate<AccountState, AccountCommand, AccountEvent>
{
    // The name stored with every event of this aggregate.
    public override string EntityName => "Account";
    // The state before the account's first event.
    public override AccountState InitialState => new();

    // Chooses what to do with a command, based on the current state.
    public override EventAction<AccountEvent> HandleCommand(
        Command<AccountCommand> command, AccountState state) =>
        (command.CommandDetails, state.Owner) switch
        {
            (Open, not null) => Reject("The account is already open"),
            (Open open, null) => Store(new Opened(open.Owner)),
            (_, null) => Reject("The account is not open"),
            (Deposit { Amount: <= 0m } or Withdraw { Amount: <= 0m }, _) =>
                Reject("The amount must be positive"),
            (Deposit deposit, _) => Store(new Deposited(deposit.Amount)),
            (Withdraw withdraw, _) when withdraw.Amount > state.Balance =>
                Reject($"Insufficient funds: {state.Balance} available"),
            (Withdraw withdraw, _) => Store(new Withdrawn(withdraw.Amount))
        };

    // Applies one event to the state. A rejection changes nothing.
    public override AccountState ApplyEvent(
        Event<AccountEvent> stored, AccountState state) =>
        stored.EventDetails switch
        {
            Opened opened => state with { Owner = opened.Owner },
            Deposited deposited =>
                state with { Balance = state.Balance + deposited.Amount },
            Withdrawn withdrawn =>
                state with { Balance = state.Balance - withdrawn.Amount },
            Rejected => state
        };

    // Stores the event and replies with it.
    static EventAction<AccountEvent> Store(AccountEvent @event) =>
        EventActions.Persist(@event);

    // Replies without storing anything.
    static EventAction<AccountEvent> Reject(string reason) =>
        EventActions.Defer<AccountEvent>(new Rejected(reason));
}
```

`decide` persists a deposit or withdrawal the account can accept and defers a rejection. `fold`
returns the state unchanged for `Rejected`, so folding a deferred reply in memory leaves the same state
that replay produces. [Withdraw money](../tutorial/withdraw-money.html) runs this aggregate and shows
its journal.

[Register the aggregate with the runtime](../tutorial/open-an-account.html#Start-FCQRS-and-send-commands) before
sending commands. F# supplies the initial state and functions in the registration record; C# supplies
them through the `Aggregate<,,>` base class.

`Fcqrs.aggregate` registers the sharding region and returns an `AggregateHandle` with two members:

- **`.Send cid id command filter`:** send a command and await the first matching aggregate reply. This
  does not wait for a projection; use [Read your writes](read-your-writes.html) for that.
- **`.Factory`:** an entity-ref factory passed to a [saga](write-a-saga.html) so it can target this
  aggregate.

For an edit based on a previously observed version, use `Fcqrs.sendIfVersion` in F# or
`SendIfVersionAsync` in C#. [Send at an expected version](send-if-version.html) shows how to reject a
stale command before the decision function runs and handle the version conflict.

`Snapshots` and `Passivation` are the two operational fields. Both default to configuration, and
`Default` in each is the right answer until a measurement says otherwise: `Snapshots` sets how much
of the journal a recovery replays, `Passivation` how often a recovery happens at all. In C# they are
the overridable `SnapshotPolicy` and `PassivationPolicy` properties on `Aggregate<>`. See
[Configuration](../configuration.html) for the resolution order and the configuration-only forms.

## Bundle application handlers

From FCQRS 6.6.0, an F# application can expose account commands through a record of functions:

```fsharp
type CommandHandlers = {
    Accounts: Handler<Account.AccountCommand, Account.AccountEvent>
}

let registerHandlers actorApi accountDefinition =
    { Accounts = Fcqrs.handler actorApi accountDefinition }
```

`Fcqrs.handler` registers the aggregate immediately and returns a reusable function with signature
`filter -> cid -> aggregateId -> command -> Async<event>`. Execute the returned async computation to
send a command. It returns the matching event's payload, including deferred replies, without waiting
for a projection. Register once during startup and call `Fcqrs.wireSagaStarters` after registering the
aggregates and sagas, including an empty list when there are no sagas.

Keep `Fcqrs.aggregate` and its handle when callers need `.Factory`, the event version, or the
`Journaled` flag used for [projection waiting](read-your-writes.html). C# applications continue to use
the existing `FCQRS.CSharp.Handler<,>` delegate, which returns the full event envelope in a `Task`.

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

When a stored event payload changes shape, register `Fcqrs.withEventUpcaster` before the aggregate in
F# or `WithEventUpcaster` on the C# host builder. [Evolve persisted events](evolve-events.html) shows a
conversion chain and explains why journal recovery, live messages, and snapshots need separate checks.

See [Aggregates and the write side](../concepts/aggregates.html) for the reasoning, and
[Test your domain](test-your-domain.html) to test these two functions directly.
