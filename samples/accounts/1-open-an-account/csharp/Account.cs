using static FCQRS.Common;
using static FCQRS.CSharp;

// docs:messages
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
// docs:end

// docs:rules
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
// docs:end
