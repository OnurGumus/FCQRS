using FCQRS;
using FCQRS.Workflows.CSharp;
using static FCQRS.Common;
using static FCQRS.CSharp;

// docs:types
// What the application asks for. The application starts the workflow with
// the transfer's ID as the workflow ID.
// New in this step: an optional memo. Transfers started before it have none.
public sealed record TransferRequest(
    string Id, string Source, string Target, decimal Amount,
    string? Memo = null);

// How a transfer ended.
public union TransferOutcome(Completed, Refunded, Refused);
// The target account took the money.
public sealed record Completed;
// The target turned it down, and the source account got the money back.
public sealed record Refunded;
// The source account turned it down, so no money moved.
public sealed record Refused(string Reason);
// docs:end

// docs:workflow
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
        var (id, source, target, amount, memo) = request;
        // Take the money from the source account.
        var send = new SendTransfer(id, target, amount, memo);
        if (await Send(ctx, source, send) is Rejected rejected)
            return new Refused(rejected.Reason);
        // Deliver it to the target account.
        var deliver = new ReceiveTransfer(id, source, amount, memo);
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
// docs:end
