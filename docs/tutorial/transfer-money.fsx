(**
---
title: 5. Transfer money
category: Tutorial
categoryindex: 2
index: 5
---
*)
(*** hide ***)
#r "nuget: FCQRS, 7.5.1"
#r "nuget: FCQRS.Workflows, 7.15.1"
#load "../../samples/accounts/5-transfer-money/fsharp/Account.fs"
#load "../../samples/accounts/5-transfer-money/fsharp/Transfer.fs"

(**
# Step 5: Transfer money

Alice sends 30 to Bob. Each account decides only with its own state ([step 2](withdraw-money.html)),
so no account can move money into another. A **workflow** coordinates the transfer: the application
starts it with the transfer's ID, and it sends commands to the accounts one at a time. When the target
account cannot take the money, the workflow gives it back to the sender.

<img src="../img/transfer-money.svg" alt="The application starts the transfer workflow with ID t1. The workflow sends SendTransfer to Alice, who stores TransferSent, then ReceiveTransfer to Bob, who stores TransferReceived, and the workflow returns Completed. For t2 to Carol, who has no account, Carol's account rejects ReceiveTransfer, the workflow sends RefundTransfer to Alice, who stores TransferRefunded, and the workflow returns Refunded." width="900"/>

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
workflow is the part that finishes or undoes the transfer, and it stores its progress so a restart
does not lose a transfer halfway.

## Add transfer commands and events

<!-- sample: accounts/5-transfer-money/fsharp Account.fs messages -->
```fsharp
module Account

open FCQRS.Common

// What a caller, or a transfer, can ask an account to do.
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
type AccountState = {
    Owner: string option
    Balance: decimal
    Sent: Set<string>
    Received: Set<string>
    Refunded: Set<string>
}

// The state before the account's first event.
let initial = {
    Owner = None
    Balance = 0m
    Sent = Set.empty
    Received = Set.empty
    Refunded = Set.empty
}
```

<div class="cs-alt"></div>

<!-- sample: accounts/5-transfer-money/csharp Account.cs messages -->
```csharp
// What a caller, or a transfer, can ask an account to do.
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
    // A repeated send gets the first answer, checked before the balance,
    // because the first send lowered it. No money moves twice.
    | SendTransfer(id, target, amount), _ when state.Sent.Contains id ->
        DeferEvent(TransferSent(id, target, amount))
    | (Deposit amount | Withdraw amount | SendTransfer(_, _, amount)), _
        when amount <= 0m -> DeferEvent(Rejected "The amount must be positive")
    | Deposit amount, _ -> PersistEvent(Deposited amount)
    | (Withdraw amount | SendTransfer(_, _, amount)), _
        when amount > state.Balance ->
        DeferEvent(Rejected $"Insufficient funds: {state.Balance} available")
    | Withdraw amount, _ -> PersistEvent(Withdrawn amount)
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
    | TransferSent(id, _, _) when state.Sent.Contains id -> state
    | TransferSent(id, _, amount) ->
        { state with
            Balance = state.Balance - amount
            Sent = state.Sent.Add id
        }
    // FCQRS folds a repeated reply too; a known ID changes nothing.
    | TransferReceived(id, _, _) when state.Received.Contains id -> state
    | TransferReceived(id, _, amount) ->
        { state with
            Balance = state.Balance + amount
            Received = state.Received.Add id
        }
    | TransferRefunded(id, _, _) when state.Refunded.Contains id -> state
    | TransferRefunded(id, _, amount) ->
        { state with
            Balance = state.Balance + amount
            Refunded = state.Refunded.Add id
        }
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
            // A repeated send gets the first answer, checked before the
            // balance, because the first send lowered it. No money moves twice.
            (SendTransfer send, _) when state.Sent.Contains(send.TransferId) =>
                Repeat(new TransferSent(
                    send.TransferId, send.Target, send.Amount)),
            (Deposit { Amount: <= 0m } or Withdraw { Amount: <= 0m }
                or SendTransfer { Amount: <= 0m }, _) =>
                Reject("The amount must be positive"),
            (Deposit deposit, _) => Store(new Deposited(deposit.Amount)),
            (Withdraw withdraw, _) when withdraw.Amount > state.Balance =>
                Reject($"Insufficient funds: {state.Balance} available"),
            (SendTransfer send, _) when send.Amount > state.Balance =>
                Reject($"Insufficient funds: {state.Balance} available"),
            (Withdraw withdraw, _) => Store(new Withdrawn(withdraw.Amount)),
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
            TransferSent sent
                when state.Sent.Contains(sent.TransferId) => state,
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

Every transfer command gets the same treatment: a second command with a transfer ID the account has
already handled gets the first answer again, as a reply that is not stored. The workflow in the next
section sends each command again after a restart when it had not stored the reply, and the money must
move only once.

The check for a repeated `SendTransfer` comes before the balance check. The first `SendTransfer`
lowered the balance, so a balance check on the repeat could refuse it, and the workflow would end a
transfer as refused after the money had left Alice's account.

FCQRS applies a repeated reply with `fold` too, as it does every deferred reply, so `fold` checks the
ID and leaves the state unchanged. An account that is not open rejects `ReceiveTransfer`; that
rejection is what sends a transfer back.

## Write the transfer as a workflow

<!-- sample: accounts/5-transfer-money/fsharp Transfer.fs types -->
```fsharp
// What the application asks for. The application starts the workflow with
// the transfer's ID as the workflow ID.
type TransferRequest = {
    Id: string
    Source: string
    Target: string
    Amount: decimal
}

// How a transfer ended.
type TransferOutcome =
    // The target account took the money.
    | Completed
    // The target turned it down, and the source account got the money back.
    | Refunded
    // The source account turned it down, so no money moved.
    | Refused of reason: string
```

<div class="cs-alt"></div>

<!-- sample: accounts/5-transfer-money/csharp Transfer.cs types -->
```csharp
// What the application asks for. The application starts the workflow with
// the transfer's ID as the workflow ID.
public sealed record TransferRequest(
    string Id, string Source, string Target, decimal Amount);

// How a transfer ended.
public union TransferOutcome(Completed, Refunded, Refused);
// The target account took the money.
public sealed record Completed;
// The target turned it down, and the source account got the money back.
public sealed record Refunded;
// The source account turned it down, so no money moved.
public sealed record Refused(string Reason);
```

`TransferRequest` is the workflow's **input**: what the application asks for. `TransferOutcome` is its
**result**: how the transfer ended.

<!-- sample: accounts/5-transfer-money/fsharp Transfer.fs workflow -->
```fsharp
// Each send is a step: FCQRS stores its reply, and after a restart the
// workflow continues after the last stored step. A step that had not stored
// its reply sends its command again.
let transfer
    (accounts: AggregateHandle<AccountCommand, AccountEvent>)
    (request: TransferRequest)
    =
    let { Id = id; Source = source; Target = target; Amount = amount } = request
    workflow {
        // Take the money from the source account.
        match! send accounts source (SendTransfer(id, target, amount)) with
        | Rejected reason -> return Refused reason
        | _ ->
            // Deliver it to the target account.
            let deliver = ReceiveTransfer(id, source, amount)
            match! send accounts target deliver with
            | TransferReceived _ -> return Completed
            // The target's only other answer is Rejected: give the money back.
            | _ ->
                let refund = RefundTransfer(id, target, amount)
                let! _ = send accounts source refund
                return Refunded
    }

// The name is stored with every instance and must not change.
let definition accounts = Workflow.define "Transfer" (transfer accounts)
```

<div class="cs-alt"></div>

<!-- sample: accounts/5-transfer-money/csharp Transfer.cs workflow -->
```csharp
// Each send is a step: FCQRS stores its reply, and after a restart the
// workflow continues after the last stored step. A step that had not stored
// its reply sends its command again.
// The name is stored with every instance and must not change.
public sealed class TransferWorkflow(IServiceProvider services)
    : Workflow<TransferRequest, TransferOutcome>("Transfer")
{
    // The accounts the workflow sends commands to.
    private readonly AggregateFactory accounts =
        services.AggregateFactory<Account>();

    public override async WorkflowTask<TransferOutcome> Run(
        WorkflowContext ctx, TransferRequest request)
    {
        var (id, source, target, amount) = request;
        // Take the money from the source account.
        var send = new SendTransfer(id, target, amount);
        if (await Send(ctx, source, send) is Rejected rejected)
            return new Refused(rejected.Reason);
        // Deliver it to the target account.
        var deliver = new ReceiveTransfer(id, source, amount);
        if (await Send(ctx, target, deliver) is TransferReceived)
            return new Completed();
        // The target's only other answer is Rejected: give the money back.
        var refund = new RefundTransfer(id, target, amount);
        await Send(ctx, source, refund);
        return new Refunded();
    }

    // Send takes any command. This helper takes an AccountCommand, so the
    // compiler rejects anything else sent to an account.
    Step<AccountEvent> Send(
        WorkflowContext ctx, string account, AccountCommand command) =>
        ctx.Send<AccountCommand, AccountEvent>(accounts, account, command);
}
```

The body reads top to bottom, in the order the transfer happens. `send` sends a command to an account
and waits for the reply: an event that this command caused. Each `send` is a **step**. FCQRS stores
the step's reply in the workflow's own journal before the body continues.

The stored steps are how the workflow survives a restart. When the process starts again, FCQRS runs
the body from the beginning and gives each stored step its stored reply instead of sending the command
again. The body continues at the first step that has no stored reply, and sends that step's command
again, because the journal cannot show whether the command reached the account. This is why every
transfer command must be safe to repeat.

The same replay is why the body must make the same decisions every time it runs. It uses only its
input and the replies of its steps. A workflow that reads the clock or calls another system does it
through steps of its own, which the [workflow tutorial](../workflow-tutorial/place-an-order.html) introduces.

Each `send` carries a command ID made from the workflow's ID and the step's position, and it sends
again, with the same ID, when no reply comes in time. `Transfer` is the workflow's stored name, like an
aggregate's `EntityName`, and must not change.

## Register the workflow and start transfers

<!-- sample: accounts/5-transfer-money/fsharp Program.fs register -->
```fsharp
// Register the workflow with the accounts it sends commands to. Registering
// it also resumes the transfers that were running when the process stopped.
let transfers = Workflows.register api (Transfer.definition accounts)
```

<div class="cs-alt"></div>

<!-- sample: accounts/5-transfer-money/csharp Program.cs register -->
```csharp
// Register the workflow beside the account. The host registers it at startup,
// which also resumes the transfers that were running when the process stopped.
builder.Services.AddFcqrs(connectionString, "accounts")
    .AddAggregate<Account>()
    .AddTransactionalProjection(options, Statement.Handle);
builder.Services
    .AddWorkflow<TransferWorkflow, TransferRequest, TransferOutcome>();
```

<!-- sample: accounts/5-transfer-money/fsharp Program.fs transfer -->
```fsharp
let describeOutcome outcome =
    match outcome with
    | Completed -> "completed"
    | Refunded -> "refunded"
    | Refused reason -> $"refused: {reason}"

// Start a transfer from Alice and wait for it to end. A start with an ID that
// is already in the journal stores nothing, so a repeated request moves no
// money: the transfer's ID is the workflow's ID.
let transfer id target amount =
    let request = {
        Id = id
        Source = "alice"
        Target = target
        Amount = amount
    }
    let start = transfers.Start id request |> Async.RunSynchronously
    let repeated =
        match start with
        | StartResult.Started -> ""
        | StartResult.AlreadyStarted _ -> " (already started)"
    let ended =
        transfers.Result id (TimeSpan.FromSeconds 30.)
        |> Async.RunSynchronously
    match ended with
    | WorkflowOutcome.Finished outcome ->
        printfn $"Transfer {id}: {describeOutcome outcome}{repeated}"
    | other -> printfn $"Transfer {id} ended without a result: %A{other}"

transfer "t1" "bob" 30m
// Carol has no account, so this transfer comes back.
transfer "t2" "carol" 20m
```

<div class="cs-alt"></div>

<!-- sample: accounts/5-transfer-money/csharp Program.cs transfer -->
```csharp
static string DescribeOutcome(TransferOutcome outcome) => outcome switch
{
    Completed => "completed",
    Refunded => "refunded",
    Refused refused => $"refused: {refused.Reason}"
};

// Start a transfer from Alice and wait for it to end. A start with an ID that
// is already in the journal stores nothing, so a repeated request moves no
// money: the transfer's ID is the workflow's ID.
async Task Transfer(string id, string target, decimal amount)
{
    var request = new TransferRequest(id, "alice", target, amount);
    var started = await transfers.StartAsync(id, request);
    var repeated = started ? "" : " (already started)";
    var outcome = await transfers.ResultAsync(id, TimeSpan.FromSeconds(30));
    Console.WriteLine($"Transfer {id}: {DescribeOutcome(outcome)}{repeated}");
}

await Transfer("t1", "bob", 30m);
// Carol has no account, so this transfer comes back.
await Transfer("t2", "carol", 20m);
```

The application starts a transfer by its ID. `Start` returns once FCQRS has stored the start, so a
transfer that has started survives a crash right after. A second start with an ID that is already in
the journal stores nothing and reports the existing instance. A caller that retries a request, for
example after a timeout, cannot start the transfer twice, as long as it sends the same transfer ID.

`Result` waits for the workflow to end and gives its result. The wait is in memory: the transfer
continues when the caller stops waiting. A workflow that has already ended gives its stored result,
which the second run below shows.

The statement is a projection ([step 4](show-a-statement.html)) and follows the journal on its own,
behind the workflow. `CatchUpAsync` waits until it has read every event the transfers stored, before
the program reads it.

## Deliver a transfer twice

<!-- sample: accounts/5-transfer-money/fsharp Program.fs repeat -->
```fsharp
// After a restart, a workflow sends a command again when its reply was not
// stored. Do the same by hand:
send bob (ReceiveTransfer("t1", "alice", 30m))
```

<div class="cs-alt"></div>

<!-- sample: accounts/5-transfer-money/csharp Program.cs repeat -->
```csharp
// After a restart, a workflow sends a command again when its reply was not
// stored. Do the same by hand:
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
Transfer t1: completed
Transfer t2: refunded
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
account rejected `ReceiveTransfer`, and the workflow refunded Alice, which is row 5 of her statement.
The second delivery of `t1` got the first answer again without storing anything, and Bob's statement
has one row for it.

Between rows 4 and 5, Alice's statement shows 20 that has left her account and reached no one. A
workflow is not a transaction: a query during a transfer sees the money in flight.

## Run it again

```text
Rejected: The account is already open (version 5, not stored)
Deposited 100 (version 6, stored)
Rejected: The account is already open (version 2, not stored)
Transfer t1: completed (already started)
Transfer t2: refunded (already started)
Received 30 from alice (t1) (version 2, not stored)
```

The program sends the same requests again. Only the deposit is new: `t1` and `t2` are already in the
journal, so their starts store nothing and `Result` gives their stored results. The statements gain
one row, Alice's deposit.

## When an account does not answer

`send` sends again with backoff for five minutes. When no reply comes in that time, the step fails
with the kind `OutcomeUnknown`: the command may have been applied, and the account may have taken the
money. The transfer does not treat that as a rejection, because a refund after Bob took the money
would create money. The failure ends the workflow as failed, and `Status` reports it:

```fsharp
// An operator, or a scheduled job, looks for a failed transfer and runs
// its failed step again. The step keeps its command ID, so an account
// that applied the command answers with its first verdict.
match transfers.Status "t1" |> Async.RunSynchronously with
| WorkflowStatus.Failed error when error.Kind = ErrorKind.OutcomeUnknown ->
    transfers.Resume "t1" |> Async.RunSynchronously
| _ -> ()
```

`Resume` keeps the stored steps and runs the failed one again. A transfer stays failed until something
resumes it; FCQRS does not restart a failed workflow by itself.

## What remains the application's job

- **A stable transfer ID.** The workflow ID is the only thing that stops a repeated request from
  starting a second transfer. Take it from the request, not from a new GUID on each attempt.
- **Repeatable commands.** Every command a workflow sends can arrive more than once. Give it an ID the
  receiver remembers, and answer a repeat with the first verdict, checked before anything the first
  command changed.
- **Failed transfers.** A transfer whose outcome is unknown waits for `Resume`. Watch for failed
  instances and decide who resumes them.
- **External systems.** Stored steps do not make a call to another system happen exactly once. A call
  to a payment provider needs its own idempotency key, timeout, and a way to undo or escalate.
  [Workflows](../concepts/workflows.html) covers recovery and failures in detail.

Earlier versions of FCQRS coordinated a transfer with a saga, a state machine started by the account's
`TransferSent` event. Sagas remain supported for applications that use them;
[Write a saga](../how-to/write-a-saga.html) shows this transfer as one.

## Next

[Step 6: Add a memo](add-a-memo.html) adds a memo to transfers. `TransferSent` events are already
stored without one, so the step shows how to change an event that the journal already holds. The
[workflow tutorial](../workflow-tutorial/place-an-order.html) continues with what a workflow can do besides
sending commands: retries, timers, signals from people, and parallel steps.

*)

(*** hide ***)
open System
open FCQRS.Common
open FCQRS.CSharp
open FCQRS.FSharp
open FCQRS.Workflows
open Account
open Transfer

let expect name actual expected =
    if actual <> expected then failwithf "%s: expected %A but got %A" name expected actual

// The harness runs no step, so the accounts are never called.
let accounts: AggregateHandle<AccountCommand, AccountEvent> =
    { Factory = Unchecked.defaultof<_>
      Send = fun _ _ _ _ -> failwith "the harness sends no command" }
let request id target amount = { Id = id; Source = "alice"; Target = target; Amount = amount }

let delivered = WorkflowTest.Start(definition accounts, request "t1" "bob" 30m)
delivered.Complete("send", TransferSent("t1", "bob", 30m))
delivered.Complete("send", TransferReceived("t1", "alice", 30m))
expect "a delivered transfer completes" delivered.Result Completed
delivered.CheckReplay()

let refunded = WorkflowTest.Start(definition accounts, request "t2" "carol" 20m)
refunded.Complete("send", TransferSent("t2", "carol", 20m))
refunded.Complete("send", Rejected "The account is not open")
refunded.Complete("send", TransferRefunded("t2", "carol", 20m))
expect "a rejected delivery is refunded" refunded.Result Refunded

let refused = WorkflowTest.Start(definition accounts, request "t3" "bob" 500m)
refused.Complete("send", Rejected "Insufficient funds: 100 available")
expect "a refused send moves nothing" refused.Result (Refused "Insufficient funds: 100 available")

// No reply in time: the workflow fails instead of refunding, and Resume sends the same step again.
let unknown = WorkflowTest.Start(definition accounts, request "t4" "bob" 30m)
unknown.Complete("send", TransferSent("t4", "bob", 30m))
unknown.Fail("send", TimeoutException "no reply", ErrorKind.OutcomeUnknown)
match unknown.Status with
| WorkflowStatus.Failed error when error.Kind = ErrorKind.OutcomeUnknown -> ()
| other -> failwithf "an unknown outcome fails the transfer, but it is %A" other
unknown.Resume()
unknown.Complete("send", TransferReceived("t4", "alice", 30m))
expect "a resumed transfer completes" unknown.Result Completed

let opened = fold (TestEnvelope.Event(Opened "Alice", 1L)) initial
let funded = fold (TestEnvelope.Event(Deposited 30m, 2L)) opened
let sent = fold (TestEnvelope.Event(TransferSent("t1", "bob", 30m), 3L)) funded
expect "a repeated send gets the first answer after the balance was spent"
    (decide (TestEnvelope.Command(SendTransfer("t1", "bob", 30m))) sent)
    (DeferEvent(TransferSent("t1", "bob", 30m)))
expect "a repeated reply leaves the state"
    (fold (TestEnvelope.Event(TransferSent("t1", "bob", 30m), 3L)) sent)
    sent
printfn "Transfer money example checked."
