// docs:messages
using FCQRS.Workflows;
using FCQRS.Workflows.CSharp;
using static FCQRS.CSharp;

// What an order workflow is started with. FCQRS stores it in the journal.
public sealed record OrderRequest(
    string Customer, string Item, int Quantity, decimal UnitPrice);

// What the workflow returns. FCQRS stores it too.
public sealed record OrderResult(
    string Status, string Reservation, string Shipment, Guid Confirmation,
    DateTime PlacedAt);
// docs:end

// docs:services
// The warehouse is an external system. The key is the same for every
// attempt of one step, so the warehouse can recognise a repeated call.
public interface IWarehouse
{
    Task<string> Reserve(string key, string item, int quantity);
    Task<string> Release(string key, string reservation);
}

// The carrier is another external system. It returns a shipment ID.
public interface ICarrier
{
    Task<string> Ship(string key, string reservation, string item);
}

// A fraud check is a third external system. It answers whether an order
// may go ahead.
public interface IFraudCheck
{
    Task<bool> Check(string key, string customer, decimal total);
}
// docs:end

// docs:workflow
// "order" is stored with every instance. It must not change.
public sealed class OrderWorkflow(
    IWarehouse warehouse, ICarrier carrier, AggregateFactory accounts,
    IFraudCheck fraud)
    : Workflow<OrderRequest, OrderResult>("order")
{
    // Up to five attempts, 200 ms apart.
    private static readonly RetryPolicy ReserveRetry = Retry.times(5)
        .WithSchedule(RetrySchedules.Fixed(TimeSpan.FromMilliseconds(200)));

    public override async WorkflowTask<OrderResult> Run(
        WorkflowContext ctx, OrderRequest request)
    {
        try
        {
            return await Fulfil(ctx, request);
        }
        // When a step's policy gives up, the step fails, and the body sees
        // WorkflowStepFailedException. Running out of stock is an answer
        // the order can give; any other failure is not caught.
        catch (WorkflowStepFailedException failed)
            when (failed.StepName == "reserve-stock")
        {
            return new OrderResult(
                "out of stock", "", "", Guid.Empty, DateTime.MinValue);
        }
    }

    private async WorkflowTask<OrderResult> Fulfil(
        WorkflowContext ctx, OrderRequest request)
    {
        // A failed attempt is not stored. FCQRS tries again until an
        // attempt succeeds or the policy gives up.
        var reservation = await ctx.Activity("reserve-stock", ReserveRetry,
            attempt => warehouse.Reserve(
                attempt.IdempotencyKey, request.Item, request.Quantity));
        // The body must give the same answers on every replay, so it
        // reads the clock and makes GUIDs only through steps. FCQRS
        // stores their values and replays them.
        var placedAt = await ctx.Now();
        var confirmation = await ctx.NewGuid();
        OrderResult Finish(string status, string shipment) =>
            new(status, reservation, shipment, confirmation, placedAt);
        // Gives the stock back when the order does not ship.
        Step<string> Release() =>
            ctx.Activity("release-stock", ReserveRetry, attempt =>
                warehouse.Release(attempt.IdempotencyKey, reservation));
        var total = request.UnitPrice * request.Quantity;
        // The new step: an order that fails the fraud check is not
        // approved.
        var passed = await ctx.Activity("check-fraud", Retry.times(3),
            attempt => fraud.Check(
                attempt.IdempotencyKey, request.Customer, total));
        // An order over 1000 waits for a person to approve it, for at
        // most one day. With no answer by then, Received is false.
        var approved = passed;
        if (passed && total > 1000m)
        {
            var answer = await ctx.WaitSignal<bool>(
                "approval", TimeSpan.FromDays(1));
            approved = answer.Received && answer.Value;
        }
        if (!approved)
        {
            await Release();
            return Finish("not approved", "");
        }
        // The confirmation number identifies the payment, so a repeated
        // debit is recognised as the same payment.
        var payment = confirmation.ToString();
        var reply = await ctx.Send<AccountCommand, AccountEvent>(
            accounts, request.Customer, new Debit(payment, total));
        if (reply is Rejected)
        {
            await Release();
            return Finish("payment refused", "");
        }
        // Packing takes two seconds. The timer's due time is stored, so
        // a restart does not start the wait again.
        await ctx.Sleep(TimeSpan.FromSeconds(2));
        var shipment = await ctx.Activity("ship", Retry.times(3),
            attempt => carrier.Ship(
                attempt.IdempotencyKey, reservation, request.Item));
        return Finish("shipped", shipment);
    }
}
// docs:end
