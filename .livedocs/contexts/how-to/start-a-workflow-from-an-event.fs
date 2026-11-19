open System
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Actor
open FCQRS.Common
open FCQRS.FSharp
open FCQRS.Workflows
// snippet: 1
// snippet: 2
let logging = LoggerFactory.Create(fun _ -> ())
let configuration = ConfigurationBuilder().Build()
let connection = Fcqrs.connect DBType.Sqlite "Data Source=accounts.db"
let api = Fcqrs.actor configuration logging (Some connection) "accounts"
let welcomeService =
    { new IWelcomes with
        member _.Send(_, _) = Task.FromResult true }
let welcomes = Workflows.register api (definition welcomeService)
// snippet: 3
