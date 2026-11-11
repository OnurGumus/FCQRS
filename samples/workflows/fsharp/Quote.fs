module Quote

open System
open System.Threading.Tasks
open FCQRS.Common
open FCQRS.Workflows

// docs:messages
// What the workflow is started with.
type QuoteRequest =
    { Customer: string
      Symbol: string
      Quantity: int }

// What the workflow returns.
type Quote =
    { Total: decimal
      Approved: bool
      Receipt: string }
// docs:end

// docs:services
// The external systems the workflow calls. Each call gets a key that is the
// same for every attempt of one step, so a system that remembers keys can
// recognise a repeated call.
type IPriceService =
    abstract Fetch: key: string * symbol: string -> Task<decimal>

type IMailer =
    abstract Send:
        key: string * customer: string * total: decimal -> Task<string>
// docs:end

// docs:workflow
// Five attempts, 200 ms apart.
let private retry =
    { Retry.times 5 with
        Schedule = FixedInterval(TimeSpan.FromMilliseconds 200.0) }

let definition (prices: IPriceService) (mailer: IMailer) =
    Workflow.define "quote" (fun (request: QuoteRequest) -> workflow {
        // The price is stored once it arrives. A restart after this step
        // does not call the price service again.
        let! price =
            activity "price" retry (fun attempt ->
                prices.Fetch(attempt.IdempotencyKey, request.Symbol))
        let total = price * decimal request.Quantity

        // A quote over 1000 waits for a person, for at most one day.
        let! approved = workflow {
            if total <= 1000m then
                return true
            else
                let! answer =
                    waitSignalFor<bool> "approval" (TimeSpan.FromDays 1.0)
                return defaultArg answer false
        }

        if not approved then
            return { Total = total; Approved = false; Receipt = "" }
        else
            let! receipt =
                activity "send-quote" retry (fun attempt ->
                    mailer.Send(
                        attempt.IdempotencyKey, request.Customer, total))
            return { Total = total; Approved = true; Receipt = receipt }
    })
// docs:end
