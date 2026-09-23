using System.Collections.Immutable;
using static FCQRS.Common;
using static FCQRS.CSharp;

// docs:messages
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
// docs:end

// docs:rules
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
// docs:end
