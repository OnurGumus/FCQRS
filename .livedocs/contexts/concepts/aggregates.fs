open System
open FCQRS.Model.Data
open FCQRS.Common
open FCQRS.FSharp
type AccountCommand = Withdraw of amount: decimal
type AccountState = { Balance: decimal }
type AccountEvent =
    | Withdrawn of amount: decimal
    | Rejected of reason: string
// snippet: 1
