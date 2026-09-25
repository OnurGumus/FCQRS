(**
---
title: 5. Transfer money
category: Tutorial
categoryindex: 2
index: 5
---
*)
(*** hide ***)
#r "nuget: FCQRS, 6.13.0"
#load "../../samples/accounts/5-transfer-money/fsharp/Account.fs"
#load "../../samples/accounts/5-transfer-money/fsharp/Transfer.fs"

(**
# Step 5: Transfer money

Alice sends 30 to Bob. Each account decides only with its own state ([step 2](withdraw-money.html)),
so no account can move money into another. A **saga** coordinates the transfer: it reacts to an
event from one account, stores how far the transfer has got, and sends commands to the accounts
involved. When the target account cannot take the money, the saga gives it back to the sender.

<img src="../img/transfer-money.svg" alt="Alice's account stores TransferSent, which starts a transfer saga in the Delivering state. The saga sends ReceiveTransfer to Bob, who stores TransferReceived, and the saga completes. For a transfer to Carol, who has no account, the target rejects ReceiveTransfer, the saga moves to Refunding and sends RefundTransfer to Alice, who stores TransferRefunded, and the saga completes." width="900"/>

## The same feature in a CRUD application

A CRUD application moves money between two rows in one database transaction:

```sql
BEGIN;
UPDATE accounts SET balance = balance - 30 WHERE id = 'alice';
UPDATE accounts SET balance = balance + 30 WHERE id = 'bob';
COMMIT;
```

That works only while both rows live in one database. When the target account belongs to another
service or another bank, the debit and the credit are separate operations, and something has to
finish or undo the transfer when the second half fails, including after a restart. In FCQRS, every
account is its own aggregate and stores its own events, so the same holds within one application. The
saga is the part that finishes or undoes the transfer, and it stores its progress so a restart does
not lose a transfer halfway.

## Add transfer commands and events

<!-- sample: accounts/5-transfer-money/fsharp Account.fs messages -->
```fsharp
module Account

open FCQRS.Common

// What a caller, or the transfer saga, can ask an account to do.
type AccountCommand =
    | Open of owner: string
    | Deposit of amount: decimal
    | Withdraw of amount: decimal
    | SendTransfer of transferId: string * target: string * amount: decimal
    | ReceiveTransfer of transferId: string * source: string * amount: decimal
    | RefundTransfer of transferId: string * target: string * amount: decimal

// What the account replies. Rejected is a reply only: it is never stored.
type AccountEvent =
    | Opened of owner: string
    | Deposited of amount: decimal
    | Withdrawn of amount: decimal
    | TransferSent of transferId: string * target: string * amount: decimal
    | TransferReceived of transferId: string * source: string * amount: decimal
    | TransferRefunded of transferId: string * target: string * amount: decimal
    | Rejected of reason: string

// What the account knows now. The sets hold the IDs of transfers it has
// handled, so a repeated transfer command moves no money twice.
type AccountState =
    { Owner: string option
      Balance: decimal
      Sent: Set<string>
      Received: Set<string>
      Refunded: Set<string> }

// The state before the account's first event.
let initial =
    { Owner = None
      Balance = 0m
      Sent = Set.empty
      Received = Set.empty
      Refunded = Set.empty }
```

<div class="cs-alt"></div>

<!-- sample: accounts/5-transfer-money/csharp Account.cs messages -->
```csharp
// What a caller, or the transfer saga, can ask an account to do.
public union AccountCommand(
    Open, Deposit, Withdraw, SendTransfer, ReceiveTransfer, RefundTransfer);
public sealed record Open(string Owner);
public sealed record Deposit(decimal Amount);
public sealed record Withdraw(decimal Amount);
public sealed record SendTransfer(
    string TransferId, string Target, decimal Amount);
public sealed record ReceiveTransfer(
    string TransferId, string Source, decimal Amount);
public sealed record RefundTransfer(
    string TransferId, string Target, decimal Amount);

// What the account replies. Rejected is a reply only: it is never stored.
public union AccountEvent(
    Opened, Deposited, Withdrawn,
    TransferSent, TransferReceived, TransferRefunded, Rejected);
public sealed record Opened(string Owner);
public sealed record Deposited(decimal Amount);
public sealed record Withdrawn(decimal Amount);
public sealed record TransferSent(
    string TransferId, string Target, decimal Amount);
public sealed record TransferReceived(
    string TransferId, string Source, decimal Amount);
public sealed record TransferRefunded(
    string TransferId, string Target, decimal Amount);
public sealed record Rejected(string Reason);

// What the account knows now. The sets hold the IDs of transfers it has
// handled, so a repeated transfer command moves no money twice.
public sealed record AccountState(
    string? Owner,
    decimal Balance,
    ImmutableHashSet<string> Sent,
    ImmutableHashSet<string> Received,
    ImmutableHashSet<string> Refunded);
```

A transfer has three messages, each named with a **transfer ID** that the caller chooses:

- `SendTransfer` asks the source account to send money. It stores `TransferSent` and debits itself.
- `ReceiveTransfer` asks the target account to take the money. It stores `TransferReceived`.
- `RefundTransfer` asks the source account to take back money the target did not accept. It stores
  `TransferRefunded`.

The state now remembers which transfer IDs each account has sent, received, and refunded.

## Make each transfer command safe to repeat

<!-- sample: accounts/5-transfer-money/fsharp Account.fs rules -->
```fsharp
// Chooses what to do with a command, based on the current state.
let decide (command: Command<AccountCommand>) (state: AccountState) =
    match command.CommandDetails, state.Owner with
    | Open _, Some _ -> DeferEvent(Rejected "The account is already open")
    | Open owner, None -> PersistEvent(Opened owner)
    | _, None -> DeferEvent(Rejected "The account is not open")
    | (Deposit amount | Withdraw amount | SendTransfer(_, _, amount)), _
        when amount <= 0m -> DeferEvent(Rejected "The amount must be positive")
    | Deposit amount, _ -> PersistEvent(Deposited amount)
    | (Withdraw amount | SendTransfer(_, _, amount)), _
        when amount > state.Balance ->
        DeferEvent(Rejected $"Insufficient funds: {state.Balance} available")
    | Withdraw amount, _ -> PersistEvent(Withdrawn amount)
    | SendTransfer(id, _, _), _ when state.Sent.Contains id ->
        DeferEvent(Rejected $"Transfer {id} was already sent")
    | SendTransfer(id, target, amount), _ ->
        PersistEvent(TransferSent(id, target, amount))
    // A repeated delivery gets the first answer; no money moves.
    | ReceiveTransfer(id, source, amount), _ when state.Received.Contains id ->
        DeferEvent(TransferReceived(id, source, amount))
    | ReceiveTransfer(id, source, amount), _ ->
        PersistEvent(TransferReceived(id, source, amount))
    | RefundTransfer(id, target, amount), _ when state.Refunded.Contains id ->
        DeferEvent(TransferRefunded(id, target, amount))
    | RefundTransfer(id, target, amount), _ ->
        PersistEvent(TransferRefunded(id, target, amount))

// Applies one event. A rejection or a repeated reply changes nothing.
let fold (event: Event<AccountEvent>) (state: AccountState) =
    match event.EventDetails with
    | Opened owner -> { state with Owner = Some owner }
    | Deposited amount -> { state with Balance = state.Balance + amount }
    | Withdrawn amount -> { state with Balance = state.Balance - amount }
    | TransferSent(id, _, amount) ->
        { state with
            Balance = state.Balance - amount
            Sent = state.Sent.Add id }
    // FCQRS folds a repeated reply too; a known ID changes nothing.
    | TransferReceived(id, _, _) when state.Received.Contains id -> state
    | TransferReceived(id, _, amount) ->
        { state with
            Balance = state.Balance + amount
            Received = state.Received.Add id }
    | TransferRefunded(id, _, _) when state.Refunded.Contains id -> state
    | TransferRefunded(id, _, amount) ->
        { state with
            Balance = state.Balance + amount
            Refunded = state.Refunded.Add id }
    | Rejected _ -> state
```

<div class="cs-alt"></div>

<!-- sample: accounts/5-transfer-money/csharp Account.cs rules -->
```csharp
public sealed class Account
    : Aggregate<AccountState, AccountCommand, AccountEvent>
{
    // The name stored with every event of this aggregate.
    public override string EntityName => "Account";
    // The state before the account's first event.
    public override AccountState InitialState => new(null, 0m, [], [], []);

    // Chooses what to do with a command, based on the current state.
    public override EventAction<AccountEvent> HandleCommand(
        Command<AccountCommand> command, AccountState state) =>
        (command.CommandDetails, state.Owner) switch
        {
            (Open, not null) => Reject("The account is already open"),
            (Open open, null) => Store(new Opened(open.Owner)),
            (_, null) => Reject("The account is not open"),
            (Deposit { Amount: <= 0m } or Withdraw { Amount: <= 0m }
                or SendTransfer { Amount: <= 0m }, _) =>
                Reject("The amount must be positive"),
            (Deposit deposit, _) => Store(new Deposited(deposit.Amount)),
            (Withdraw withdraw, _) when withdraw.Amount > state.Balance =>
                Reject($"Insufficient funds: {state.Balance} available"),
            (SendTransfer send, _) when send.Amount > state.Balance =>
                Reject($"Insufficient funds: {state.Balance} available"),
            (Withdraw withdraw, _) => Store(new Withdrawn(withdraw.Amount)),
            (SendTransfer send, _) when state.Sent.Contains(send.TransferId) =>
                Reject($"Transfer {send.TransferId} was already sent"),
            (SendTransfer send, _) => Store(
                new TransferSent(send.TransferId, send.Target, send.Amount)),
            // A repeated delivery gets the first answer; no money moves.
            (ReceiveTransfer receive, _)
                when state.Received.Contains(receive.TransferId) =>
                Repeat(new TransferReceived(
                    receive.TransferId, receive.Source, receive.Amount)),
            (ReceiveTransfer receive, _) =>
                Store(new TransferReceived(
                    receive.TransferId, receive.Source, receive.Amount)),
            (RefundTransfer refund, _)
                when state.Refunded.Contains(refund.TransferId) =>
                Repeat(new TransferRefunded(
                    refund.TransferId, refund.Target, refund.Amount)),
            (RefundTransfer refund, _) =>
                Store(new TransferRefunded(
                    refund.TransferId, refund.Target, refund.Amount))
        };

    // Applies one event. A rejection or a repeated reply changes nothing.
    public override AccountState ApplyEvent(
        Event<AccountEvent> stored, AccountState state) =>
        stored.EventDetails switch
        {
            Opened opened => state with { Owner = opened.Owner },
            Deposited deposited =>
                state with { Balance = state.Balance + deposited.Amount },
            Withdrawn withdrawn =>
                state with { Balance = state.Balance - withdrawn.Amount },
            TransferSent sent => state with
            {
                Balance = state.Balance - sent.Amount,
                Sent = state.Sent.Add(sent.TransferId)
            },
            // FCQRS folds a repeated reply too; a known ID changes nothing.
            TransferReceived received
                when state.Received.Contains(received.TransferId) => state,
            TransferReceived received => state with
            {
                Balance = state.Balance + received.Amount,
                Received = state.Received.Add(received.TransferId)
            },
            TransferRefunded refunded
                when state.Refunded.Contains(refunded.TransferId) => state,
            TransferRefunded refunded => state with
            {
                Balance = state.Balance + refunded.Amount,
                Refunded = state.Refunded.Add(refunded.TransferId)
            },
            Rejected => state
        };

    // Stores the event and replies with it.
    static EventAction<AccountEvent> Store(AccountEvent @event) =>
        EventActions.Persist(@event);

    // Replies without storing anything.
    static EventAction<AccountEvent> Reject(string reason) =>
        EventActions.Defer<AccountEvent>(new Rejected(reason));

    // Replies with an earlier answer again, without storing it.
    static EventAction<AccountEvent> Repeat(AccountEvent @event) =>
        EventActions.Defer(@event);
}
```

The rules use the transfer IDs in two ways:

- A second `SendTransfer` with the same ID is rejected. A caller that retries a request, for example
  after a timeout, cannot send the money twice.
- A second `ReceiveTransfer` or `RefundTransfer` with the same ID gets the first answer again, as a
  reply that is not stored. The saga can deliver these commands more than once, as the next sections
  show, and the money must move only once.

FCQRS applies a repeated reply with `fold` too, as it does every deferred reply, so `fold` checks the
ID and leaves the state unchanged. An account that is not open rejects `ReceiveTransfer`; that
rejection is what sends a transfer back.

## Describe where a transfer can be

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

The saga's state is what it has stored about one transfer. `TransferDetails` holds what the transfer
moves; both waiting states carry it, because each needs it for its next command. Write the states as
a table before the code:

| State | Event | Next state | Command sent |
|---|---|---|---|
| none | `TransferSent` | `Delivering` | `ReceiveTransfer` to the target |
| `Delivering` | `TransferReceived` | `Completed` | none; the saga stops |
| `Delivering` | `Rejected` from the target | `Refunding` | `RefundTransfer` to the source |
| `Refunding` | `TransferRefunded` | `Completed` | none; the saga stops |

## React to events

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
// Turns the event that started the transfer into its first state.
public override EventAction<TransferState> Start(
    object message, TransferData data) =>
    message is Event<AccountEvent>
    {
        EventDetails: TransferSent sent, Sender: { } sender
    }
        ? StateChanged(new Delivering(
            new(sent.TransferId, sender.Value.ToString(), sent.Target,
                sent.Amount)))
        : Unhandled();

// Turns a later event into the next state to store.
public override EventAction<TransferState> HandleEvent(
    object message, SagaState<TransferData, TransferState> saga) =>
    (message, saga.State) switch
    {
        // Sender is the ID of the account that stored the event.
        (Event<AccountEvent> { Sender: { } sender } stored, var state) =>
            React(stored.EventDetails, sender.Value.ToString(), state),
        // No answer in time. The outcome is unknown, so enter the same
        // state again, which sends the command again.
        (ExpectationExhausted, Delivering or Refunding) =>
            StateChanged(saga.State),
        _ => Unhandled()
    };

static EventAction<TransferState> React(
    AccountEvent @event, string sender, TransferState state) =>
    (@event, state) switch
    {
        (TransferReceived received, Delivering delivering)
            when received.TransferId == delivering.Transfer.Id =>
            StateChanged(new Completed()),
        (Rejected, Delivering delivering)
            when sender == delivering.Transfer.Target =>
            StateChanged(new Refunding(delivering.Transfer)),
        (TransferRefunded refunded, Refunding refunding)
            when refunded.TransferId == refunding.Transfer.Id =>
            StateChanged(new Completed()),
        _ => Unhandled()
    };
```

`handleEvent` receives events from every account that takes part in the transfer, as `obj`,
together with the stored state. Before the first state is stored, that state is `None`. It returns the
next state to store, or `UnhandledEvent` for an event that does not belong in the current state. It
sends no commands: FCQRS stores the state first.

C# splits the two cases. `Start` receives the event that started the transfer, before the saga has a
state, and returns the first state. `HandleEvent` receives the later events with the stored state.

The events reach this saga through the transfer's **correlation ID** ([step 4](show-a-statement.html)).
The saga's commands carry the correlation ID of the `SendTransfer` request, and so do the replies the
accounts send back. `Sender` identifies the account that replied, so a rejection counts only when it
comes from the target.

## Send commands for each state

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

`applySideEffects` (`ApplySideEffects` in C#) runs after FCQRS stores a state. `toAggregate` sends a
command to an account by ID; `toOriginator` sends one to the account whose event started the saga.
`Completed` returns `StopSaga`, which ends the saga.

The function runs again when FCQRS recovers the saga after a restart. The journal shows the stored
state, but not whether the command left the process before it stopped, so the saga sends it again.
This is why `ReceiveTransfer` and `RefundTransfer` must be safe to repeat.

`expecting` (`Expectations.Create` in C#) gives each wait a deadline. FCQRS sends the command, sends
it again every 5 seconds until a new state is stored, and after 30 seconds passes
`ExpectationExhausted` to `handleEvent`. The deadline counts from the moment the state was stored, so
a restart does not extend it.

`ExpectationExhausted` means that no answer came, not that the transfer failed: Bob may have taken the
money. The money is in flight, so the transfer must not give up. `handleEvent` enters the same state
again, which stores a new state and starts another 30 seconds. Every round is stored, so a stuck
transfer is visible in the saga's journal.

In C#, `SagaCommands` takes a command as `object`. The helpers take an `AccountCommand`, so the
compiler rejects anything that is not an account command.

## Register the saga

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
public override bool StartsOn(Event<AccountEvent> stored) =>
    stored.EventDetails is TransferSent;
```

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
    .AddSaga(services => new Transfer(services.AggregateFactory<Account>()))
    .AddTransactionalProjection(options, Statement.Handle);
```

`startsOn` (`StartsOn` in C#) selects the event that begins a transfer. FCQRS starts one saga for each
stored `TransferSent`. A rejected `SendTransfer` is not stored, so it starts nothing. `Transfer` is
the saga's stored name, like an aggregate's `EntityName`.

`Fcqrs.wireSagaStarters` installs the start rules, and must run after every saga is registered. It
also makes Alice's account wait, before it publishes `TransferSent`, until the new saga listens for the
transfer's correlation ID, so the saga cannot miss its first event. The C# host installs the rules
itself when it starts.

## Send transfers and wait for them

<!-- sample: accounts/5-transfer-money/fsharp Program.fs transfer -->
```fsharp
// A transfer ends when the target stores the money or the source gets it back.
let finished (message: IMessageWithCID) =
    match message with
    | :? Event<AccountEvent> as event ->
        match event.EventDetails with
        | TransferReceived _ | TransferRefunded _ -> true
        | _ -> false
    | _ -> false

// Ask Alice's account to send money, and wait until the saga has finished.
let transfer id target amount =
    let cid = Fcqrs.newCid ()
    // Subscribe first: the saga can finish before the reply arrives.
    use outcome = statement.Subscribe(cid, finished, 1)
    let command = SendTransfer(id, target, amount)
    let reply =
        accounts.Send cid alice command (fun _ -> true)
        |> Async.RunSynchronously
    show reply
    if reply.Journaled = Some true then
        outcome.Task.WaitAsync(TimeSpan.FromSeconds 30.).Wait()

transfer "t1" "bob" 30m
// Carol has no account, so this transfer comes back.
transfer "t2" "carol" 20m
```

<div class="cs-alt"></div>

<!-- sample: accounts/5-transfer-money/csharp Program.cs transfer -->
```csharp
// A transfer ends when the target stores the money or the source gets it back.
static bool Finished(Data.IMessageWithCID message) =>
    message is Event<AccountEvent>
    {
        EventDetails: TransferReceived or TransferRefunded
    };

// Ask Alice's account to send money, and wait until the saga has finished.
async Task SendTransfer(string id, string target, decimal amount)
{
    var cid = Values.NewCID();
    // Subscribe first: the saga can finish before the reply arrives.
    using var outcome = statement.SubscribeForFirst(cid, Finished);
    var reply = await accounts(
        _ => true, cid, alice, new SendTransfer(id, target, amount));
    Show(reply);
    if (reply.Journaled?.Value == true)
        await outcome.Task.WaitAsync(TimeSpan.FromSeconds(30));
}

await SendTransfer("t1", "bob", 30m);
// Carol has no account, so this transfer comes back.
await SendTransfer("t2", "carol", 20m);
```

The reply to `SendTransfer` arrives when Alice's account has stored `TransferSent`, which is before
the transfer has finished. The program waits for the transfer's last event instead: `TransferReceived`
from the target, or `TransferRefunded` from Alice. It subscribes to the correlation ID on the
statement projection from step 4, with a filter for those two events, before it sends.

## Deliver a transfer twice

<!-- sample: accounts/5-transfer-money/fsharp Program.fs repeat -->
```fsharp
// After a restart, a saga sends its last command again. Do the same by hand:
send bob (ReceiveTransfer("t1", "alice", 30m))
```

<div class="cs-alt"></div>

<!-- sample: accounts/5-transfer-money/csharp Program.cs repeat -->
```csharp
// After a restart, a saga sends its last command again. Do the same by hand:
await Send(bob, new ReceiveTransfer("t1", "alice", 30m));
```

## Run it

From `samples/accounts`:

```text
dotnet run --project 5-transfer-money/fsharp
```

<div class="cs-alt" data-fs="text" data-cs="text"></div>

```text
dotnet run --project 5-transfer-money/csharp
```

Both programs print:

```text
Opened for Alice (version 1, stored)
Deposited 100 (version 2, stored)
Opened for Bob (version 1, stored)
Sent 30 to bob (t1) (version 3, stored)
Sent 20 to carol (t2) (version 4, stored)
Received 30 from alice (t1) (version 2, not stored)

Statement for alice:
  version  entry                    amount  balance
        1  Opened for Alice              0        0
        2  Deposit                     100      100
        3  Transfer t1 to bob          -30       70
        4  Transfer t2 to carol        -20       50
        5  Refund of transfer t2        20       70

Statement for bob:
  version  entry                    amount  balance
        1  Opened for Bob                0        0
        2  Transfer t1 from alice       30       30
```

Transfer `t1` moved 30 from Alice to Bob. Transfer `t2` came back: Carol has no account, so her
account rejected `ReceiveTransfer`, and the saga refunded Alice, which is row 5 of her statement. The
second delivery of `t1` got the first answer again without storing anything, and Bob's statement has
one row for it.

Between rows 4 and 5, Alice's statement shows 20 that has left her account and reached no one. A saga
is not a transaction: a query during a transfer sees the money in flight.

## Run it again

```text
Rejected: The account is already open (version 5, not stored)
Deposited 100 (version 6, stored)
Rejected: The account is already open (version 2, not stored)
Rejected: Transfer t1 was already sent (version 6, not stored)
Rejected: Transfer t2 was already sent (version 6, not stored)
Received 30 from alice (t1) (version 2, not stored)
```

The program sends the same requests again. Only the deposit is new: the transfer IDs stop `t1` and
`t2` from sending money a second time, and the statements gain one row, Alice's deposit.

## What remains the application's job

- **Repeatable commands.** Every command a saga sends can arrive more than once. Give it an ID the
  receiver remembers, as the transfer ID does here.
- **A deadline for every wait.** A state that waits for an answer needs `expecting`, or its own timer,
  so a lost message cannot leave it waiting forever.
- **External systems.** Stored saga states do not make a call to another system happen exactly once.
  A call to a payment provider needs its own idempotency key, timeout, retry policy, and a way to undo
  or escalate. [Sagas](../concepts/sagas.html) covers recovery in detail, and
  [Write a saga](../how-to/write-a-saga.html) covers the other command targets and timers.

## Next

[Step 6: Add a memo](add-a-memo.html) adds a memo to transfers. `TransferSent` events are already
stored without one, so the step shows how to change an event that the journal already holds.

*)

(*** hide ***)
open FCQRS.Common
open FCQRS.CSharp
open Account
open Transfer

let expect name actual expected =
    if actual <> expected then failwithf "%s: expected %A but got %A" name expected actual

let from account event =
    { TestEnvelope.Event(event, 1L) with Sender = Some(Values.CreateAggregateId account) }
let saga state = { Data = (); State = state }
let details id target amount = { Id = id; Source = "alice"; Target = target; Amount = amount }
let delivering = Delivering(details "t2" "carol" 20m)

expect "start"
    (handleEvent (box (from "alice" (TransferSent("t1", "bob", 30m)))) (saga None))
    (StateChangedEvent(Delivering(details "t1" "bob" 30m)))
expect "refund on the target's rejection"
    (handleEvent (box (from "carol" (Rejected "The account is not open"))) (saga (Some delivering)))
    (StateChangedEvent(Refunding(details "t2" "carol" 20m)))
expect "ignore a rejection from another account"
    (handleEvent (box (from "alice" (Rejected "Insufficient funds"))) (saga (Some delivering)))
    UnhandledEvent

let opened = fold (TestEnvelope.Event(Opened "Bob", 1L)) initial
let received = fold (TestEnvelope.Event(TransferReceived("t1", "alice", 30m), 2L)) opened
expect "a repeated delivery replies without storing"
    (decide (TestEnvelope.Command(ReceiveTransfer("t1", "alice", 30m))) received)
    (DeferEvent(TransferReceived("t1", "alice", 30m)))
expect "a repeated reply leaves the state"
    (fold (TestEnvelope.Event(TransferReceived("t1", "alice", 30m), 2L)) received)
    received
printfn "Transfer money example checked."
