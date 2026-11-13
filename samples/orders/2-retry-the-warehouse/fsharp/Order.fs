// docs:messages
module Order

open System
open System.Threading.Tasks
open FCQRS.Common
open FCQRS.Workflows

// What an order workflow is started with. FCQRS stores it in the journal.
type OrderRequest = {
    Customer: string
    Item: string
    Quantity: int
    UnitPrice: decimal
}

// What the workflow returns. FCQRS stores it too.
type OrderResult = { Reservation: string }
// docs:end

// docs:services
// The warehouse is an external system. The key is the same for every
// attempt of one step, so the warehouse can recognise a repeated call.
type IWarehouse =
    abstract Reserve:
        key: string * item: string * quantity: int -> Task<string>
// docs:end

// docs:retry
// Up to five attempts, 200 ms apart.
let reserveRetry = {
    Retry.times 5 with
        Schedule = FixedInterval(TimeSpan.FromMilliseconds 200.0)
}
// docs:end

// docs:workflow
// "order" is stored with every instance. It must not change.
let definition (warehouse: IWarehouse) =
    Workflow.define "order" (fun (request: OrderRequest) ->
        workflow {
            // A failed attempt is not stored. FCQRS tries again until an
            // attempt succeeds or the policy gives up.
            let! reservation =
                activity "reserve-stock" reserveRetry (fun attempt ->
                    warehouse.Reserve(
                        attempt.IdempotencyKey, request.Item, request.Quantity))
            return { Reservation = reservation }
        })
// docs:end
