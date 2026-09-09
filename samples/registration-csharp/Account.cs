// docs:messages
using static FCQRS.Common;
using static FCQRS.CSharp;

public sealed record RegisterUser(string Name);
public sealed record UserRegistered(string Name);
public sealed record AccountState(string? Name = null);
// docs:end

// docs:rules
public sealed class Account : Aggregate<AccountState, RegisterUser, UserRegistered>
{
    public override string EntityName => "RegistrationCSharpAccount";
    public override AccountState InitialState => new();

    public override EventAction<UserRegistered> HandleCommand(
        Command<RegisterUser> command, AccountState state) =>
        EventActions.PersistConditionally(state.Name is null,
            new UserRegistered(state.Name ?? command.CommandDetails.Name));

    public override AccountState ApplyEvent(Event<UserRegistered> stored, AccountState state) =>
        new(stored.EventDetails.Name);
}
// docs:end
