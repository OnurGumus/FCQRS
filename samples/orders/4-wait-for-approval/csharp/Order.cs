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
}

// The carrier is another external system. It returns a shipment ID.
public interface ICarrier
{
    Task<string> Ship(string key, string reservation);
}
// docs:end

// docs:workflow
// "order" is stored with every instance. It must not change.
public sealed class OrderWorkflow(IWarehouse warehouse, ICarrier carrier)
    : Workflow<OrderRequest, OrderResult>("order")
{
    // Up to five attempts, 200 ms apart.
    private static readonly RetryPolicy ReserveRetry = Retry.times(5)
        .WithSchedule(RetrySchedules.Fixed(TimeSpan.FromMilliseconds(200)));

    public override async WorkflowTask<OrderResult> Run(
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
        // An order over 1000 waits for a person to approve it, for at
        // most three seconds in this program. With no answer by then,
        // Received is false.
        var total = request.UnitPrice * request.Quantity;
        if (total > 1000m)
        {
            var answer = await ctx.WaitSignal<bool>(
                "approval", TimeSpan.FromSeconds(3));
            if (!(answer.Received && answer.Value))
                return new OrderResult(
                    "not approved", reservation, "", confirmation, placedAt);
        }
        // Packing takes two seconds. The timer's due time is stored, so
        // a restart does not start the wait again.
        await ctx.Sleep(TimeSpan.FromSeconds(2));
        var shipment = await ctx.Activity("ship", Retry.times(3),
            attempt => carrier.Ship(attempt.IdempotencyKey, reservation));
        return new OrderResult(
            "shipped", reservation, shipment, confirmation, placedAt);
    }
}
// docs:end
