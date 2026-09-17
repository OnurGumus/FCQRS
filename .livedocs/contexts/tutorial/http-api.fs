// include: samples/registration-fsharp/Account.fs
open System
open FCQRS.Model.Data
open FCQRS.Common
open FCQRS.FSharp
open Account
// include: samples/registration-http-fsharp/UserView.fs
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open UserView
[<CLIMutable>]
type RegistrationRequest = { Name: string }
let valid (value: string) = not (String.IsNullOrWhiteSpace(value)) && value.Length <= 255
let endpoints (app: WebApplication) (accounts: AggregateHandle<RegisterUser, UserRegistered>) (users: UserView) =
    // snippet: 1
    // snippet: 2
