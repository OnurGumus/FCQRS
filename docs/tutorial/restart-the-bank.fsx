(**
---
title: 3. Restart the bank
category: Tutorial
categoryindex: 2
index: 3
---
*)
(*** hide ***)
#r "nuget: FCQRS, 6.8.0"
#load "../../samples/accounts/3-restart-the-bank/fsharp/Account.fs"

(**
# Step 3: Restart the bank

When you ran steps 1 and 2 again, the versions continued where the last run stopped. Before the first
command, FCQRS loaded the account by folding its stored events. This step shows when an account loads,
how a snapshot shortens loading for a long history, and what loading requires from `fold`.

<img src="../img/restart-the-bank.svg" alt="Alice's journal holds 251 events, with snapshots at versions 100 and 200. Loading reads the newest snapshot, a balance of 1990 at version 200, then folds events 201 to 251 to reach a balance of 2500 at version 251. Events 1 to 200 are not read." width="900"/>

## When an account loads

FCQRS keeps an account in memory while it receives commands. It loads the account from the database:

- on the account's first command after the program starts;
- on its first command after two idle minutes. FCQRS removes an account from memory when it has
  received no commands for that long, which is called **passivation**. In a cluster, an account also
  loads again when it moves to another node.

Loading starts from the initial state and folds the account's stored events in order. Commands after
that use the state in memory, so an account pays the cost of loading once per load, not once per
command. The registration's `Passivation = PassivationPolicy.Default` keeps the two-minute limit, and
a C# aggregate class inherits the same default. `PassivationPolicy.After` (`NewAfter` in C#) sets
another idle time, and `PassivationPolicy.Never` keeps an account in memory until the program stops.

## Loading compared with a CRUD read

A CRUD application stores the balance in a column, so reading an account costs one row however long
its history is:

```sql
-- The balance is a column: one row.
SELECT owner, balance FROM accounts WHERE id = 'alice';
```

Without snapshots, loading an account with 10,000 events folds 10,000 events. A **snapshot** bounds
that work: FCQRS saves the account's state every so many events, and loading starts from the newest
snapshot. After this step's first run, loading Alice's account reads the equivalent of these two
queries:

```sql
-- The newest snapshot: Alice's state at version 200.
SELECT sequence_number, snapshot FROM snapshot
WHERE persistence_id = 'Account/default-shard/alice'
ORDER BY sequence_number DESC LIMIT 1;
-- The events stored after it, folded in order.
SELECT sequence_number, message FROM journal
WHERE persistence_id = 'Account/default-shard/alice'
  AND sequence_number > 200
ORDER BY sequence_number;
```

## Save a snapshot every 100 events

<!-- sample: accounts/3-restart-the-bank/fsharp Program.fs register -->
```fsharp
// Register the account rules and save a snapshot every 100 events.
let accounts =
    Fcqrs.aggregate api
        { Name = "Account"
          Initial = initial
          Decide = decide
          Fold = fold
          Snapshots = Every 100
          Passivation = PassivationPolicy.Default }
```

<div class="cs-alt"></div>

<!-- sample: accounts/3-restart-the-bank/csharp Account.cs snapshots -->
```csharp
// Save a snapshot of the state every 100 events.
public override SnapshotPolicy SnapshotPolicy => SnapshotPolicy.NewEvery(100);
```

`Snapshots = Every 100` in the F# registration, like the `SnapshotPolicy` override on the C# `Account`
class, saves the account's state after each event whose version is a multiple of 100. `Default` saves one every 30 events: steps 1 and 2 used it,
but neither stored 30 events. `NoSnapshots` stops saving new snapshots, although loading still uses a
snapshot saved earlier.

The account's rules are the ones from step 2.

## Send 250 deposits

<!-- sample: accounts/3-restart-the-bank/fsharp Program.fs send -->
```fsharp
let alice = Fcqrs.aggregateId "alice"

// Send a command and wait for the account's reply.
let request command =
    accounts.Send (Fcqrs.newCid ()) alice command (fun _ -> true)
    |> Async.RunSynchronously

let show (reply: Event<AccountEvent>) =
    let description = describe reply.EventDetails
    printfn $"{description} (version {reply.Version})"

show (request (Open "Alice"))

// Deposit 10, 250 times, and print the last reply.
let replies = [ for _ in 1 .. 250 -> request (Deposit 10m) ]
show (List.last replies)
```

<div class="cs-alt"></div>

<!-- sample: accounts/3-restart-the-bank/csharp Program.cs send -->
```csharp
// Sends commands to accounts and returns the event each one replied with.
var accounts = host.Services
    .GetRequiredService<Handler<AccountCommand, AccountEvent>>();
var alice = Values.CreateAggregateId("alice");

// Send a command and wait for the account's reply.
Task<Event<AccountEvent>> Request(AccountCommand command) =>
    accounts(_ => true, Values.NewCID(), alice, command);

void Show(Event<AccountEvent> reply)
{
    var description = Describe(reply.EventDetails);
    Console.WriteLine($"{description} (version {reply.Version})");
}

Show(await Request(new Open("Alice")));

// Deposit 10, 250 times, and print the last reply.
var replies = new List<Event<AccountEvent>>();
for (var i = 0; i < 250; i++)
    replies.Add(await Request(new Deposit(10m)));
Show(replies[^1]);
```

## Run it

From `samples/accounts`:

```text
dotnet run --project 3-restart-the-bank/fsharp
```

<div class="cs-alt" data-fs="text" data-cs="text"></div>

```text
dotnet run --project 3-restart-the-bank/csharp
```

Both programs print:

```text
Opened for Alice (version 1)
Deposited 10 (version 251)

Journal: 251 events for Account/default-shard/alice
Snapshots:
  version 100  {"Owner":"Alice","Balance":990}
  version 200  {"Owner":"Alice","Balance":1990}
```

The journal still holds all 251 events. A snapshot is an extra row that stores the account's state at
one version as JSON: at version 200, Alice's account held one `Opened` and 199 deposits of 10.

FCQRS saves a snapshot in the background after it replies, so the reply to the 200th command does not
wait for it. The program waits for the snapshot rows before it prints them, and it reads these tables
only to show them.

## Run it again

```text
Rejected: The account is already open (version 251)
Deposited 10 (version 501)

Journal: 501 events for Account/default-shard/alice
Snapshots:
  version 100  {"Owner":"Alice","Balance":990}
  version 200  {"Owner":"Alice","Balance":1990}
  version 300  {"Owner":"Alice","Balance":2990}
  version 400  {"Owner":"Alice","Balance":3990}
  version 500  {"Owner":"Alice","Balance":4990}
```

To handle the first command, `Open`, FCQRS loaded Alice's account from the snapshot at version 200 and
folded the 51 events after it, instead of all 251. FCQRS keeps every event and every snapshot. Loading
uses the newest snapshot.

## What loading requires from fold

`fold` runs when FCQRS stores an event and again every time the account loads, possibly months later
in a newer version of the program. Each run must produce the same state, so `fold` may use only the
previous state and the event. A `fold` that reads the clock, a random number, a configuration value,
or a database can produce a different state on each load.

For example, a `fold` that subtracted a withdrawal fee read from configuration would change past
balances when the fee changed: every account would load with the new fee applied to its old
withdrawals. Compute the fee in `decide`, which runs once per command, and store it in the event.
`fold` then applies what the event records.

## When the state or fold changes

A snapshot stores the result of `fold` under the name of the state type. Changing `AccountState`, or
what `fold` computes, affects the snapshots already stored:

- An account loaded from an old snapshot keeps what the old `fold` computed for the events before
  that snapshot.
- If FCQRS cannot read the newest snapshot, for example because `AccountState` was renamed, it stops
  the process with `Process terminated due to deserialization error` when the account loads.

The journal still holds every event, so the snapshots can go. Stop the program, delete the
aggregate's snapshots, and start the new version. Each account's next load folds its whole history
with the new code:

```sql
DELETE FROM snapshot WHERE persistence_id LIKE 'Account/%';
```

[Evolve persisted events](../how-to/evolve-events.html#Handle-snapshots-separately) covers keeping
snapshots compatible instead.

## Next

[Step 4: Show a statement](show-a-statement.html) builds a statement for Alice: a read model with her
transactions and balance, updated from the journal.

*)

(*** hide ***)
open FCQRS.Common
open FCQRS.CSharp
open Account

let expect name actual expected =
    if actual <> expected then failwithf "%s: expected %A but got %A" name expected actual

let foldFrom state events =
    events |> List.fold (fun state (event, version) -> fold (TestEnvelope.Event(event, version)) state) state

let deposits first last = [ for version in first .. last -> Deposited 10m, version ]
let full = foldFrom initial ((Opened "Alice", 1L) :: deposits 2L 251L)

// The state at version 200 is the snapshot the program prints.
expect "snapshot at 200" (foldFrom initial ((Opened "Alice", 1L) :: deposits 2L 200L)) { Owner = Some "Alice"; Balance = 1990m }
// Loading from that snapshot and folding the rest gives the state a full replay gives.
expect "snapshot plus later events" (foldFrom { Owner = Some "Alice"; Balance = 1990m } (deposits 201L 251L)) full
expect "state at 251" full { Owner = Some "Alice"; Balance = 2500m }
printfn "Restart the bank example checked."
