// snippet: 1 module
    // Unchanged from step 3.
    type IWarehouse =
        abstract Reserve:
            key: string * item: string * quantity: int -> Task<string>
    type ICarrier =
        abstract Ship: key: string * reservation: string -> Task<string>
    let reserveRetry = Retry.times 5
    // snippet: 2
open System
open FCQRS.Workflows
open Order
// Unchanged from step 3.
let orders: WorkflowHandle<OrderRequest, OrderResult> = Unchecked.defaultof<_>
// snippet: 3
