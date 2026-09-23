// include: samples/accounts/2-withdraw-money/fsharp/Account.fs
open FCQRS.Common
open FCQRS.FSharp
open Account
// snippet: 1
let tryWithdraw (api: IActor) (accounts: AggregateHandle<AccountCommand, AccountEvent>) =
    // snippet: 2
