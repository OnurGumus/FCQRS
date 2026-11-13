// include: samples/orders/9-guard-the-change/fsharp/Account.fs
module Order =
    open System
    open System.Threading.Tasks
    open FCQRS.Common
    open FCQRS.FSharp
    open FCQRS.Workflows
    open Account
    // Unchanged from step 8.
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
    let reserveRetry = Retry.times 5
    // snippet: 1
open System
open System.Collections.Generic
open System.Threading
open Microsoft.Data.Sqlite
open FCQRS.Common
open FCQRS.FSharp
open FCQRS.Workflows
open Account
open Order
// Unchanged from step 8.
let database = "orders.db"
let api = Unchecked.defaultof<IActor>
type Warehouse() =
    interface IWarehouse with
        member _.Reserve(_, _, _) = Tasks.Task.FromResult "r-1"
        member _.Release(_, reservation) = Tasks.Task.FromResult reservation
type Carrier(crash: bool) =
    interface ICarrier with
        member _.Ship(_, _, _) = Tasks.Task.FromResult "s-1"
let accounts =
    Unchecked.defaultof<AggregateHandle<AccountCommand, AccountEvent>>
let orders = Unchecked.defaultof<WorkflowHandle<OrderRequest, OrderResult>>
// snippet: 2
