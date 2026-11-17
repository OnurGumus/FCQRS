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
// What the warehouse answers when it is asked for stock. FCQRS stores the
// answer as the step's result.
public union StockAnswer(Reserved, OutOfStock);
public sealed record Reserved(string Reservation);
public sealed record OutOfStock;

// The warehouse is an external system. The key is the same for every
// attempt of one step, so the warehouse can recognise a repeated call.
public interface IWarehouse
{
    Task<StockAnswer> Reserve(string key, string item, int quantity);
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
    // Up to five attempts, 200 ms apart, each allowed ten seconds.
    private static readonly RetryPolicy ReserveRetry =
        Retry.times(5, TimeSpan.FromSeconds(10))
            .WithSchedule(RetrySchedules.Fixed(TimeSpan.FromMilliseconds(200)));

    // Up to three attempts, each allowed ten seconds.
    private static readonly RetryPolicy CallRetry =
        Retry.times(3, TimeSpan.FromSeconds(10));

    public override async WorkflowTask<OrderResult> Run(
        WorkflowContext ctx, OrderRequest request)
    {
        try
        {
            // A failed attempt is not stored. FCQRS tries again until an
            // attempt succeeds or the policy gives up.
            var answer = await ctx.Activity("reserve-stock", ReserveRetry,
                attempt => warehouse.Reserve(
                    attempt.IdempotencyKey, request.Item, request.Quantity));
            // An answer is the step's result, whichever case it is. It is
            // stored, and the policy does not retry it.
            return answer switch
            {
                OutOfStock => Unfilled("out of stock"),
                Reserved reserved =>
                    await Fulfil(ctx, request, reserved.Reservation),
            };
        }
        // When a step's policy gives up, the step fails, and the body sees
        // WorkflowStepFailedException. A warehouse that was busy at every
        // attempt reserved nothing, so the order can end with a result. Any
        // other failure is not caught.
        catch (WorkflowStepFailedException failed)
            when (failed.StepName == "reserve-stock")
        {
            return Unfilled("warehouse unavailable");
        }
    }

    // The result of an order that reserved no stock.
    private static OrderResult Unfilled(string status) =>
        new(status, "", "", Guid.Empty, DateTime.MinValue);

    // The rest of an order, once its stock is reserved.
    private async WorkflowTask<OrderResult> Fulfil(
        WorkflowContext ctx, OrderRequest request, string reservation)
    {
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
        // An order that had passed this point before the fraud check was
        // added gets 1 and skips it. A new order gets 2. An order that
        // fails the fraud check is not approved.
        var change = await ctx.Version("fraud-check", 2);
        var passed = change < 2
            || await ctx.Activity("check-fraud", CallRetry,
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
        var shipment = await ctx.Activity("ship", CallRetry,
            attempt => carrier.Ship(
                attempt.IdempotencyKey, reservation, request.Item));
        return Finish("shipped", shipment);
    }
}
// docs:end
