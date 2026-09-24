open System.Threading
open FCQRS.Model.Data
open FCQRS.Common
open FCQRS.FSharp
open FCQRS.Query
module Account =
    type AccountCommand =
        | Open of owner: string
        | Deposit of amount: decimal
        | Withdraw of amount: decimal
    type AccountEvent =
        | Opened of owner: string
        | Deposited of amount: decimal
        | Withdrawn of amount: decimal
        | Rejected of reason: string
open Account
let deposit (api: IActor) (handle: obj -> unit)
            (accounts: AggregateHandle<AccountCommand, AccountEvent>)
            (cid: CID) (alice: AggregateId) = async {
    // snippet: 1
    return reply
}
let sendManually (statement: ISubscribe)
                 (accounts: AggregateHandle<AccountCommand, AccountEvent>)
                 (cid: CID) (alice: AggregateId) (command: AccountCommand)
                 (cancellationToken: CancellationToken) = async {
    // snippet: 2
}
