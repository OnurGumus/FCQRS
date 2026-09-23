using static FCQRS.Common;
using static FCQRS.CSharp;

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
