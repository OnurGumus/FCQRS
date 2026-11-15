// docs:messages
module Order

open System
open System.Threading.Tasks
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

// docs:workflow
// "order" is stored with every instance. It must not change.
let definition (warehouse: IWarehouse) =
    Workflow.define "order" (fun (request: OrderRequest) ->
        workflow {
            // An activity is the only place the body calls another system.
            // FCQRS stores its result. "reserve-stock" is stored too and
            // must not change while orders are running.
            // One attempt. It fails if the warehouse has not answered within
            // ten seconds.
            let policy = Retry.once (TimeSpan.FromSeconds 10.0)
            let! reservation =
                activity "reserve-stock" policy (fun attempt ->
                    warehouse.Reserve(
                        attempt.IdempotencyKey, request.Item, request.Quantity))
            return { Reservation = reservation }
        })
// docs:end
