(**
---
title: 2. Withdraw money
category: Tutorial
categoryindex: 2
index: 2
---
*)
(*** hide ***)
#r "nuget: FCQRS, 6.14.0"
#load "../../samples/accounts/2-withdraw-money/fsharp/Account.fs"

(**
# Step 2: Withdraw money

In [step 1](open-an-account.html) the account stored every command it received. This step adds
withdrawals and the rules a bank needs: an account opens once, only an open account takes money,
amounts are positive, and a withdrawal cannot take more than the balance. A command that breaks a rule
gets a reply that says why, and nothing is stored.

<img src="../img/withdraw-money.svg" alt="Three withdrawals reach Alice's account, which holds 70. Withdraw 500 is rejected and not stored. Two withdrawals of 60 arrive together: the account takes them one at a time, stores the first as version 4, and rejects the second because the balance is now 10. The journal holds only the four accepted events." width="900"/>

## The same feature in a CRUD application

A CRUD application reads the balance, checks it in application code, and writes the new balance. Two
withdrawals of 60 from a balance of 70 that arrive together can both pass the check:

```sql
-- Both requests read the balance before either one writes.
SELECT balance FROM accounts WHERE id = 'alice';  -- first: 70
SELECT balance FROM accounts WHERE id = 'alice';  -- second: 70
-- Both check 60 <= 70 in application code, then subtract.
UPDATE accounts SET balance = balance - 60 WHERE id = 'alice';  -- 10
UPDATE accounts SET balance = balance - 60 WHERE id = 'alice';  -- -50
```

Each check was correct when it ran, and the balance still ends at -50. The usual fixes are a row lock
(`SELECT ... FOR UPDATE`), a version column that makes the second update fail, or a conditional
`UPDATE ... WHERE balance >= 60` whose affected-row count says whether the withdrawal happened. Every
code path that changes the balance has to use the same fix.

In FCQRS, every command for Alice's account goes to the same aggregate instance, and the instance
handles one command at a time. The second withdrawal is decided after the first one is stored, against
a balance of 10.

## Name the messages

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

`Withdraw` and `Withdrawn` follow the pattern from step 1: the command asks, the event records.

`Rejected` is the reply to a command the account turns away, and it carries the reason. It is a case
of `AccountEvent` because every reply has that type, but the account never stores it.

## Write the rules

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

`decide` now checks each command against the state before accepting it. The cases are tried from top
to bottom, and the first one that matches decides. `Open` is accepted only while the account has no
owner, and every other command is rejected until it has one.

`decide` can reply in two ways:

- `PersistEvent` stores the event, applies it to the state with `fold`, and replies with it. The
  version goes up by one.
- `DeferEvent` replies with the event without storing it. The version stays the same, and the journal
  never sees the event. The name comes from Akka.NET, the actor library FCQRS runs on.

In C#, `Reject` is a helper like `Store` from step 1. It creates a `DeferEvent` with
`EventActions.Defer`.

FCQRS applies a deferred event with `fold` too, so `fold` returns the state unchanged for `Rejected`.
A change made there would last only until the account is loaded again, because loading folds stored
events only.

Turn a command away with a rejection, not an exception. An exception in `decide` or `fold` does not
reach the caller: FCQRS stops the whole program, for reasons
[When FCQRS stops the process](../concepts/process-termination.html) explains.

## Send the commands

<!-- sample: accounts/2-withdraw-money/fsharp Program.fs send -->
```fsharp
// Each account ID gets its own aggregate instance.
let alice = Fcqrs.aggregateId "alice"

// Send a command and return the account's reply.
let request command =
    accounts.Send (Fcqrs.newCid ()) alice command (fun _ -> true)

// Print a reply. Journaled tells whether FCQRS stored the event.
let show (reply: Event<AccountEvent>) =
    let stored = if reply.Journaled = Some true then "stored" else "not stored"
    let description = describe reply.EventDetails
    printfn $"{description} (version {reply.Version}, {stored})"

let send command = request command |> Async.RunSynchronously |> show

send (Open "Alice")
send (Deposit 100m)
send (Withdraw 30m)
send (Withdraw 500m)
send (Open "Alice")
```

<div class="cs-alt"></div>

<!-- sample: accounts/2-withdraw-money/csharp Program.cs send -->
```csharp
// Sends commands to accounts and returns the event each one replied with.
var accounts = host.Services
    .GetRequiredService<Handler<AccountCommand, AccountEvent>>();
// Each account ID gets its own aggregate instance.
var alice = Values.CreateAggregateId("alice");

// Send a command and return the account's reply.
Task<Event<AccountEvent>> Request(AccountCommand command) =>
    accounts(_ => true, Values.NewCID(), alice, command);

// Print a reply. Journaled tells whether FCQRS stored the event.
void Show(Event<AccountEvent> reply)
{
    var stored = reply.Journaled?.Value == true ? "stored" : "not stored";
    var description = Describe(reply.EventDetails);
    Console.WriteLine($"{description} (version {reply.Version}, {stored})");
}

async Task Send(AccountCommand command) => Show(await Request(command));

await Send(new Open("Alice"));
await Send(new Deposit(100m));
await Send(new Withdraw(30m));
await Send(new Withdraw(500m));
await Send(new Open("Alice"));
```

Startup is the same as in step 1. `request` sends a command and returns the pending reply, so the
next example can send two commands before waiting for either. `send` waits for the reply and prints
it. `Journaled` on a reply says whether FCQRS stored the event.

## Send two withdrawals together

<!-- sample: accounts/2-withdraw-money/fsharp Program.fs together -->
```fsharp
// Two withdrawals of 60 arrive at the same moment.
let replies =
    [ request (Withdraw 60m); request (Withdraw 60m) ]
    |> Async.Parallel
    |> Async.RunSynchronously

// Print the stored one first.
replies
|> Array.sortBy (fun reply -> reply.Journaled <> Some true)
|> Array.iter show
```

<div class="cs-alt"></div>

<!-- sample: accounts/2-withdraw-money/csharp Program.cs together -->
```csharp
// Two withdrawals of 60 arrive at the same moment.
var replies = await Task.WhenAll(
    Request(new Withdraw(60m)), Request(new Withdraw(60m)));

// Print the stored one first.
foreach (var reply in replies.OrderBy(reply => reply.Journaled?.Value != true))
    Show(reply);
```

Both withdrawals are in flight at once, and the balance is 70. The account takes one of them first,
stores `Withdrawn 60`, and then decides the other against a balance of 10. Which one arrives first is
not fixed, so the program prints the stored reply first.

This guarantee covers one account. Commands for different accounts run in parallel, and each account
decides only with its own state. Moving money between two accounts needs a workflow across both, which
step 5 builds.

## Run it

From `samples/accounts`:

```text
dotnet run --project 2-withdraw-money/fsharp
```

<div class="cs-alt" data-fs="text" data-cs="text"></div>

```text
dotnet run --project 2-withdraw-money/csharp
```

```text
Opened for Alice (version 1, stored)
Deposited 100 (version 2, stored)
Withdrew 30 (version 3, stored)
Rejected: Insufficient funds: 70 available (version 3, not stored)
Rejected: The account is already open (version 3, not stored)
Withdrew 60 (version 4, stored)
Rejected: Insufficient funds: 10 available (version 4, not stored)

Journal rows for Account/default-shard/alice:
  1  {"Case":"Opened","owner":"Alice"}
  2  {"Case":"Deposited","amount":100}
  3  {"Case":"Withdrawn","amount":30}
  4  {"Case":"Withdrawn","amount":60}
```

<div class="cs-alt" data-fs="text" data-cs="text"></div>

```text
Opened for Alice (version 1, stored)
Deposited 100 (version 2, stored)
Withdrew 30 (version 3, stored)
Rejected: Insufficient funds: 70 available (version 3, not stored)
Rejected: The account is already open (version 3, not stored)
Withdrew 60 (version 4, stored)
Rejected: Insufficient funds: 10 available (version 4, not stored)

Journal rows for Account/default-shard/alice:
  1  {"$case":"Opened","$value":{"Owner":"Alice"}}
  2  {"$case":"Deposited","$value":{"Amount":100}}
  3  {"$case":"Withdrawn","$value":{"Amount":30}}
  4  {"$case":"Withdrawn","$value":{"Amount":60}}
```

Seven commands produced seven replies and four journal rows. A rejected reply shows the version the
account already had, because nothing was stored.

## Run it again

```text
Rejected: The account is already open (version 4, not stored)
Deposited 100 (version 5, stored)
Withdrew 30 (version 6, stored)
Rejected: Insufficient funds: 80 available (version 6, not stored)
Rejected: The account is already open (version 6, not stored)
Withdrew 60 (version 7, stored)
Rejected: Insufficient funds: 20 available (version 7, not stored)
```

The first `Open` is now rejected. FCQRS loaded Alice's account from its four stored events before
handling the new commands, so the account was already open and held 10. The rules use that loaded
state, and the rejections from the first run were never stored, so none of them were replayed.

## Next

[Step 3: Restart the bank](restart-the-bank.html) looks at loading: when an account loads, how
snapshots shorten loading for a long history, and why `fold` must give the same result every time it
runs.

*)

(*** hide ***)
open FCQRS.Common
open FCQRS.CSharp
open Account

let expect name actual expected =
    if actual <> expected then failwithf "%s: expected %A but got %A" name expected actual

let decideOn state command = decide (TestEnvelope.Command command) state
let holding70 = { Owner = Some "Alice"; Balance = 70m }

expect "withdraw within the balance" (decideOn holding70 (Withdraw 60m)) (PersistEvent(Withdrawn 60m))
expect "overdraft" (decideOn holding70 (Withdraw 500m)) (DeferEvent(Rejected "Insufficient funds: 70 available"))
expect "second open" (decideOn holding70 (Open "Alice")) (DeferEvent(Rejected "The account is already open"))
expect "account not open" (decideOn initial (Deposit 100m)) (DeferEvent(Rejected "The account is not open"))
expect "zero amount" (decideOn holding70 (Withdraw 0m)) (DeferEvent(Rejected "The amount must be positive"))
expect "rejection leaves state" (fold (TestEnvelope.Event(Rejected "any", 3L)) holding70) holding70
printfn "Withdraw money example checked."
