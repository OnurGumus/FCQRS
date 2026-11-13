// include: samples/accounts/5-transfer-money/fsharp/Account.fs
open System
open System.Threading.Tasks
open FCQRS.Common
open FCQRS.FSharp
open FCQRS.Workflows
open Account
let score (key: string) (order: string) : Task<int> = Task.FromResult 42
let charge (key: string) (amount: decimal) : Task<string> = Task.FromResult "paid"
let isShipped (key: string) (order: string) : Task<bool> = Task.FromResult true
// snippet: 1
// snippet: 2
// snippet: 3
// snippet: 4
// snippet: 5
// snippet: 6
// snippet: 7
// snippet: 8
// snippet: 9
// snippet: 10
