using System.Text.Json.Serialization;
using static FCQRS.Common;
using static FCQRS.CSharp;

// docs:account
// An account that can only be opened. Opening it stores Opened, the event
// that starts a welcome.
public sealed record Open(string Owner);

// The case names are stored with each event, so they must not change.
[JsonDerivedType(typeof(Opened), "Opened")]
[JsonDerivedType(typeof(Rejected), "Rejected")]
public abstract record AccountEvent;
public sealed record Opened(string Owner) : AccountEvent;
// A reply only: it is never stored.
public sealed record Rejected(string Reason) : AccountEvent;

public sealed record AccountState(string? Owner);

public sealed class Account : Aggregate<AccountState, Open, AccountEvent>
{
    public override string EntityName => "Account";
    public override AccountState InitialState => new((string?)null);

    public override EventAction<AccountEvent> HandleCommand(
        Command<Open> command, AccountState state) =>
        state.Owner is null
            ? EventActions.Persist<AccountEvent>(
                new Opened(command.CommandDetails.Owner))
            : EventActions.Defer<AccountEvent>(
                new Rejected("The account is already open"));

    public override AccountState ApplyEvent(
        Event<AccountEvent> stored, AccountState state) =>
        stored.EventDetails is Opened opened ? new(opened.Owner) : state;
}
// docs:end
