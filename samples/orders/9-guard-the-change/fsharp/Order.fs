// docs:messages
module Order

open System
open System.Threading.Tasks
open FCQRS.Common
open FCQRS.FSharp
open FCQRS.Workflows
open Account

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
    abstract Release: key: string * reservation: string -> Task<string>

// The carrier is another external system. It returns a shipment ID.
type ICarrier =
    abstract Ship:
        key: string * reservation: string * item: string -> Task<string>

// A fraud check is a third external system. It answers whether an order
// may go ahead.
type IFraudCheck =
    abstract Check:
        key: string * customer: string * total: decimal -> Task<bool>
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
let definition
    (warehouse: IWarehouse)
    (carrier: ICarrier)
    (accounts: AggregateHandle<AccountCommand, AccountEvent>)
    (fraud: IFraudCheck)
    =
    Workflow.define "order" (fun (request: OrderRequest) ->
        workflow {
            try
                // A failed attempt is not stored. FCQRS tries again until
                // an attempt succeeds or the policy gives up.
                let! reservation =
                    activity "reserve-stock" reserveRetry (fun attempt ->
                        warehouse.Reserve(
                            attempt.IdempotencyKey,
                            request.Item,
                            request.Quantity))
                // The body must give the same answers on every replay, so it
                // reads the clock and makes GUIDs only through steps. FCQRS
                // stores their values and replays them.
                let! placedAt = now
                let! confirmation = newGuid
                let finish status shipment = {
                    Status = status
                    Reservation = reservation
                    Shipment = shipment
                    Confirmation = confirmation
                    PlacedAt = placedAt
                }
                // Gives the stock back when the order does not ship.
                let release =
                    activity "release-stock" reserveRetry (fun attempt ->
                        warehouse.Release(attempt.IdempotencyKey, reservation))
                let total = request.UnitPrice * decimal request.Quantity
                // An order that had passed this point before the fraud check
                // was added gets 1 and skips it. A new order gets 2. An order
                // that fails the fraud check is not approved.
                let! change = version "fraud-check" 2
                let check (attempt: AttemptContext) =
                    fraud.Check(attempt.IdempotencyKey, request.Customer, total)
                let! passed =
                    workflow {
                        if change < 2 then
                            return true
                        else
                            return! activity "check-fraud" (Retry.times 3) check
                    }
                // An order over 1000 waits for a person to approve it, for at
                // most one day. With no answer by then, the wait returns None.
                let approvalWait = TimeSpan.FromDays 1.0
                let! approved =
                    workflow {
                        if not passed then
                            return false
                        elif total <= 1000m then
                            return true
                        else
                            let! answer =
                                waitSignalFor<bool> "approval" approvalWait
                            return defaultArg answer false
                    }
                if not approved then
                    let! _ = release
                    return finish "not approved" ""
                else
                    // The confirmation number identifies the payment, so a
                    // repeated debit is recognised as the same payment.
                    let payment = string confirmation
                    let! reply =
                        send accounts request.Customer (Debit(payment, total))
                    match reply with
                    | Rejected _ ->
                        let! _ = release
                        return finish "payment refused" ""
                    | _ ->
                        // Packing takes two seconds. The timer's due time
                        // is stored, so a restart does not start the wait.
                        do! sleep (TimeSpan.FromSeconds 2.0)
                        let! shipment =
                            activity "ship" (Retry.times 3) (fun attempt ->
                                carrier.Ship(
                                    attempt.IdempotencyKey,
                                    reservation,
                                    request.Item))
                        return finish "shipped" shipment
            // When a step's policy gives up, the step fails, and the body
            // sees WorkflowStepFailedException. Running out of stock is an
            // answer the order can give; any other failure is not caught.
            with :? WorkflowStepFailedException as failed
                    when failed.StepName = "reserve-stock" ->
                return {
                    Status = "out of stock"
                    Reservation = ""
                    Shipment = ""
                    Confirmation = Guid.Empty
                    PlacedAt = DateTime.MinValue
                }
        })
// docs:end
