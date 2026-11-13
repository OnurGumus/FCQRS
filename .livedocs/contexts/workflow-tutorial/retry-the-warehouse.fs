// snippet: 1 module
    // Unchanged from step 1.
    type IWarehouse =
        abstract Reserve:
            key: string * item: string * quantity: int -> Task<string>
    // snippet: 2
    // snippet: 3
open System
open System.Collections.Generic
open Order
// snippet: 4
