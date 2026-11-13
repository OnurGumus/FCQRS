// docs:messages
using System.Collections.Immutable;
using static FCQRS.Common;
using static FCQRS.CSharp;

// What the program and the order workflow can ask an account to do.
public union AccountCommand(Deposit, Debit);
public sealed record Deposit(decimal Amount);
public sealed record Debit(string Payment, decimal Amount);

// What the account replies. Rejected is a reply only: it is never stored.
public union AccountEvent(Deposited, Debited, Rejected);
public sealed record Deposited(decimal Amount);
public sealed record Debited(string Payment, decimal Amount);
public sealed record Rejected(string Reason);

// What the account knows now. Payments holds the payments it has taken, so
// a repeated debit takes the money once.
public sealed record AccountState(
    decimal Balance, ImmutableHashSet<string> Payments);
// docs:end

// docs:rules
public sealed class Account
    : Aggregate<AccountState, AccountCommand, AccountEvent>
{
    // The name stored with every event of this aggregate.
    public override string EntityName => "Account";
    // The state before the account's first event.
    public override AccountState InitialState => new(0m, []);

    // Chooses what to do with a command, based on the current state.
    public override EventAction<AccountEvent> HandleCommand(
        Command<AccountCommand> command, AccountState state) =>
        command.CommandDetails switch
        {
            Deposit deposit => Store(new Deposited(deposit.Amount)),
            // A repeated debit gets the first answer. It is checked before
            // the balance, because the first debit has already lowered it.
            Debit debit when state.Payments.Contains(debit.Payment) =>
                Repeat(new Debited(debit.Payment, debit.Amount)),
            Debit debit when debit.Amount > state.Balance =>
                Reject($"Insufficient funds: {state.Balance} available"),
            Debit debit => Store(new Debited(debit.Payment, debit.Amount))
        };

    // Applies one event. A rejection or a repeated reply changes nothing.
    public override AccountState ApplyEvent(
        Event<AccountEvent> stored, AccountState state) =>
        stored.EventDetails switch
        {
            Deposited deposited =>
                state with { Balance = state.Balance + deposited.Amount },
            Debited debited when state.Payments.Contains(debited.Payment) =>
                state,
            Debited debited => state with
            {
                Balance = state.Balance - debited.Amount,
                Payments = state.Payments.Add(debited.Payment)
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
