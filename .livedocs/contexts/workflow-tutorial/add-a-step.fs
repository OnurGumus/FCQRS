// include: samples/orders/8-add-a-step/fsharp/Account.fs
module Order =
    open System
    open System.Threading.Tasks
    open FCQRS.Common
    open FCQRS.FSharp
    open FCQRS.Workflows
    open Account
    // Unchanged from step 7.
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
// Unchanged from step 7.
let database = "orders.db"
let api = Unchecked.defaultof<IActor>
type Warehouse() =
    interface IWarehouse with
        member _.Reserve(_, _, _) = Tasks.Task.FromResult "r-1"
        member _.Release(_, reservation) = Tasks.Task.FromResult reservation
type Carrier(crash: bool) =
    interface ICarrier with
        member _.Ship(_, _, _) = Tasks.Task.FromResult "s-1"
// snippet: 3
// snippet: 4
// snippet: 5
// snippet: 6
