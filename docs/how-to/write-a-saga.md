---
title: Write a saga
category: Apply
categoryindex: 4
index: 7
---

# Write a saga

This recipe coordinates a money transfer across two accounts. Alice's account stores `TransferSent`,
which takes the money out of her balance. Bob's account decides whether to accept it. A saga delivers
the money to the target account and, if the target rejects it, refunds the source.
[Transfer money](../tutorial/transfer-money.html) builds and runs this saga step by step. This page
lists what every saga needs, using the same code.

Read [Sagas](../concepts/sagas.html) first if you need the ground-up explanation of transitions,
`SagaStartingEvent`, the starter handshake, and recovery re-drive.

> **Motivation:** Use a saga here because the transfer crosses two independent owners and the
> conversation must survive a process restart.

## Write the state table first

| Current state | Incoming event | Next state | Command issued |
|---|---|---|---|
| not started | `TransferSent` from the source | `Delivering` | `ReceiveTransfer` to the target |
| `Delivering` | `TransferReceived` from the target | `Completed` | none; stop saga |
| `Delivering` | `Rejected` from the target | `Refunding` | `RefundTransfer` to the source |
| `Refunding` | `TransferRefunded` from the source | `Completed` | none; stop saga |
| `Delivering` or `Refunding` | no reply before the deadline | the same state | the same command |

The implementation has one function for the first three columns and another for the last column.

> **Motivation:** Writing the table first exposes missing outcomes and accidental loops before routing,
> persistence, or language syntax can hide them.

## Map incoming events to persisted states

Each state carries what its command needs:

<!-- sample: accounts/5-transfer-money/fsharp Transfer.fs states -->
```fsharp
// What a transfer moves, and between which accounts.
type TransferDetails =
    { Id: string
      Source: string
      Target: string
      Amount: decimal }

// Where a transfer is.
type TransferState =
    // Waiting for the target account to take the money.
    | Delivering of TransferDetails
    // The target turned it down: waiting for the source account's refund.
    | Refunding of TransferDetails
    | Completed
```

<div class="cs-alt"></div>

<!-- sample: accounts/5-transfer-money/csharp Transfer.cs states -->
```csharp
// What a transfer moves, and between which accounts.
public sealed record TransferDetails(
    string Id, string Source, string Target, decimal Amount);

// Where a transfer is.
public union TransferState(Delivering, Refunding, Completed);
// Waiting for the target account to take the money.
public sealed record Delivering(TransferDetails Transfer);
// The target turned it down: waiting for the source account's refund.
public sealed record Refunding(TransferDetails Transfer);
public sealed record Completed;

// The saga needs no fixed data besides its state.
public sealed record TransferData;
```

`handleEvent` receives events from every participant as `obj`. Match the typed envelope and current
saga state together. The state is `None` when the starting event first reaches user code. `Sender`
identifies the account that stored an event, which separates the target's rejection from any other.

<!-- sample: accounts/5-transfer-money/fsharp Transfer.fs react -->
```fsharp
// Turns an event into the next state to store.
let handleEvent (message: obj) (saga: SagaState<unit, TransferState option>) =
    match message, saga.State with
    | (:? Event<AccountEvent> as event), state ->
        // Sender is the ID of the account that stored the event.
        let sender = string event.Sender.Value
        match event.EventDetails, state with
        | TransferSent(id, target, amount), None ->
            let transfer =
                { Id = id; Source = sender; Target = target; Amount = amount }
            StateChangedEvent(Delivering transfer)
        | TransferReceived(id, _, _), Some(Delivering transfer)
            when id = transfer.Id -> StateChangedEvent Completed
        | Rejected _, Some(Delivering transfer) when sender = transfer.Target ->
            StateChangedEvent(Refunding transfer)
        | TransferRefunded(id, _, _), Some(Refunding transfer)
            when id = transfer.Id -> StateChangedEvent Completed
        | _ -> UnhandledEvent
    // No answer in time. The outcome is unknown, so enter the same state again,
    // which sends the command again.
    | :? ExpectationExhausted, Some(Delivering _ | Refunding _ as waiting) ->
        StateChangedEvent waiting
    | _ -> UnhandledEvent
```

<div class="cs-alt"></div>

<!-- sample: accounts/5-transfer-money/csharp Transfer.cs react -->
```csharp
// Turns an event into the next state to store.
public override EventAction<TransferState> HandleEvent(
    object message,
    SagaState<TransferData, FSharpOption<TransferState>> saga) =>
    (message, saga.State) switch
    {
        // Sender is the ID of the account that stored the event.
        (Event<AccountEvent> { Sender: { } sender } stored, var state) =>
            React(stored.EventDetails, sender.Value.ToString(), state),
        // No answer in time. The outcome is unknown, so enter the same
        // state again, which sends the command again.
        (ExpectationExhausted, { Value: Delivering or Refunding } waiting) =>
            StateChanged(waiting.Value),
        _ => Unhandled()
    };

static EventAction<TransferState> React(
    AccountEvent @event, string sender, FSharpOption<TransferState>? state) =>
    (@event, state) switch
    {
        (TransferSent sent, null) => StateChanged(new Delivering(
            new(sent.TransferId, sender, sent.Target, sent.Amount))),
        (TransferReceived received, { Value: Delivering delivering })
            when received.TransferId == delivering.Transfer.Id =>
            StateChanged(new Completed()),
        (Rejected, { Value: Delivering delivering })
            when sender == delivering.Transfer.Target =>
            StateChanged(new Refunding(delivering.Transfer)),
        (TransferRefunded refunded, { Value: Refunding refunding })
            when refunded.TransferId == refunding.Transfer.Id =>
            StateChanged(new Completed()),
        _ => Unhandled()
    };
```

`StateChangedEvent next` stores the next saga state. `UnhandledEvent` rejects an event that does not
belong in the current state. Do not issue commands from this function; state persistence must complete
first. The `ExpectationExhausted` case answers a missed deadline, described
[below](#Give-every-wait-a-deadline).

In C#, `HandleEvent` takes `object` deliberately: a saga also receives other aggregates' reply events
and `ToSelf` timeout payloads. It receives the saga state as an `FSharpOption`, which is `null` before
the first user state exists. The typed `SagaApi.InitSimple` shortcut delivers only the originator's
events, so it cannot express timeouts or coordination across aggregates.

## Map persisted states to commands

`applySideEffects` runs after the state is stored and again after recovery. It returns commands plus
the transition FCQRS should make after issuing them. In C#, `ApplySideEffects` runs only once a user
state exists, so it receives the state directly.

<!-- sample: accounts/5-transfer-money/fsharp Transfer.fs effects -->
```fsharp
// Returns the commands for a stored state. It runs again after recovery; the
// last argument says whether FCQRS is recovering, and this saga ignores it.
let applySideEffects accounts (saga: SagaState<unit, TransferState>) _ =
    // Send now and every 5 seconds; after 30 seconds, tell handleEvent.
    let deliver command =
        let retry = FixedInterval(TimeSpan.FromSeconds 5.)
        expecting (TimeSpan.FromSeconds 30.) retry [ command ], []
    match saga.State with
    | Delivering transfer ->
        let command =
            ReceiveTransfer(transfer.Id, transfer.Source, transfer.Amount)
        deliver (toAggregate accounts transfer.Target command)
    | Refunding transfer ->
        let command =
            RefundTransfer(transfer.Id, transfer.Target, transfer.Amount)
        deliver (toOriginator accounts command)
    | Completed -> StopSaga, []
```

<div class="cs-alt"></div>

<!-- sample: accounts/5-transfer-money/csharp Transfer.cs effects -->
```csharp
// Returns the commands for a stored state. It runs again after recovery;
// `recovering` says whether FCQRS is recovering, and this saga ignores it.
public override SagaSideEffectResult<TransferState> ApplySideEffects(
    SagaState<TransferData, TransferState> saga, bool recovering) =>
    saga.State switch
    {
        Delivering(var (id, source, target, amount)) =>
            Deliver(ToAccount(target, new ReceiveTransfer(id, source, amount))),
        Refunding(var (id, _, target, amount)) =>
            Deliver(ToSource(new RefundTransfer(id, target, amount))),
        Completed => new() { Transition = StopSaga(), Commands = [] }
    };

// Send now and every 5 seconds; after 30 seconds, tell HandleEvent.
static SagaSideEffectResult<TransferState> Deliver(ExecuteCommand command) =>
    new()
    {
        Transition = Stay(),
        Expect = Expectations.Create(
            [command],
            TimeSpan.FromSeconds(30),
            RetrySchedules.Fixed(TimeSpan.FromSeconds(5)))
    };

// SagaCommands takes any object. These helpers take an AccountCommand, so the
// compiler rejects anything else sent to an account.
ExecuteCommand ToAccount(string id, AccountCommand command) =>
    SagaCommands.ToAggregate(accounts, id, command);

ExecuteCommand ToSource(AccountCommand command) =>
    SagaCommands.ToOriginator(accounts, command);
```

The command helpers select a target. C# has the same helpers on `SagaCommands`:

- `toOriginator factory command`: the exact aggregate instance whose event started the saga;
- `toAggregate factory id command`: another aggregate instance selected by id;
- `toActor actorRef command`: an arbitrary actor reference;
- `toSelf command`: a message back to the saga itself, which arrives in `handleEvent`;
- `toOriginatorAfter factory delayMs taskName command`, `toAggregateAfter`, and `toSelfAfter`: delayed
  variants for timeout or retry behaviour. A `toSelfAfter` reminder is a hand-rolled saga timeout:
  enter a state, schedule a wake-up, and let `handleEvent` decide whether it still matters.

The returned saga transition means:

- `Stay`: keep waiting in the current state after sending commands;
- `StayExpecting expectation`: keep waiting, with a deadline and retry schedule for the wait, as
  `expecting` returns (next section);
- `NextState next`: persist another state immediately and run its side effects;
- `StopSaga`: send any returned commands, then complete and passivate. Delayed commands returned with
  `StopSaga` are still delivered, except `toSelfAfter` ones. Those are cancelled with a warning, so a
  completed saga cannot resurrect itself.

## Give every wait a deadline

A waiting state can hang forever for two reasons: the command produced no reply event (the target
decided `IgnoreEvent`), or a message was lost between nodes. `expecting` in F#, or `Expect` with
`Expectations.Create` in C#, declares what the wait expects and what happens when it does not arrive.

The transfer's `deliver` helper sends its command on state entry, sends exactly that command again every
5 seconds while no state transition is persisted, and after 30 seconds delivers an
`ExpectationExhausted` message to `handleEvent`. The handler must answer it with a transition.

The rules that make this safe:

- The deadline is measured from the persisted state-entry time, not from a timer. A restart or crash
  loop re-arms the schedule from the journal and cannot postpone the deadline.
- Re-sent commands must be retry-safe. This is the same contract recovery re-drives already impose;
  the expectation adds no new obligation on the target aggregate.
- A timeout means the outcome is unknown, not failed. The reply may still arrive after the saga
  escalated, so the escalated state's `handleEvent` should decide what a late success means instead
  of ignoring it.
- An unhandled `ExpectationExhausted` (no matching case, or a handler exception) is logged as an
  error and re-delivered one deadline period later. The framework never invents a terminal state.
- `Deadline` and `RetryEvery` are explicit; there are no defaults. Size the deadline above worst-case
  shard handoff plus journal latency, and prefer `Backoff` (which applies jitter) when many sagas can
  wait on the same aggregate.
- One expectation per state. A state waiting on several aggregates with different deadlines should be
  split into one state per wait.

The hand-rolled equivalent, `toSelfAfter` with an attempt counter in the state, remains valid and
shows exactly what the framework automates.

## Exhaustion has two answers: escalate or renew

Escalating to a failure or compensation state is correct while the workflow can still change its
mind. Some waits cannot fail. Once a saga has persisted a decision that other aggregates may already
have acted on, the only correct behaviour is to keep delivering that decision until every participant
has confirmed it.

The transfer is such a wait. The money left Alice's account when her account stored `TransferSent`. A
timeout in `Delivering` does not show whether Bob's account stored `TransferReceived`: the command may
have arrived and only the reply been lost. Refunding on a timeout could then pay the money twice, once
to Bob and once back to Alice. The saga therefore answers exhaustion by entering the same state again,
as the `ExpectationExhausted` case in `handleEvent` does.

A self-transition persists a new state entry, which re-anchors the deadline and re-arms the schedule:
the saga retries forever, but in journaled cycles. Each renewal is a durable event operators can
alert on, so a participant that never recovers shows up as a growing trail of renewals instead of a
silent hang. This is the deliberate blocking behaviour of a decided workflow made observable, not a
bug.

Choose per state:

- **Escalate** when a timeout can still resolve the workflow: report a rejection, compensate, or
  release a hold. A wait before any money moves, such as a fraud check before the source account sends
  the transfer, belongs here.
- **Renew** when the state represents a decision already made. Never abort after the decision;
  alert on repeated renewals and fix the participant instead.

## Declare the start event

`StartOn` answers “which originator event creates one new instance of this saga?” Match only the event
that begins the workflow.

<!-- sample: accounts/5-transfer-money/fsharp Transfer.fs definition -->
```fsharp
// A transfer starts when an account stores TransferSent.
let startsOn (event: Event<AccountEvent>) =
    match event.EventDetails with
    | TransferSent _ -> true
    | _ -> false

let definition accounts =
    { Name = "Transfer"
      InitialData = ()
      Originator = accounts
      HandleEvent = handleEvent
      ApplySideEffects = applySideEffects accounts
      StartOn = startsOn
      Snapshots = Default }
```

<div class="cs-alt"></div>

<!-- sample: accounts/5-transfer-money/csharp Transfer.cs definition -->
```csharp
// A transfer starts when an account stores TransferSent.
public static bool StartsOn(object message) =>
    message is Event<AccountEvent> { EventDetails: TransferSent };
```

`Originator` supplies the aggregate factory used by the starting handshake and by `toOriginator`.
`InitialData` supplies fixed data available to the saga functions. Current workflow progress belongs in
the state-machine cases; use `unit` when no additional fixed data is needed. In C#, the saga class
declares `SagaName`, `InitialData`, and `Originator`, and the start predicate goes to `AddSaga`.

Do not construct `SagaStartingEvent` yourself. FCQRS creates and stores that runtime envelope from the
event accepted by `StartOn`.

> **Motivation:** The start rule lets FCQRS subscribe the saga before the originator publishes the one
> event that begins the workflow. Without that handshake, the new saga could miss its first event.

## Register the saga and starter rules

Register participant aggregates before constructing the saga, then wire every saga start rule once.
Here the accounts are both the originator and the target, so the saga needs one factory:

<!-- sample: accounts/5-transfer-money/fsharp Program.fs register -->
```fsharp
// Register the saga with the accounts it sends commands to, then install its
// start rule. From now on, each stored TransferSent starts one transfer.
let transfers = Fcqrs.saga api (Transfer.definition accounts.Factory)
Fcqrs.wireSagaStarters api [ transfers ]
```

<div class="cs-alt"></div>

<!-- sample: accounts/5-transfer-money/csharp Program.cs register -->
```csharp
// Register the saga with the accounts it sends commands to. The host installs
// its start rule: from now on, each stored TransferSent starts one transfer.
builder.Services.AddFcqrs(connectionString, "accounts")
    .AddAggregate<Account>()
    .AddSaga<Transfer, AccountEvent, TransferData, TransferState>(
        services => new Transfer(services.AggregateFactory<Account>()),
        Transfer.StartsOn)
    .AddTransactionalProjection(options, Statement.Handle);
```

`wireSagaStarters` is not optional. It installs the predicates and the safe-start handshake that
subscribes a new saga before the originator publishes its starting event. The C# host builder wires
the saga starter from all registered sagas at startup, so C# has no counterpart to call.

## Make recovery commands safe

After reconstructing saga state, FCQRS invokes `applySideEffects` with `recovering = true`. Delivery of
the previous command is uncertain, so each waiting state must do one of the following:

- resend an idempotent command;
- query an external operation by a stable idempotency key;
- issue a recovery-specific reconciliation command;
- move to an explicit failed or manual-resolution path.

> **Motivation:** Recovery repeats the next intended action because the journal can prove the stored
> state, but it cannot prove whether an outgoing message crossed the process boundary before failure.

In this example, each account records the IDs of the transfers it has received and refunded. A
repeated `ReceiveTransfer` or `RefundTransfer` for a known ID returns the first answer as a deferred
reply and moves no money. The normal commands are therefore safe to issue again, after recovery or on
each retry. [Transfer money](../tutorial/transfer-money.html) sends a repeated delivery and shows the
result.

Do not return no command merely because `recovering` is true. If the process stopped before delivery,
that leaves the workflow waiting forever. Add a timeout for every event that may never arrive.

Use [Test your domain](test-your-domain.html) to test the
event-to-state and state-to-command functions independently.
