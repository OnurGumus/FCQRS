---
title: Use FCQRS from C#
category: Apply
categoryindex: 4
index: 14
---

# Use FCQRS from C#

C# applications use the same FCQRS model as F# applications: commands describe requests, events
record outcomes, an aggregate decides and folds, and projections build queryable views. The C# API
adds base classes, action factories, delegates, and host-builder registration around that model.

This guide uses the current host-builder API. The lower-level `ActorApi` and `ActorWiring` APIs remain
available for custom composition, but most applications do not need them.

The [tutorial](../tutorial/open-an-account.html) uses C# 15 unions on .NET 11, like this guide.
Start there for a runnable project.

## Compiler requirement for this guide

The examples use C# discriminated unions. The `union` keyword is part of C# 15, which needs the .NET 11
SDK, a release candidate at the time of writing, and a `net11.0` target. FCQRS targets `net10.0` and
can run in a `net11.0` host. On .NET 10, use [record hierarchies](../concepts/csharp-interop.html) or
define the domain in an F# class library and keep the host, endpoints, and projections in C#.

FCQRS writes an explicit case discriminator for unions in the journal. Do not replace its event
serializer with a caseless union representation: two cases can have the same field shape but different
domain meaning.

## 1. Define commands, events, and state

Use commands for intent and persisted events for facts. Deferred events are replies that are published
and folded but not stored. Their fold should leave state unchanged because recovery cannot replay them.
The examples are the account from the tutorial's [withdraw money](../tutorial/withdraw-money.html)
step:

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

## 2. Implement the aggregate

`HandleCommand` chooses an action. `ApplyEvent` reconstructs state from stored events. Keep both
functions deterministic and free of database, network, clock, and random-number calls.
`EventActions` builds the actions: `Persist` stores an event, and `Defer` replies without storing it.

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

`EntityName` is part of persistent identity. Treat it as stable after deployment. See
[Evolve persisted events](evolve-events.html) before renaming domain types or cases.

For a readable old payload that needs a new representation, register
`WithEventUpcaster<OldEvent, CurrentEvent>(convert)` on the host builder before startup. Conversions
apply to journal reads and can form a chain. They do not convert live published messages or migrate
application snapshot state. The [event-evolution guide](evolve-events.html) shows the complete C#
registration and rollout requirements.

`Aggregate<>` carries two optional operational overrides. `SnapshotPolicy` sets the snapshot cadence,
and `PassivationPolicy` sets the idle timeout after which the entity is stopped and its next command
replays:

```csharp
// Or PassivationPolicy.Never to keep the entity in memory.
public override PassivationPolicy PassivationPolicy =>
    PassivationPolicy.NewAfter(TimeSpan.FromHours(2));
```

Both default to configuration; [Configuration](../configuration.html) gives the resolution order.

## 3. Register the runtime

The host starts aggregates first, then sagas, the saga starter, and finally the projection. Register
one projection per FCQRS runtime: a second `AddProjection` or `AddTransactionalProjection` call throws
`InvalidOperationException`. That one handler may update several read-model tables.

```csharp
var builder = Host.CreateApplicationBuilder(args);

var connectionString = "Data Source=accounts.db";
var store = new SqlProjectionStore(
    ProjectionSqlDialect.Sqlite, () => new SqliteConnection(connectionString));
var options = new TransactionalProjectionOptions("Statement", store);

builder.Services
    .AddFcqrs(connectionString, "accounts")
    .AddAggregate<Account>()
    .AddTransactionalProjection(options, Statement.Handle);

var app = builder.Build();
await app.RunAsync();
```

`Statement.Handle` is the tutorial's [statement projection](../tutorial/show-a-statement.html). FCQRS
commits its read-model changes and progress in one transaction. For a read model kept in memory or
outside the journal database, see [Add a projection](add-a-projection.html).

## 4. Send from an endpoint or application service

Registration adds a typed `Handler<AccountCommand, AccountEvent>` to dependency injection. The
handler waits for the matching aggregate reply. It does not by itself wait for a projection. If no
matching reply arrives within `akka.fcqrs.command-timeout` (default 30s), for example because the
aggregate decided `IgnoreEvent` or the filter never matches, the handler raises
`TimeoutException` instead of waiting forever. If the actor system stops while the handler waits, the
handler raises `OperationCanceledException`. In both cases the command may or may not have been
applied. A command that would start a saga its correlation ID already started raises
`SagaAlreadyStartedException`, and the aggregate stored nothing
([Correlation IDs](../concepts/correlation-ids.html#One-saga-start-per-correlation-ID-and-aggregate)).

Handlers are keyed by command/event type pair. Two aggregates sharing the same `TCommand`/`TEvent`
pair make the plain registration ambiguous; resolving it then throws with guidance. Resolve the
aggregate you mean through the keyed registration instead, e.g.
`services.GetKeyedService<Handler<C, E>>(typeof(MyShard))` or `[FromKeyedServices(typeof(MyShard))]`.

```csharp
public sealed class AccountService(
    Handler<AccountCommand, AccountEvent> accounts,
    IProjection statement)
{
    public async Task<Event<AccountEvent>> DepositAsync(
        string accountId, decimal amount, CancellationToken cancellationToken)
    {
        var cid = Values.NewCID();
        var account = Values.CreateAggregateId(accountId);

        // Subscribe before sending so the projection cannot win the race.
        using var projected = statement.SubscribeForFirst(cid);

        // Accept the account's reply: Deposited or Rejected.
        var reply = await accounts(
            _ => true, cid, account, new Deposit(amount));

        // Deferred replies are not journaled and cannot reach a projection.
        if (reply.Journaled?.Value != false)
            await projected.Task.WaitAsync(cancellationToken);

        return reply;
    }
}
```

After `projected.Task` completes, query the read model maintained by this subscription. Use a bounded
cancellation policy because projection subscriptions are in-memory request coordination, not a durable
queue. The complete ordering and notification rules are in [Read your writes](read-your-writes.html).

When a decision depends on data read earlier, pass its aggregate version to
`runtime.Actor.SendIfVersionAsync`. `Values.VersionValue(reply.Version)` reads a reply's version as a
`long`. Inject `FcqrsRuntime` and the aggregate's
`AggregateRefs<AccountCommand, AccountEvent>` to obtain
the actor API and factory. [Send at an expected version](send-if-version.html) gives the complete
service example and handles `AggregateVersionConflictException` when another command has changed
the aggregate.

## 5. Test without starting the host

Construct the aggregate and call its two methods directly:

```csharp
var account = new Account();
var state = new AccountState(Owner: "Alice", Balance: 70m);

// Name the union type: the aggregate expects a Command<AccountCommand>.
var withdraw = TestEnvelope.Command<AccountCommand>(new Withdraw(60m));
Assert.Equal(
    EventActions.Persist<AccountEvent>(new Withdrawn(60m)),
    account.HandleCommand(withdraw, state));

// A larger withdrawal is rejected without storing anything.
var rejection = new Rejected("Insufficient funds: 70 available");
var tooMuch = TestEnvelope.Command<AccountCommand>(new Withdraw(500m));
Assert.Equal(
    EventActions.Defer<AccountEvent>(rejection),
    account.HandleCommand(tooMuch, state));

// Folding the stored withdrawal lowers the balance.
var stored = TestEnvelope.Event<AccountEvent>(new Withdrawn(60m), version: 3);
Assert.Equal(10m, account.ApplyEvent(stored, state).Balance);
```

`TestEnvelope.Command` and `TestEnvelope.Event` also take a `TimeProvider`. Pass a `FakeTimeProvider`
when a decision depends on the envelope's creation time. Add replay fixtures for old
events before changing their serialized shape. See [Test your domain](test-your-domain.html).

## Where to continue

- [Define an aggregate](define-an-aggregate.html) explains all aggregate actions and snapshot choices.
- [Write a saga](write-a-saga.html) shows cross-aggregate coordination and C# registration.
- [C# interop and serialization](../concepts/csharp-interop.html) explains union representation and
  compatibility.
