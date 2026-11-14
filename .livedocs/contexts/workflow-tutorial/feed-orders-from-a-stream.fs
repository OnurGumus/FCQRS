// include: samples/orders/10-feed-orders-from-a-stream/fsharp/Account.fs
// include: samples/orders/10-feed-orders-from-a-stream/fsharp/Order.fs
// Unchanged from step 9.
open Order
// snippet: 1
// snippet: 2
// Registered further down this page.
module Registered =
    let orders =
        Unchecked.defaultof<WorkflowHandle<OrderRequest, OrderResult>>
open Registered
// snippet: 3
// snippet: 4
// snippet: 5
// snippet: 6
// snippet: 7
// snippet: 8
