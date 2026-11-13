// include: samples/orders/5-restart-during-shipping/fsharp/Order.fs
open System
open System.Collections.Generic
open System.Threading
open FCQRS.Workflows
open Order
// Unchanged from step 4.
let api = Unchecked.defaultof<FCQRS.Common.IActor>
type Warehouse() =
    interface IWarehouse with
        member _.Reserve(_, _, _) = Tasks.Task.FromResult "r-1"
// snippet: 1
// snippet: 2
// snippet: 3
