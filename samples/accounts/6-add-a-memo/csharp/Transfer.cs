using FCQRS;
using Microsoft.FSharp.Core;
using static FCQRS.Common;
using static FCQRS.CSharp;

// docs:states
// What a transfer moves, and between which accounts.
// Memo is new in this step. Transfers stored before it have none.
public sealed record TransferDetails(
    string Id, string Source, string Target, decimal Amount, string? Memo = null);

// Where a transfer is.
public union TransferState(Delivering, Refunding, Completed);
// Waiting for the target account to take the money.
public sealed record Delivering(TransferDetails Transfer);
// The target turned it down: waiting for the source account's refund.
public sealed record Refunding(TransferDetails Transfer);
public sealed record Completed;

// The saga needs no fixed data besides its state.
public sealed record TransferData;
// docs:end

public sealed class Transfer(AggregateFactory accounts)
    : Saga<AccountEvent, TransferData, TransferState>
{
    public override string SagaName => "Transfer";
    public override TransferData InitialData => new();
    // The aggregate whose event starts the saga.
    public override AggregateFactory Originator => accounts;

    // docs:react
    // Turns an event into the next state to store.
    public override EventAction<TransferState> HandleEvent(
        object message,
        SagaState<TransferData, FSharpOption<TransferState>> saga) =>
        (message, saga.State) switch
        {
            // Sender is the ID of the account that stored the event.
            (Event<AccountEvent> { Sender: { } sender } stored, var state) =>
                React(stored.EventDetails, sender.Value.ToString(), state),
            // No answer in time. The outcome is unknown, so enter the same
            // state again, which sends the command again.
            (ExpectationExhausted, { Value: Delivering or Refunding } waiting) =>
                StateChanged(waiting.Value),
            _ => Unhandled()
        };

    static EventAction<TransferState> React(
        AccountEvent @event, string sender, FSharpOption<TransferState>? state) =>
        (@event, state) switch
        {
            (TransferSent sent, null) => StateChanged(new Delivering(new(
                sent.TransferId, sender, sent.Target, sent.Amount, sent.Memo))),
            (TransferReceived received, { Value: Delivering delivering })
                when received.TransferId == delivering.Transfer.Id =>
                StateChanged(new Completed()),
            (Rejected, { Value: Delivering delivering })
                when sender == delivering.Transfer.Target =>
                StateChanged(new Refunding(delivering.Transfer)),
            (TransferRefunded refunded, { Value: Refunding refunding })
                when refunded.TransferId == refunding.Transfer.Id =>
                StateChanged(new Completed()),
            _ => Unhandled()
        };
    // docs:end

    // docs:effects
    // Returns the commands for a stored state. It runs again after recovery;
    // `recovering` says whether FCQRS is recovering, and this saga ignores it.
    public override SagaSideEffectResult<TransferState> ApplySideEffects(
        SagaState<TransferData, TransferState> saga, bool recovering) =>
        saga.State switch
        {
            Delivering(var (id, source, target, amount, memo)) => Deliver(
                ToAccount(target, new ReceiveTransfer(id, source, amount, memo))),
            Refunding(var (id, _, target, amount, _)) =>
                Deliver(ToSource(new RefundTransfer(id, target, amount))),
            Completed => new() { Transition = StopSaga(), Commands = [] }
        };

    // Send now and every 5 seconds; after 30 seconds, tell HandleEvent.
    static SagaSideEffectResult<TransferState> Deliver(ExecuteCommand command) =>
        new()
        {
            Transition = Stay(),
            Expect = Expectations.Create(
                [command],
                TimeSpan.FromSeconds(30),
                RetrySchedules.Fixed(TimeSpan.FromSeconds(5)))
        };

    // An account expects its command union, so these helpers take one. In FCQRS
    // 6.6.0, a single case passed as object arrives as its own type, and no account
    // handles it.
    ExecuteCommand ToAccount(string id, AccountCommand command) =>
        SagaCommands.ToAggregate(accounts, id, command);

    ExecuteCommand ToSource(AccountCommand command) =>
        SagaCommands.ToOriginator(accounts, command);
    // docs:end

    // docs:definition
    // A transfer starts when an account stores TransferSent.
    public static bool StartsOn(object message) =>
        message is Event<AccountEvent> { EventDetails: TransferSent };
    // docs:end
}
