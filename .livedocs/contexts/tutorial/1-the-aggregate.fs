// include: samples/registration-fsharp/Account.fs
open System
open FCQRS.Model.Data
open FCQRS.Common
open FCQRS.FSharp
open Account
let send (accounts: AggregateHandle<RegisterUser, UserRegistered>) (id: AggregateId) = async {
    // snippet: 1
    return reply
}
// snippet: 2
