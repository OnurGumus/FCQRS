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
// What the warehouse answers when it is asked for stock. FCQRS stores the
// answer as the step's result.
type StockAnswer =
    | Reserved of reservation: string
    | OutOfStock

// The warehouse is an external system. The key is the same for every
// attempt of one step, so the warehouse can recognise a repeated call.
type IWarehouse =
    abstract Reserve:
        key: string * item: string * quantity: int -> Task<StockAnswer>
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
// Up to five attempts, 200 ms apart, each allowed ten seconds.
let reserveRetry = {
    Retry.times 5 (TimeSpan.FromSeconds 10.0) with
        Schedule = FixedInterval(TimeSpan.FromMilliseconds 200.0)
}

// Up to three attempts, each allowed ten seconds.
let callRetry = Retry.times 3 (TimeSpan.FromSeconds 10.0)
// docs:end

// docs:workflow
// "order" is stored with every instance. It must not change.
let definition
    (warehouse: IWarehouse)
    (carrier: ICarrier)
    (accounts: AggregateHandle<AccountCommand, AccountEvent>)
    (fraud: IFraudCheck)
    =
    // The result of an order that reserved no stock.
    let unfilled status = {
        Status = status
        Reservation = ""
        Shipment = ""
        Confirmation = Guid.Empty
        PlacedAt = DateTime.MinValue
    }
    // The rest of an order, once its stock is reserved.
    let fulfil (request: OrderRequest) (reservation: string) =
        workflow {
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
                        return! activity "check-fraud" callRetry check
                }
            // An order over 1000 waits for a person to approve it, for at
            // most one day. With no answer by then, the wait returns None.
            let approvalWait = TimeSpan.FromDays 1.0
            let approval =
                workflow {
                    if not passed then
                        return Error "not approved"
                    elif total <= 1000m then
                        return Ok()
                    else
                        let! answer =
                            waitSignalFor<bool> "approval" approvalWait
                        if defaultArg answer false then
                            return Ok()
                        else
                            return Error "not approved"
                }
            // The confirmation number identifies the payment, so a
            // repeated debit is recognised as the same payment.
            let payment = string confirmation
            let debit =
                workflow {
                    let! reply =
                        send accounts request.Customer (Debit(payment, total))
                    match reply with
                    | Rejected _ -> return Error "payment refused"
                    | _ -> return Ok()
                }
            // A step that gives an Error ends this block with it. An order
            // that is not approved is not charged, and an order that is
            // not paid for does not ship.
            let! shipped =
                workflowResult {
                    do! approval
                    do! debit
                    // Packing takes two seconds. The timer's due time is
                    // stored, so a restart does not start the wait again.
                    do! sleep (TimeSpan.FromSeconds 2.0)
                    return!
                        activity "ship" callRetry (fun attempt ->
                            carrier.Ship(
                                attempt.IdempotencyKey,
                                reservation,
                                request.Item))
                }
            match shipped with
            | Ok shipment -> return finish "shipped" shipment
            | Error status ->
                // An Error is a value, so no handler runs for it. The stock
                // is given back here, whichever step refused.
                let! _ =
                    activity "release-stock" reserveRetry (fun attempt ->
                        warehouse.Release(attempt.IdempotencyKey, reservation))
                return finish status ""
        }
    Workflow.define "order" (fun (request: OrderRequest) ->
        workflow {
            try
                // A failed attempt is not stored. FCQRS tries again until
                // an attempt succeeds or the policy gives up.
                let! answer =
                    activity "reserve-stock" reserveRetry (fun attempt ->
                        warehouse.Reserve(
                            attempt.IdempotencyKey,
                            request.Item,
                            request.Quantity))
                // An answer is the step's result, whichever case it is. It
                // is stored, and the policy does not retry it.
                match answer with
                | OutOfStock -> return unfilled "out of stock"
                | Reserved reservation -> return! fulfil request reservation
            // When a step's policy gives up, the step fails, and the body
            // sees WorkflowStepFailedException. A warehouse that was busy at
            // every attempt reserved nothing, so the order can end with a
            // result. Any other failure is not caught.
            with :? WorkflowStepFailedException as failed
                    when failed.StepName = "reserve-stock" ->
                return unfilled "warehouse unavailable"
        })
// docs:end
