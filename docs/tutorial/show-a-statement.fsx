(**
---
title: 4. Show a statement
category: Tutorial
categoryindex: 2
index: 4
---
*)
(*** hide ***)
#r "nuget: FCQRS, 6.7.0"
#r "nuget: Dapper, 2.1.66"
#load "../../samples/accounts/4-show-a-statement/fsharp/Account.fs"
#load "../../samples/accounts/4-show-a-statement/fsharp/Statement.fs"

(**
# Step 4: Show a statement

Alice wants a statement: every deposit and withdrawal, with the balance after it. The account cannot
answer that. It decides commands and keeps only what its rules need, which is the owner and the
balance. This step adds the other half of the split from [the overview](../overview.html): a **read
model**, a table shaped for one screen, which a **projection** keeps up to date from the journal.

<img src="../img/show-a-statement.svg" alt="On the write side, commands reach Alice's account, which stores events in the journal and replies. On the read side, a projection reads new journal events and writes statement rows and its progress in one transaction, then notifies the waiting caller. The caller queries the statement table with SQL." width="900"/>

## The same feature in a CRUD application

A CRUD application writes the statement row in the same transaction as the balance, and the statement
query reads the table that the write code changed:

```sql
BEGIN;
UPDATE accounts SET balance = balance - 30 WHERE id = 'alice';
INSERT INTO transactions (account, entry, amount)
VALUES ('alice', 'Withdrawal', -30);
COMMIT;
-- The statement reads the rows the write code inserted.
SELECT entry, amount FROM transactions WHERE account = 'alice';
```

Every code path that changes a balance must also insert the transaction row. In FCQRS, the account
stores only its event. One projection writes the statement from the journal, and because the journal
holds every event, the statement can be rebuilt from it.

## Create the read model

<!-- sample: accounts/4-show-a-statement/fsharp Statement.fs table -->
```fsharp
// The read model: one row per stored event, with the balance after it.
let createTable (connection: DbConnection) =
    connection.Execute
        "CREATE TABLE IF NOT EXISTS statement (
             account TEXT NOT NULL,
             version INTEGER NOT NULL,
             entry TEXT NOT NULL,
             amount NUMERIC NOT NULL,
             balance NUMERIC NOT NULL,
             PRIMARY KEY (account, version))"
    |> ignore
```

<div class="cs-alt"></div>

<!-- sample: accounts/4-show-a-statement/csharp Statement.cs table -->
```csharp
// The read model: one row per stored event, with the balance after it.
public static void CreateTable(DbConnection connection) =>
    connection.Execute(
        """
        CREATE TABLE IF NOT EXISTS statement (
            account TEXT NOT NULL,
            version INTEGER NOT NULL,
            entry TEXT NOT NULL,
            amount NUMERIC NOT NULL,
            balance NUMERIC NOT NULL,
            PRIMARY KEY (account, version))
        """);
```

The statement is an ordinary table. `version` is the version of the event that produced the row, so
each event adds at most one row.

## Write the projection

<!-- sample: accounts/4-show-a-statement/fsharp Statement.fs handle -->
```fsharp
// Adds a row whose balance continues from the account's previous row.
let private addRow =
    "INSERT INTO statement (account, version, entry, amount, balance)
     SELECT @Account, @Version, @Entry, @Amount,
            COALESCE((SELECT balance FROM statement WHERE account = @Account
                      ORDER BY version DESC LIMIT 1), 0) + @Amount"

// FCQRS calls this for each stored event, inside a transaction it commits.
let handle (connection: DbConnection) (transaction: DbTransaction)
           (envelope: EventEnvelope) =
    task {
        match envelope.Event with
        // Only account events go on a statement; Sender is the account's ID.
        | :? Event<AccountEvent> as stored ->
            let add (entry: string) (amount: decimal) =
                let row =
                    {| Account = string stored.Sender.Value
                       Version = envelope.SequenceNr
                       Entry = entry
                       Amount = amount |}
                connection.ExecuteAsync(addRow, row, transaction) :> Task
            match stored.EventDetails with
            | Opened owner -> do! add $"Opened for {owner}" 0m
            | Deposited amount -> do! add "Deposit" amount
            | Withdrawn amount -> do! add "Withdrawal" -amount
            // A rejection is a reply only; the journal never holds one.
            | Rejected _ -> ()
        | _ -> ()
    }
    :> Task
```

<div class="cs-alt"></div>

<!-- sample: accounts/4-show-a-statement/csharp Statement.cs handle -->
```csharp
// Adds a row whose balance continues from the account's previous row.
const string AddRow =
    """
    INSERT INTO statement (account, version, entry, amount, balance)
    SELECT @Account, @Version, @Entry, @Amount,
           COALESCE((SELECT balance FROM statement WHERE account = @Account
                     ORDER BY version DESC LIMIT 1), 0) + @Amount
    """;

// FCQRS calls this for each stored event, inside a transaction it commits.
public static async Task Handle(
    DbConnection connection, DbTransaction transaction, EventEnvelope envelope)
{
    // Only account events go on a statement; Sender is the account's ID.
    if (envelope.Event is not Event<AccountEvent> { Sender: { } sender } stored)
        return;

    Task Add(string entry, decimal amount)
    {
        var row = new
        {
            Account = sender.Value.ToString(),
            Version = envelope.SequenceNr,
            Entry = entry,
            Amount = amount
        };
        return connection.ExecuteAsync(AddRow, row, transaction);
    }

    await (stored.EventDetails switch
    {
        Opened opened => Add($"Opened for {opened.Owner}", 0m),
        Deposited deposited => Add("Deposit", deposited.Amount),
        Withdrawn withdrawn => Add("Withdrawal", -withdrawn.Amount),
        // A rejection is a reply only; the journal never holds one.
        Rejected => Task.CompletedTask
    });
}
```

FCQRS calls the handler for each stored event and passes it a connection and a transaction. It
commits the handler's writes together with the projection's progress. If the handler fails, neither
is committed, and the event is handled again when the projection restarts.

The handler receives events from every aggregate, so it keeps only account events. `envelope.Event`
is the stored event, `Sender` is the ID of the account that stored it, and `envelope.SequenceNr` is
its version. `Rejected` never arrives, because the journal never holds it.

Each row's balance continues from the account's previous row. That is correct because a projection
receives one account's events in version order. It does not order events across accounts: a handler
that combines several accounts must not depend on which account's event arrives first.

## Register the projection

<!-- sample: accounts/4-show-a-statement/fsharp Program.fs register -->
```fsharp
// The statement table lives in the same SQLite file as the journal.
do
    use connection = new SqliteConnection(connectionString)
    Statement.createTable connection

// FCQRS passes each new journal event to Statement.handle, and commits the
// handler's rows and the projection's progress in one transaction.
let store =
    SqlProjectionStore(
        ProjectionSqlDialect.Sqlite,
        Func<DbConnection>(fun () -> new SqliteConnection(connectionString)))
let options = TransactionalProjectionOptions("Statement", store)
let statement = Fcqrs.transactionalProjection api options Statement.handle
```

<div class="cs-alt"></div>

<!-- sample: accounts/4-show-a-statement/csharp Program.cs register -->
```csharp
// The statement table lives in the same SQLite file as the journal.
using (var connection = new SqliteConnection(connectionString))
    Statement.CreateTable(connection);

// FCQRS passes each new journal event to Statement.Handle, and commits the
// handler's rows and the projection's progress in one transaction.
var store = new SqlProjectionStore(
    ProjectionSqlDialect.Sqlite, () => new SqliteConnection(connectionString));
var options = new TransactionalProjectionOptions("Statement", store);

var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Services.AddFcqrs(connectionString, "accounts")
    .AddAggregate<Account>()
    .AddTransactionalProjection(options, Statement.Handle);
using var host = builder.Build();
await host.StartAsync();
```

`"Statement"` names the projection's progress. FCQRS records, for each account, the version of the
last event this projection committed, and resumes from there after a restart. Give each read model its
own name. In C#, `AddTransactionalProjection` registers the projection with the host, and the program
gets it as `IProjection`.

## Send commands and wait for the statement

<!-- sample: accounts/4-show-a-statement/fsharp Program.fs send -->
```fsharp
let alice = Fcqrs.aggregateId "alice"

// Send a command and wait until the statement includes the event it stored.
let send command =
    let reply =
        Fcqrs.sendAwaiting
            statement accounts (Fcqrs.newCid ()) alice command (fun _ -> true)
        |> Async.RunSynchronously
    let description = describe reply.EventDetails
    printfn $"{description} (version {reply.Version})"

send (Open "Alice")
send (Deposit 100m)
send (Withdraw 30m)
send (Deposit 50m)
send (Withdraw 500m)
```

<div class="cs-alt"></div>

<!-- sample: accounts/4-show-a-statement/csharp Program.cs send -->
```csharp
// Sends commands to accounts and returns the event each one replied with.
var accounts = host.Services
    .GetRequiredService<Handler<AccountCommand, AccountEvent>>();
// Publishes a notification after it commits each event.
var statement = host.Services.GetRequiredService<IProjection>();
var alice = Values.CreateAggregateId("alice");

// Send a command and wait until the statement includes the event it stored.
async Task Send(AccountCommand command)
{
    var cid = Values.NewCID();
    // Subscribe first: a notification sent before the subscription is lost.
    using var projected = statement.SubscribeForFirst(cid);
    var reply = await accounts(_ => true, cid, alice, command);
    // A rejection is not stored, so no notification comes for it.
    if (reply.Journaled?.Value != false)
        await projected.Task.WaitAsync(TimeSpan.FromSeconds(30));
    var description = Describe(reply.EventDetails);
    Console.WriteLine($"{description} (version {reply.Version})");
}

await Send(new Open("Alice"));
await Send(new Deposit(100m));
await Send(new Withdraw(30m));
await Send(new Deposit(50m));
await Send(new Withdraw(500m));
```

The account replies as soon as it stores the event. The projection writes the statement row
afterwards, in its own transaction, so a query right after the reply can miss the newest row.

A **correlation ID** labels one request. FCQRS copies it to the events the command causes, and the
projection publishes a notification carrying it after it commits each event. `Fcqrs.sendAwaiting`
subscribes to the command's correlation ID, sends the command, and waits for that notification. The C#
`Send` spells out the same steps.

- **Subscribe before sending.** A notification published before the subscription exists is missed,
  and the wait would last until its timeout.
- **A rejection has no notification.** It is not stored, so the projection never sees it.
  `sendAwaiting` returns right away when the reply was not stored; the C# code checks `Journaled`.
- **The wait is bounded.** `sendAwaiting` raises `TimeoutException` after the command timeout, 30
  seconds by default. The C# code uses `WaitAsync` for the same bound.

The last argument, `fun _ -> true` (`_ => true` in C#), chooses which reply completes the send. Each
command here produces one reply, so it accepts any.

Waiting covers this projection only. Another read model, or a system that reads the journal, can still
be behind. Subscriptions live in memory for the duration of the request; they are not a queue a client
can reconnect to. To wait for every event stored so far instead of one request's events, call
`CatchUpAsync` on the projection, as [Catch up projections](../how-to/catch-up-projections.html)
shows.

## Query the statement

<!-- sample: accounts/4-show-a-statement/fsharp Program.fs query -->
```fsharp
// The statement is an ordinary table: read it with SQL.
let printStatement () =
    use connection = new SqliteConnection(connectionString)
    connection.Open()
    use query = connection.CreateCommand()
    query.CommandText <-
        "SELECT version, entry, amount, balance FROM statement
         WHERE account = 'alice' ORDER BY version"
    use rows = query.ExecuteReader()
    printfn "\nStatement for alice:"
    printfn "  version  entry              amount  balance"
    while rows.Read() do
        let version, entry = rows.GetInt64 0, rows.GetString 1
        let amount, balance = rows.GetDecimal 2, rows.GetDecimal 3
        printfn $"  {version,7}  {entry,-18}{amount,7}{balance,9}"

printStatement ()
```

<div class="cs-alt"></div>

<!-- sample: accounts/4-show-a-statement/csharp Program.cs query -->
```csharp
// The statement is an ordinary table: read it with SQL.
void PrintStatement()
{
    using var connection = new SqliteConnection(connectionString);
    connection.Open();
    using var query = connection.CreateCommand();
    query.CommandText =
        """
        SELECT version, entry, amount, balance FROM statement
        WHERE account = 'alice' ORDER BY version
        """;
    using var rows = query.ExecuteReader();
    Console.WriteLine();
    Console.WriteLine("Statement for alice:");
    Console.WriteLine("  version  entry              amount  balance");
    while (rows.Read())
    {
        var (version, entry) = (rows.GetInt64(0), rows.GetString(1));
        var (amount, balance) = (rows.GetDecimal(2), rows.GetDecimal(3));
        Console.WriteLine($"  {version,7}  {entry,-18}{amount,7}{balance,9}");
    }
}

PrintStatement();
```

Reading a read model is a plain SQL query. The table already has the shape the screen needs, so the
query does not fold events or join tables.

## Run it

From `samples/accounts`:

```text
dotnet run --project 4-show-a-statement/fsharp
```

<div class="cs-alt" data-fs="text" data-cs="text"></div>

```text
dotnet run --project 4-show-a-statement/csharp
```

Both programs print:

```text
Opened for Alice (version 1)
Deposited 100 (version 2)
Withdrew 30 (version 3)
Deposited 50 (version 4)
Rejected: Insufficient funds: 120 available (version 4)

Statement for alice:
  version  entry              amount  balance
        1  Opened for Alice        0        0
        2  Deposit               100      100
        3  Withdrawal            -30       70
        4  Deposit                50      120
```

The rejected withdrawal has no row: the projection only sees stored events.

## Run it again

```text
Rejected: The account is already open (version 4)
Deposited 100 (version 5)
Withdrew 30 (version 6)
Deposited 50 (version 7)
Rejected: Insufficient funds: 240 available (version 7)

Statement for alice:
  version  entry              amount  balance
        1  Opened for Alice        0        0
        2  Deposit               100      100
        3  Withdrawal            -30       70
        4  Deposit                50      120
        5  Deposit               100      220
        6  Withdrawal            -30      190
        7  Deposit                50      240
```

The projection resumed after version 4, the last event it had committed for Alice, so rows 1 to 4
were not written again. The primary key would reject a repeated row.

## Rebuild the statement

The statement holds nothing that the journal does not, so it can be rebuilt. Stop the program, delete
the statement's rows and the projection's progress, and run it again:

```sql
DELETE FROM statement;
DELETE FROM fcqrs_projection_progress
WHERE projection_name = 'Statement';
```

The projection starts again from each account's first event and writes every row, then continues with
the new commands. Rebuild this way after fixing a bug in the handler or changing the table.
[Rebuild a read model](../how-to/rebuild-a-read-model.html) covers rebuilding beside a model that must
stay available.

## Next

[Step 5: Transfer money](transfer-money.html) moves money from Alice to Bob. A transfer changes two
accounts, and each account decides only with its own state, so the transfer needs a workflow that
coordinates them: a saga.

*)

(*** hide ***)
open System.Data.Common
open Akka.Persistence.Query
open Microsoft.Data.Sqlite
open FCQRS.CSharp
open Account

let expect name actual expected =
    if actual <> expected then failwithf "%s: expected %A but got %A" name expected actual

// Run the handler against an in-memory database, as the projection would, and read the balances.
let balances events =
    use connection = new SqliteConnection("Data Source=:memory:")
    connection.Open()
    Statement.createTable connection
    for version, event in events do
        use transaction = connection.BeginTransaction()
        let stored = { TestEnvelope.Event(event, version) with Sender = Some(Values.CreateAggregateId "alice") }
        let envelope = EventEnvelope(Offset.Sequence version, "Account/default-shard/alice", version, box stored, 0L, [||])
        (Statement.handle connection transaction envelope).Wait()
        transaction.Commit()
    use query = connection.CreateCommand()
    query.CommandText <- "SELECT balance FROM statement ORDER BY version"
    use rows = query.ExecuteReader()
    [ while rows.Read() do rows.GetDecimal 0 ]

expect "running balance"
    (balances [ 1L, Opened "Alice"; 2L, Deposited 100m; 3L, Withdrawn 30m; 4L, Deposited 50m ])
    [ 0m; 100m; 70m; 120m ]
printfn "Show a statement example checked."
