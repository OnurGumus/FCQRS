// snippet: 1 module
    // snippet: 2
open FCQRS.Model.Data
open FCQRS.Common
open FCQRS.FSharp
open Account
let send (accounts: AggregateHandle<RegisterUser, UserRegistered>) (id: AggregateId) = async {
    // snippet: 3
    return reply
}
