// snippet: 1 module
    // snippet: 2
    // Unchanged from step 2.
    let reserveRetry = Retry.times 5
    // snippet: 3
// snippet: 4
// Unchanged from step 2.
type Warehouse() =
    interface IWarehouse with
        member _.Reserve(_, _, _) = System.Threading.Tasks.Task.FromResult "r-1"
// snippet: 5
// snippet: 6
// snippet: 7
