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
type OrderResult = {
    Status: string
    Reservation: string
    Shipment: string
    Confirmation: Guid
    PlacedAt: DateTime
}
// docs:end

// docs:services
// The warehouse is an external system. The key is the same for every
// attempt of one step, so the warehouse can recognise a repeated call.
type IWarehouse =
    abstract Reserve:
        key: string * item: string * quantity: int -> Task<string>

// The carrier is another external system. It returns a shipment ID.
type ICarrier =
    abstract Ship: key: string * reservation: string -> Task<string>
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
let definition (warehouse: IWarehouse) (carrier: ICarrier) =
    Workflow.define "order" (fun (request: OrderRequest) ->
        workflow {
            // A failed attempt is not stored. FCQRS tries again until an
            // attempt succeeds or the policy gives up.
            let! reservation =
                activity "reserve-stock" reserveRetry (fun attempt ->
                    warehouse.Reserve(
                        attempt.IdempotencyKey, request.Item, request.Quantity))
            // The body must give the same answers on every replay, so it
            // reads the clock and makes GUIDs only through steps. FCQRS
            // stores their values and replays them.
            let! placedAt = now
            let! confirmation = newGuid
            // An order over 1000 waits for a person to approve it, for at
            // most three seconds in this program. With no answer by then,
            // the wait returns None.
            let total = request.UnitPrice * decimal request.Quantity
            let approvalWait = TimeSpan.FromSeconds 3.0
            let! approved =
                workflow {
                    if total <= 1000m then
                        return true
                    else
                        let! answer =
                            waitSignalFor<bool> "approval" approvalWait
                        return defaultArg answer false
                }
            if not approved then
                return {
                    Status = "not approved"
                    Reservation = reservation
                    Shipment = ""
                    Confirmation = confirmation
                    PlacedAt = placedAt
                }
            else
                // Packing takes two seconds. The timer's due time is
                // stored, so a restart does not start the wait again.
                do! sleep (TimeSpan.FromSeconds 2.0)
                let! shipment =
                    activity "ship" (Retry.times 3) (fun attempt ->
                        carrier.Ship(attempt.IdempotencyKey, reservation))
                return {
                    Status = "shipped"
                    Reservation = reservation
                    Shipment = shipment
                    Confirmation = confirmation
                    PlacedAt = placedAt
                }
            })
// docs:end
