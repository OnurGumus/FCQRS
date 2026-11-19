open System
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Actor
open FCQRS.FSharp
open FCQRS.Workflows
// snippet: 1
let private policy = Retry.times 5 (TimeSpan.FromSeconds 30.0)
// snippet: 2
// snippet: 3
let logging = LoggerFactory.Create(fun _ -> ())
let configuration = ConfigurationBuilder().Build()
let connection = Fcqrs.connect DBType.Sqlite "Data Source=reservations.db"
let api = Fcqrs.actor configuration logging (Some connection) "reservations"
let booking =
    { new IBooking with
        member _.Apply(_, change) = task { return change.Version } }
let reservations = Workflows.register api (definition booking)
// snippet: 4
