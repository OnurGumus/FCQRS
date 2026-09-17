open System
open FCQRS.Model.Data
open FCQRS.Common
open FCQRS.FSharp
type OrderCommand = CancelOrder
type OrderState = Pending | Shipped | Cancelled
type OrderEvent = OrderCancelled | OrderAlreadyShipped | AlreadyCancelled
// snippet: 1
