(**
---
title: 6. Add a memo
category: Tutorial
categoryindex: 2
index: 6
---
*)
(*** hide ***)
#r "nuget: FCQRS, 6.13.1"
#load "../../samples/accounts/6-add-a-memo/fsharp/Account.fs"

(**
# Step 6: Add a memo

Alice wants to say why she sends money: a memo on each transfer. The transfers that step 5 stored
have no memo, and they stay in the journal as they are, because FCQRS never rewrites a stored event.
This step changes the events so that the new code reads the old ones, and it rebuilds the statement
with a memo column.

<img src="../img/add-a-memo.svg" alt="Alice's journal holds TransferSent events for t1 and t2 without a memo, stored by step 5, and one for t3 with the memo rent, stored by step 6. The same step 6 code reads all three: the old ones as having no memo. A new projection builds a new statement table, statement_v2, from the first event in the journal, while the old statement table stays unchanged." width="900"/>

## The same change in a CRUD application

A CRUD application adds a column, and the existing rows get `NULL`:

```sql
ALTER TABLE transactions ADD COLUMN memo TEXT;
```

The journal cannot change that way. A stored event records what happened, and nothing rewrites it,
so every later version of the program must read it as it was stored. Adding an optional field is a
change of that kind: an old event reads as a transfer without a memo.

## Continue from step 5

<!-- sample: accounts/6-add-a-memo/fsharp Program.fs continue -->
```fsharp
// This step is the bank's next release. On its first run, it copies the
// database step 5 wrote, so its journal starts with events without a memo.
let database = Path.Combine(AppContext.BaseDirectory, "accounts.db")
if not (File.Exists database) then
    // Step 5 builds into the same folder layout next to this step.
    let previous =
        AppContext.BaseDirectory.Replace("6-add-a-memo", "5-transfer-money")
    let step5 = Path.Combine(previous, "accounts.db")
    if not (File.Exists step5) then
        eprintfn "Run step 5 first: this step continues from its database."
        exit 1
    File.Copy(step5, database)
```

<div class="cs-alt"></div>

<!-- sample: accounts/6-add-a-memo/csharp Program.cs continue -->
```csharp
// This step is the bank's next release. On its first run, it copies the
// database step 5 wrote, so its journal starts with events without a memo.
var database = Path.Combine(AppContext.BaseDirectory, "accounts.db");
if (!File.Exists(database))
{
    // Step 5 builds into the same folder layout next to this step.
    var previous =
        AppContext.BaseDirectory.Replace("6-add-a-memo", "5-transfer-money");
    var step5 = Path.Combine(previous, "accounts.db");
    if (!File.Exists(step5))
    {
        Console.Error.WriteLine(
            "Run step 5 first: this step continues from its database.");
        return 1;
    }
    File.Copy(step5, database);
}
```

This program is the bank's next release, so it starts from the history step 5 wrote. The journal
stores each event with the name of its type, including the assembly it came from:

```text
FCQRS.Common+Event`1[[Account+AccountEvent, Accounts, ...]], FCQRS, ...
```

Steps 5 and 6 both build an assembly named `Accounts` (the `AssemblyName` in their project files), and
both declare `AccountEvent` in the same place, so step 6 can read what step 5 stored. Renaming the event
type, moving it, or renaming the assembly would leave the old rows unreadable. Before such a change,
give the type a stable journal name, as [Evolve persisted events](../how-to/evolve-events.html) shows.

## Add the memo

<!-- sample: accounts/6-add-a-memo/fsharp Account.fs messages -->
```fsharp
module Account

open FCQRS.Common

// What a caller, or the transfer saga, can ask an account to do.
type AccountCommand =
    | Open of owner: string
    | Deposit of amount: decimal
    | Withdraw of amount: decimal
    // New in this step: an optional memo, which the saga passes to the target.
    | SendTransfer of
        transferId: string * target: string * amount: decimal *
        memo: string option
    | ReceiveTransfer of
        transferId: string * source: string * amount: decimal *
        memo: string option
    | RefundTransfer of transferId: string * target: string * amount: decimal

// What the account replies. Rejected is a reply only: it is never stored.
type AccountEvent =
    | Opened of owner: string
    | Deposited of amount: decimal
    | Withdrawn of amount: decimal
    // Events stored before this step have no memo, so it is optional:
    // reading them gives None.
    | TransferSent of
        transferId: string * target: string * amount: decimal *
        memo: string option
    | TransferReceived of
        transferId: string * source: string * amount: decimal *
        memo: string option
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

<!-- sample: accounts/6-add-a-memo/csharp Account.cs messages -->
```csharp
// What a caller, or the transfer saga, can ask an account to do.
public union AccountCommand(
    Open, Deposit, Withdraw, SendTransfer, ReceiveTransfer, RefundTransfer);
public sealed record Open(string Owner);
public sealed record Deposit(decimal Amount);
public sealed record Withdraw(decimal Amount);
// New in this step: an optional memo, which the saga passes to the target.
public sealed record SendTransfer(
    string TransferId, string Target, decimal Amount, string? Memo = null);
public sealed record ReceiveTransfer(
    string TransferId, string Source, decimal Amount, string? Memo = null);
public sealed record RefundTransfer(
    string TransferId, string Target, decimal Amount);

// What the account replies. Rejected is a reply only: it is never stored.
public union AccountEvent(
    Opened, Deposited, Withdrawn,
    TransferSent, TransferReceived, TransferRefunded, Rejected);
public sealed record Opened(string Owner);
public sealed record Deposited(decimal Amount);
public sealed record Withdrawn(decimal Amount);
// Events stored before this step have no memo, so it is optional:
// reading them gives null.
public sealed record TransferSent(
    string TransferId, string Target, decimal Amount, string? Memo = null);
public sealed record TransferReceived(
    string TransferId, string Source, decimal Amount, string? Memo = null);
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

The events stored before this step have no memo, so the new field is optional: `string option` in F#,
and a parameter with the default `null` in C#. An old `TransferSent` reads with no memo. In F#, a new
field that is not an option would make every old `TransferSent` unreadable.

The other parts of an event stay as they are: its case name, its field names, and the meaning of each
field are all stored, and step 5's events use them.

<!-- sample: accounts/6-add-a-memo/fsharp Account.fs rules -->
```fsharp
// Chooses what to do with a command, based on the current state.
let decide (command: Command<AccountCommand>) (state: AccountState) =
    match command.CommandDetails, state.Owner with
    | Open _, Some _ -> DeferEvent(Rejected "The account is already open")
    | Open owner, None -> PersistEvent(Opened owner)
    | _, None -> DeferEvent(Rejected "The account is not open")
    | (Deposit amount | Withdraw amount | SendTransfer(_, _, amount, _)), _
        when amount <= 0m -> DeferEvent(Rejected "The amount must be positive")
    | Deposit amount, _ -> PersistEvent(Deposited amount)
    | (Withdraw amount | SendTransfer(_, _, amount, _)), _
        when amount > state.Balance ->
        DeferEvent(Rejected $"Insufficient funds: {state.Balance} available")
    | Withdraw amount, _ -> PersistEvent(Withdrawn amount)
    | SendTransfer(id, _, _, _), _ when state.Sent.Contains id ->
        DeferEvent(Rejected $"Transfer {id} was already sent")
    | SendTransfer(id, target, amount, memo), _ ->
        PersistEvent(TransferSent(id, target, amount, memo))
    // A repeated delivery gets the first answer; no money moves.
    | ReceiveTransfer(id, source, amount, memo), _
        when state.Received.Contains id ->
        DeferEvent(TransferReceived(id, source, amount, memo))
    | ReceiveTransfer(id, source, amount, memo), _ ->
        PersistEvent(TransferReceived(id, source, amount, memo))
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
    | TransferSent(id, _, amount, _) ->
        { state with
            Balance = state.Balance - amount
            Sent = state.Sent.Add id }
    // FCQRS folds a repeated reply too; a known ID changes nothing.
    | TransferReceived(id, _, _, _) when state.Received.Contains id -> state
    | TransferReceived(id, _, amount, _) ->
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

<!-- sample: accounts/6-add-a-memo/csharp Account.cs rules -->
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
            (SendTransfer send, _) => Store(new TransferSent(
                send.TransferId, send.Target, send.Amount, send.Memo)),
            // A repeated delivery gets the first answer; no money moves.
            (ReceiveTransfer receive, _)
                when state.Received.Contains(receive.TransferId) =>
                Repeat(new TransferReceived(receive.TransferId,
                    receive.Source, receive.Amount, receive.Memo)),
            (ReceiveTransfer receive, _) =>
                Store(new TransferReceived(receive.TransferId,
                    receive.Source, receive.Amount, receive.Memo)),
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

The rules only pass the memo along. The saga passes it too: its `TransferDetails` gets an optional
`Memo` for the same reason, because step 5's sagas stored their states without one.

## Rebuild the statement with a memo column

<!-- sample: accounts/6-add-a-memo/fsharp Statement.fs table -->
```fsharp
// A new read model with a memo column. It is a new table, so the projection
// fills it from the first event in the journal.
let createTable (connection: DbConnection) =
    connection.Execute
        "CREATE TABLE IF NOT EXISTS statement_v2 (
             account TEXT NOT NULL,
             version INTEGER NOT NULL,
             entry TEXT NOT NULL,
             memo TEXT,
             amount NUMERIC NOT NULL,
             balance NUMERIC NOT NULL,
             PRIMARY KEY (account, version))"
    |> ignore
```

<div class="cs-alt"></div>

<!-- sample: accounts/6-add-a-memo/csharp Statement.cs table -->
```csharp
// A new read model with a memo column. It is a new table, so the projection
// fills it from the first event in the journal.
public static void CreateTable(DbConnection connection) =>
    connection.Execute(
        """
        CREATE TABLE IF NOT EXISTS statement_v2 (
            account TEXT NOT NULL,
            version INTEGER NOT NULL,
            entry TEXT NOT NULL,
            memo TEXT,
            amount NUMERIC NOT NULL,
            balance NUMERIC NOT NULL,
            PRIMARY KEY (account, version))
        """);
```

<!-- sample: accounts/6-add-a-memo/fsharp Statement.fs handle -->
```fsharp
// Adds a row whose balance continues from the account's previous row.
let private addRow =
    "INSERT INTO statement_v2 (account, version, entry, memo, amount, balance)
     SELECT @Account, @Version, @Entry, @Memo, @Amount,
            COALESCE((SELECT balance FROM statement_v2 WHERE account = @Account
                      ORDER BY version DESC LIMIT 1), 0) + @Amount"

// FCQRS calls this for each stored event, inside a transaction it commits.
let handle (connection: DbConnection) (transaction: DbTransaction)
           (envelope: EventEnvelope) =
    task {
        match envelope.Event with
        // Only account events go on a statement; Sender is the account's ID.
        | :? Event<AccountEvent> as stored ->
            let add (entry: string) (memo: string option) (amount: decimal) =
                let row =
                    {| Account = string stored.Sender.Value
                       Version = envelope.SequenceNr
                       Entry = entry
                       Memo = Option.toObj memo
                       Amount = amount |}
                connection.ExecuteAsync(addRow, row, transaction) :> Task
            match stored.EventDetails with
            | Opened owner -> do! add $"Opened for {owner}" None 0m
            | Deposited amount -> do! add "Deposit" None amount
            | Withdrawn amount -> do! add "Withdrawal" None -amount
            // The same code handles old events, whose memo is None.
            | TransferSent(id, target, amount, memo) ->
                do! add $"Transfer {id} to {target}" memo -amount
            | TransferReceived(id, source, amount, memo) ->
                do! add $"Transfer {id} from {source}" memo amount
            | TransferRefunded(id, _, amount) ->
                do! add $"Refund of transfer {id}" None amount
            // A rejection is a reply only; the journal never holds one.
            | Rejected _ -> ()
        | _ -> ()
    }
    :> Task
```

<div class="cs-alt"></div>

<!-- sample: accounts/6-add-a-memo/csharp Statement.cs handle -->
```csharp
// Adds a row whose balance continues from the account's previous row.
const string AddRow =
    """
    INSERT INTO statement_v2 (account, version, entry, memo, amount, balance)
    SELECT @Account, @Version, @Entry, @Memo, @Amount,
           COALESCE((SELECT balance FROM statement_v2 WHERE account = @Account
                     ORDER BY version DESC LIMIT 1), 0) + @Amount
    """;

// FCQRS calls this for each stored event, inside a transaction it commits.
public static async Task Handle(
    DbConnection connection, DbTransaction transaction, EventEnvelope envelope)
{
    // Only account events go on a statement; Sender is the account's ID.
    if (envelope.Event is not Event<AccountEvent> { Sender: { } sender } stored)
        return;

    Task Add(string entry, string? memo, decimal amount)
    {
        var row = new
        {
            Account = sender.Value.ToString(),
            Version = envelope.SequenceNr,
            Entry = entry,
            Memo = memo,
            Amount = amount
        };
        return connection.ExecuteAsync(AddRow, row, transaction);
    }

    await (stored.EventDetails switch
    {
        Opened opened => Add($"Opened for {opened.Owner}", null, 0m),
        Deposited deposited => Add("Deposit", null, deposited.Amount),
        Withdrawn withdrawn => Add("Withdrawal", null, -withdrawn.Amount),
        // The same code handles old events, whose memo is null.
        TransferSent sent => Add(
            $"Transfer {sent.TransferId} to {sent.Target}",
            sent.Memo, -sent.Amount),
        TransferReceived received => Add(
            $"Transfer {received.TransferId} from {received.Source}",
            received.Memo, received.Amount),
        TransferRefunded refunded => Add(
            $"Refund of transfer {refunded.TransferId}", null, refunded.Amount),
        // A rejection is a reply only; the journal never holds one.
        Rejected => Task.CompletedTask
    });
}
```

<!-- sample: accounts/6-add-a-memo/fsharp Program.fs register -->
```fsharp
// The new statement has its own table and its own projection name. FCQRS has no
// progress for that name yet, so the projection starts from the first event.
do
    use connection = new SqliteConnection(connectionString)
    Statement.createTable connection

let store =
    SqlProjectionStore(
        ProjectionSqlDialect.Sqlite,
        Func<DbConnection>(fun () -> new SqliteConnection(connectionString)))
let options = TransactionalProjectionOptions("StatementV2", store)
let statement = Fcqrs.transactionalProjection api options Statement.handle
```

<div class="cs-alt"></div>

<!-- sample: accounts/6-add-a-memo/csharp Program.cs register -->
```csharp
// The new statement has its own table and its own projection name. FCQRS has no
// progress for that name yet, so the projection starts from the first event.
using (var connection = new SqliteConnection(connectionString))
    Statement.CreateTable(connection);
var store = new SqlProjectionStore(
    ProjectionSqlDialect.Sqlite, () => new SqliteConnection(connectionString));
var options = new TransactionalProjectionOptions("StatementV2", store);
```

The statement needs a memo column. Instead of changing the table step 5's projection filled, the new
code writes a new table, `statement_v2`, under a new projection name, `StatementV2`. FCQRS has no
progress for that name, so the projection reads the journal from the first event and writes every row
again: step 5's events without memos, and the new ones with them. One handler reads both.

The old table stays as it was, so a program still reading it keeps working until it moves to the new
one. [Rebuild a read model](../how-to/rebuild-a-read-model.html) covers this for a model that must stay
available, and [step 4](show-a-statement.html#Rebuild-the-statement) rebuilds a table in place.

## Send a transfer with a memo

<!-- sample: accounts/6-add-a-memo/fsharp Program.fs transfer -->
```fsharp
// Ask Alice's account for a transfer with a memo, and wait for the saga.
let transfer id target amount memo =
    let cid = Fcqrs.newCid ()
    use outcome = statement.Subscribe(cid, finished, 1)
    let command = SendTransfer(id, target, amount, memo)
    let reply =
        accounts.Send cid alice command (fun _ -> true)
        |> Async.RunSynchronously
    show reply
    if reply.Journaled = Some true then
        outcome.Task.WaitAsync(TimeSpan.FromSeconds 30.).Wait()

transfer "t3" "bob" 25m (Some "rent")
```

<div class="cs-alt"></div>

<!-- sample: accounts/6-add-a-memo/csharp Program.cs transfer -->
```csharp
// Ask Alice's account for a transfer with a memo, and wait for the saga.
async Task SendTransfer(string id, string target, decimal amount, string? memo)
{
    var cid = Values.NewCID();
    using var outcome = statement.SubscribeForFirst(cid, Finished);
    var reply = await accounts(
        _ => true, cid, alice, new SendTransfer(id, target, amount, memo));
    Show(reply);
    if (reply.Journaled?.Value == true)
        await outcome.Task.WaitAsync(TimeSpan.FromSeconds(30));
}

await SendTransfer("t3", "bob", 25m, "rent");
```

## Run it

Run step 5 first, if you have not, then this step. From `samples/accounts`:

```text
dotnet run --project 5-transfer-money/fsharp
dotnet run --project 6-add-a-memo/fsharp
```

<div class="cs-alt" data-fs="text" data-cs="text"></div>

```text
dotnet run --project 5-transfer-money/csharp
dotnet run --project 6-add-a-memo/csharp
```

After one run of step 5, step 6 prints:

```text
Sent 25 to bob (t3, "rent") (version 6, stored)

Transfers stored for alice:
  3  {"transferId":"t1","target":"bob","amount":30}
  4  {"transferId":"t2","target":"carol","amount":20}
  6  {"transferId":"t3","target":"bob","amount":25,"memo":"rent"}
```

<div class="cs-alt" data-fs="text" data-cs="text"></div>

```text
Sent 25 to bob (t3, "rent") (version 6, stored)

Transfers stored for alice:
  3  {"TransferId":"t1","Target":"bob","Amount":30}
  4  {"TransferId":"t2","Target":"carol","Amount":20}
  6  {"TransferId":"t3","Target":"bob","Amount":25,"Memo":"rent"}
```

```text
Statement for alice:
  version  entry                    memo   amount  balance
        1  Opened for Alice                     0        0
        2  Deposit                            100      100
        3  Transfer t1 to bob                 -30       70
        4  Transfer t2 to carol               -20       50
        5  Refund of transfer t2               20       70
        6  Transfer t3 to bob       rent      -25       45

Statement for bob:
  version  entry                    memo   amount  balance
        1  Opened for Bob                       0        0
        2  Transfer t1 from alice              30       30
        3  Transfer t3 from alice   rent       25       55
```

The journal holds both shapes of `TransferSent`: rows 3 and 4 as step 5 stored them, and row 6 with a
memo. To send `t3`, FCQRS loaded Alice's account from all of them, and the new statement has every row
of her history, the first five written from events step 5 stored. If you ran step 5 more than once, the
statements have more rows.

## Run it again

```text
Rejected: Transfer t3 was already sent (version 6, not stored)
```

The rest of the output is the same: the statement keeps its rows, and the projection continues after
the last event it committed.

## Change stored events safely

- **Add fields as optional.** Old events read with the field missing.
- **Keep what is stored.** Case names, field names, the event type's name, its assembly, and the
  meaning of every field are part of the journal. Give the type a stable journal name before you move
  or rename it.
- **Convert what cannot stay.** To rename a field or split an event, register an upcaster, which
  converts old events as FCQRS reads them. [Evolve persisted events](../how-to/evolve-events.html)
  shows how.
- **Mind the snapshots.** A snapshot stores the state, so a state change follows the same rules, or
  the snapshots are deleted, as [step 3](restart-the-bank.html#When-the-state-or-fold-changes) shows.
- **Deploy readers before writers.** When nodes upgrade one at a time, every node must read the new
  shape before any node writes it.

## Where to go next

The six steps built a bank with the parts of FCQRS an application uses:

1. [Open an account](open-an-account.html): commands, events, the journal, and `fold`.
2. [Withdraw money](withdraw-money.html): rules, rejections, and one command at a time.
3. [Restart the bank](restart-the-bank.html): loading an account and snapshots.
4. [Show a statement](show-a-statement.html): read models and projections.
5. [Transfer money](transfer-money.html): sagas and commands that are safe to repeat.
6. Add a memo: changing stored events.

[Concepts](../concepts/index.html) explains the models behind these steps, the
[task guides](../how-to/index.html) cover single tasks such as testing and serving an aggregate over
HTTP, and [Configuration](../configuration.html) lists the runtime settings.

*)

(*** hide ***)
open System.Text.Json
open FCQRS.Common
open Account

let expect name actual expected =
    if actual <> expected then failwithf "%s: expected %A but got %A" name expected actual

let decode<'T> (json: string) =
    FCQRS.Serialization.Serialization.decode<'T> (JsonDocument.Parse(json).RootElement)

// A TransferSent as step 5 stored it.
let stored = """{"Case":"TransferSent","transferId":"t1","target":"bob","amount":30}"""
expect "an old event reads with no memo" (decode<AccountEvent> stored) (TransferSent("t1", "bob", 30m, None))

// The same event, read into a type whose new field is required.
type Required =
    | TransferSent of transferId: string * target: string * amount: decimal * memo: string

let unreadable =
    try
        decode<Required> stored |> ignore
        false
    with _ -> true
expect "a required new field cannot read an old event" unreadable true
printfn "Add a memo example checked."
