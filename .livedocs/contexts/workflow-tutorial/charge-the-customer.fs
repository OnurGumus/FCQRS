// snippet: 1 module
    // snippet: 2
// snippet: 3 module
    // snippet: 4
    // Unchanged from step 2.
    let reserveRetry = Retry.times 5
    // snippet: 5
// snippet: 6
// snippet: 7
// Unchanged from step 5.
type Carrier(crash: bool) =
    interface ICarrier with
        member _.Ship(_, _) = Tasks.Task.FromResult "s-1"
// snippet: 8
// snippet: 9
