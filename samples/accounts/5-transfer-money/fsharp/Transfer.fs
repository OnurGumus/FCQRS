module Transfer

open FCQRS.FSharp
open FCQRS.Workflows
open Account

// docs:types
// What the application asks for. The application starts the workflow with
// the transfer's ID as the workflow ID.
type TransferRequest = {
    Id: string
    Source: string
    Target: string
    Amount: decimal
}

// How a transfer ended.
type TransferOutcome =
    // The target account took the money.
    | Completed
    // The target turned it down, and the source account got the money back.
    | Refunded
    // The source account turned it down, so no money moved.
    | Refused of reason: string
// docs:end

// docs:workflow
// Each send is a step: FCQRS stores its reply, and after a restart the
// workflow continues after the last stored step. A step that had not stored
// its reply sends its command again.
let transfer
    (accounts: AggregateHandle<AccountCommand, AccountEvent>)
    (request: TransferRequest)
    =
    let { Id = id; Source = source; Target = target; Amount = amount } = request
    workflow {
        // Take the money from the source account.
        match! send accounts source (SendTransfer(id, target, amount)) with
        | Rejected reason -> return Refused reason
        | _ ->
            // Deliver it to the target account.
            let deliver = ReceiveTransfer(id, source, amount)
            match! send accounts target deliver with
            | TransferReceived _ -> return Completed
            // The target's only other answer is Rejected: give the money back.
            | _ ->
                let refund = RefundTransfer(id, target, amount)
                let! _ = send accounts source refund
                return Refunded
    }

// The name is stored with every instance and must not change.
let definition accounts = Workflow.define "Transfer" (transfer accounts)
// docs:end
