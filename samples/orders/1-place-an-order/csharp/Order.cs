// docs:messages
using FCQRS.Workflows;
using FCQRS.Workflows.CSharp;

// What an order workflow is started with. FCQRS stores it in the journal.
public sealed record OrderRequest(
    string Customer, string Item, int Quantity, decimal UnitPrice);

// What the workflow returns. FCQRS stores it too.
public sealed record OrderResult(string Reservation);
// docs:end

// docs:services
// The warehouse is an external system. The key is the same for every
// attempt of one step, so the warehouse can recognise a repeated call.
public interface IWarehouse
{
    Task<string> Reserve(string key, string item, int quantity);
}
// docs:end

// docs:workflow
// "order" is stored with every instance. It must not change.
public sealed class OrderWorkflow(IWarehouse warehouse)
    : Workflow<OrderRequest, OrderResult>("order")
{
    public override async WorkflowTask<OrderResult> Run(
        WorkflowContext ctx, OrderRequest request)
    {
        // An activity is the only place the body calls another system.
        // FCQRS stores its result. "reserve-stock" is stored too and
        // must not change while orders are running.
        var reservation = await ctx.Activity("reserve-stock", Retry.none,
            attempt => warehouse.Reserve(
                attempt.IdempotencyKey, request.Item, request.Quantity));
        return new OrderResult(reservation);
    }
}
// docs:end
