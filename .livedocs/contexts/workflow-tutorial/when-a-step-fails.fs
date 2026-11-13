// include: samples/orders/7-when-a-step-fails/fsharp/Account.fs
module Order =
    open System
    open System.Threading.Tasks
    open FCQRS.Common
    open FCQRS.FSharp
    open FCQRS.Workflows
    open Account
    // Unchanged from step 6.
    type OrderRequest = {
        Customer: string
        Item: string
        Quantity: int
        UnitPrice: decimal
    }
    type OrderResult = {
        Status: string
        Reservation: string
        Shipment: string
        Confirmation: Guid
        PlacedAt: DateTime
    }
    // snippet: 1
    let reserveRetry = Retry.times 5
    // snippet: 2
open System
open System.Collections.Generic
open System.Threading
open Microsoft.Data.Sqlite
open FCQRS.Common
open FCQRS.FSharp
open FCQRS.Workflows
open Account
open Order
// Unchanged from step 6.
let database = "orders.db"
let api = Unchecked.defaultof<IActor>
// snippet: 3
// snippet: 4
let accounts =
    Unchecked.defaultof<AggregateHandle<AccountCommand, AccountEvent>>
let orders = Unchecked.defaultof<WorkflowHandle<OrderRequest, OrderResult>>
// snippet: 5
// snippet: 6
