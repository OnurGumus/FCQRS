(**
---
title: 1. Open an account
category: Tutorial
categoryindex: 2
index: 1
---
*)
(*** hide ***)
#r "nuget: FCQRS, 6.8.0"
#load "../../samples/accounts/1-open-an-account/fsharp/Account.fs"

(**
# Step 1: Open an account

The tutorial builds a small bank one step at a time: accounts, deposits and withdrawals, a statement,
and transfers between accounts. [The overview](../overview.html) explains why FCQRS splits an application
into aggregates and read models. In this step you open Alice's account, deposit money twice, and look at
what FCQRS stored.

<img src="../img/open-an-account.svg" alt="Three commands enter Alice's account, which decides each one against its current state. The stored events form the journal, and folding them produces the state: owner Alice, balance 150." width="900"/>

## The same feature in a CRUD application

A CRUD application keeps one row per account and changes it in place:

```sql
-- Open Alice's account with an empty balance.
INSERT INTO accounts (id, owner, balance) VALUES ('alice', 'Alice', 0);
-- Each deposit overwrites the balance.
UPDATE accounts SET balance = balance + 100 WHERE id = 'alice';
UPDATE accounts SET balance = balance + 50 WHERE id = 'alice';
```

The row ends at 150 and no longer shows how it got there. A bank statement needs that history, so the
application also inserts into a transactions table in the same database transaction.

FCQRS stores the history itself. Every change becomes a row that is never updated, and the balance is
computed from those rows.

## Name the messages

<!-- sample: accounts/1-open-an-account/fsharp Account.fs messages -->
```fsharp
module Account

open FCQRS.Common

// What a caller can ask an account to do.
type AccountCommand =
    | Open of owner: string
    | Deposit of amount: decimal

// What the account records when it accepts a command.
type AccountEvent =
    | Opened of owner: string
    | Deposited of amount: decimal

// What the account knows now, rebuilt from its events.
type AccountState = { Owner: string option; Balance: decimal }

// The state before the account's first event.
let initial = { Owner = None; Balance = 0m }
```

<div class="cs-alt"></div>

<!-- sample: accounts/1-open-an-account/csharp Account.cs messages -->
```csharp
// What a caller can ask an account to do.
public union AccountCommand(Open, Deposit);
public sealed record Open(string Owner);
public sealed record Deposit(decimal Amount);

// What the account records when it accepts a command.
public union AccountEvent(Opened, Deposited);
public sealed record Opened(string Owner);
public sealed record Deposited(decimal Amount);

// What the account knows now, rebuilt from its events.
public sealed record AccountState(string? Owner = null, decimal Balance = 0m);
```

- A **command** asks for a change: `Open "Alice"`, `Deposit 100m`. It is named as an instruction,
  because the account has not accepted it yet.
- An **event** records a change that happened: `Opened "Alice"`, `Deposited 100m`. It is named in the
  past tense.
- The **state** holds what the account needs to know now: its owner and its balance.

In C#, `union` (new in C# 15) declares a closed set of cases, as an F# union does: an `AccountCommand`
is an `Open` or a `Deposit`. A `switch` that misses a case gets a compiler warning, as an incomplete
`match` does in F#.

## Write the rules

<!-- sample: accounts/1-open-an-account/fsharp Account.fs rules -->
```fsharp
// Chooses what to do with a command: here, always store an event.
let decide (command: Command<AccountCommand>) (state: AccountState) =
    match command.CommandDetails with
    | Open owner -> PersistEvent(Opened owner)
    | Deposit amount -> PersistEvent(Deposited amount)

// Applies one stored event to the state.
let fold (event: Event<AccountEvent>) (state: AccountState) =
    match event.EventDetails with
    | Opened owner -> { state with Owner = Some owner }
    | Deposited amount -> { state with Balance = state.Balance + amount }
```

<div class="cs-alt"></div>

<!-- sample: accounts/1-open-an-account/csharp Account.cs rules -->
```csharp
public sealed class Account
    : Aggregate<AccountState, AccountCommand, AccountEvent>
{
    // The name stored with every event of this aggregate.
    public override string EntityName => "Account";
    // The state before the account's first event.
    public override AccountState InitialState => new();

    // Chooses what to do with a command: here, always store an event.
    public override EventAction<AccountEvent> HandleCommand(
        Command<AccountCommand> command, AccountState state) =>
        command.CommandDetails switch
        {
            Open open => Store(new Opened(open.Owner)),
            Deposit deposit => Store(new Deposited(deposit.Amount))
        };

    // Applies one stored event to the state.
    public override AccountState ApplyEvent(
        Event<AccountEvent> stored, AccountState state) =>
        stored.EventDetails switch
        {
            Opened opened => state with { Owner = opened.Owner },
            Deposited deposited =>
                state with { Balance = state.Balance + deposited.Amount }
        };

    // Stores the event and replies with it.
    static EventAction<AccountEvent> Store(AccountEvent @event) =>
        EventActions.Persist(@event);
}
```

`decide` (`HandleCommand` in C#) receives a command and the current state, and returns what to do.
`PersistEvent` means: store this event. In C#, the `Store` helper creates it with `EventActions.Persist`.
In this step every command is accepted. Step 2 adds rules that turn some commands away.

`fold` (`ApplyEvent` in C#) applies one stored event to the state. For Alice's three events:

```text
start                  Owner: none    Balance: 0
fold Opened "Alice"    Owner: Alice   Balance: 0
fold Deposited 100     Owner: Alice   Balance: 100
fold Deposited 50      Owner: Alice   Balance: 150
```

FCQRS calls `fold` right after it stores an event. When it loads an account, for example after a
restart, it calls `fold` for every stored event of that account in order. That is how the balance
exists without a balance column.

The state, `decide`, and `fold` together form an **aggregate**. FCQRS keeps one aggregate instance per
account ID, and each instance handles one command at a time.

## Start FCQRS and send commands

<!-- sample: accounts/1-open-an-account/fsharp Program.fs startup -->
```fsharp
// FCQRS logs through Microsoft.Extensions.Logging; this sample stays quiet.
let logging = LoggerFactory.Create(fun _ -> ())
// FCQRS reads optional settings from IConfiguration; this sample sets none.
let configuration = ConfigurationBuilder().Build()
// Events go to a SQLite file next to the program.
let connection = Fcqrs.connect DBType.Sqlite $"Data Source={database};"
let api = Fcqrs.actor configuration logging (Some connection) "accounts"

// Register the account rules; `accounts` sends commands to them.
let accounts =
    Fcqrs.aggregate api
        { Name = "Account"
          Initial = initial
          Decide = decide
          Fold = fold
          Snapshots = Default
          Passivation = PassivationPolicy.Default }

// Finish startup. This program has no sagas yet.
Fcqrs.wireSagaStarters api []
```

<div class="cs-alt"></div>

<!-- sample: accounts/1-open-an-account/csharp Program.cs startup -->
```csharp
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
// Events go to a SQLite file next to the program; register the account rules.
builder.Services.AddFcqrs($"Data Source={database};", "accounts")
    .AddAggregate<Account>();
using var host = builder.Build();
await host.StartAsync();
```

FCQRS stores events in a SQLite file, `accounts.db`. The aggregate is registered under the name
`Account`, which becomes part of every row it stores. In F#, `Fcqrs.aggregate` returns `accounts`, the
handle you send commands with. `Snapshots` and `Passivation` keep their defaults until step 3, and
`wireSagaStarters` completes startup; step 5 gives it a saga. In C#, `AddFcqrs` and `AddAggregate` do
the same inside the .NET host.

<!-- sample: accounts/1-open-an-account/fsharp Program.fs send -->
```fsharp
// Each account ID gets its own aggregate instance.
let alice = Fcqrs.aggregateId "alice"

// Send a command, wait for the stored event, and print it.
let send command =
    let reply =
        accounts.Send (Fcqrs.newCid ()) alice command (fun _ -> true)
        |> Async.RunSynchronously
    let description = describe reply.EventDetails
    printfn $"{description} (version {reply.Version})"

send (Open "Alice")
send (Deposit 100m)
send (Deposit 50m)
```

<div class="cs-alt"></div>

<!-- sample: accounts/1-open-an-account/csharp Program.cs send -->
```csharp
// Sends commands to accounts and returns the event each one stored.
var accounts = host.Services
    .GetRequiredService<Handler<AccountCommand, AccountEvent>>();
// Each account ID gets its own aggregate instance.
var alice = Values.CreateAggregateId("alice");

// Send a command, wait for the stored event, and print it.
async Task Send(AccountCommand command)
{
    var reply = await accounts(_ => true, Values.NewCID(), alice, command);
    var description = Describe(reply.EventDetails);
    Console.WriteLine($"{description} (version {reply.Version})");
}

await Send(new Open("Alice"));
await Send(new Deposit(100m));
await Send(new Deposit(50m));
```

`alice` selects Alice's account. Sending waits for the account's reply, which is the event it stored,
and prints it with its **version**: the number of events the account has stored so far. Two arguments
come back in [step 4](show-a-statement.html): `Fcqrs.newCid ()` / `Values.NewCID()` labels the request, and `fun _ -> true` /
`_ => true` accepts any reply.

## Run it

With the **.NET 11 SDK** and Git installed:

```text
git clone https://github.com/OnurGumus/FCQRS.git
cd FCQRS/samples/accounts
dotnet run --project 1-open-an-account/fsharp
```

<div class="cs-alt" data-fs="text" data-cs="text"></div>

```text
git clone https://github.com/OnurGumus/FCQRS.git
cd FCQRS/samples/accounts
dotnet run --project 1-open-an-account/csharp
```

Run the tutorial's programs from `samples/accounts`. Its `global.json` selects the .NET 11 SDK, which
the C# programs need for C# 15. At the time of writing, that SDK is a release candidate.

```text
Opened for Alice (version 1)
Deposited 100 (version 2)
Deposited 50 (version 3)

Journal rows for Account/default-shard/alice:
  1  {"Case":"Opened","owner":"Alice"}
  2  {"Case":"Deposited","amount":100}
  3  {"Case":"Deposited","amount":50}
```

<div class="cs-alt" data-fs="text" data-cs="text"></div>

```text
Opened for Alice (version 1)
Deposited 100 (version 2)
Deposited 50 (version 3)

Journal rows for Account/default-shard/alice:
  1  {"$case":"Opened","$value":{"Owner":"Alice"}}
  2  {"$case":"Deposited","$value":{"Amount":100}}
  3  {"$case":"Deposited","$value":{"Amount":50}}
```

The first three lines are the replies. The rest is the **journal**: the database table FCQRS stores
events in. It has one row per event, in the order the events happened, and rows are only ever added.
`Account/default-shard/alice` identifies Alice's account in the journal. Each row holds an event's
version and the event as stored. A stored event names its case, such as `Opened`, so the names of
event cases are part of what FCQRS stores. The program reads the table only to show it to you. Applications read events through projections,
which step 4 introduces.

The journal has no balance. The balance, 150, is in the account's state, computed by `fold`.

## Run it again

```text
Opened for Alice (version 4)
Deposited 100 (version 5)
Deposited 50 (version 6)
```

The versions continue at 4. Before handling the new commands, FCQRS loaded Alice's account by folding
its three stored events, so the account started this run with a balance of 150. The journal now has six
rows, including a second `Opened`: nothing stops an account from opening twice yet.

## Next

[Step 2: Withdraw money](withdraw-money.html) adds rules to `decide`: an account opens once, only an
open account takes money, and a withdrawal cannot overdraw the account.

*)

(*** hide ***)
open FCQRS.Common
open FCQRS.CSharp
open Account

let expect name actual expected =
    if actual <> expected then failwithf "%s: expected %A but got %A" name expected actual

let replay events =
    events |> List.fold (fun state (event, version) -> fold (TestEnvelope.Event(event, version)) state) initial

expect "open" (decide (TestEnvelope.Command(Open "Alice")) initial) (PersistEvent(Opened "Alice"))
expect "deposit" (decide (TestEnvelope.Command(Deposit 100m)) initial) (PersistEvent(Deposited 100m))
expect "replay" (replay [ Opened "Alice", 1L; Deposited 100m, 2L; Deposited 50m, 3L ]) { Owner = Some "Alice"; Balance = 150m }
printfn "Open an account example checked."
