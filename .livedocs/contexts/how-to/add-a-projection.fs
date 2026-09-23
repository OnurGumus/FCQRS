open FCQRS.Common
open FCQRS.FSharp
module Account =
    type AccountEvent =
        | Opened of owner: string
        | Deposited of amount: decimal
        | Withdrawn of amount: decimal
        | Rejected of reason: string
open Account
// snippet: 1
let register (api: IActor) (connString: string) =
    // snippet: 2
    subscriptions
