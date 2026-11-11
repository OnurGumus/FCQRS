module Quote =
    open System
    open System.Threading.Tasks
    open FCQRS.Common
    open FCQRS.Workflows
    // snippet: 1
    // snippet: 2
    // snippet: 3
open System
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Actor
open FCQRS.FSharp
open FCQRS.Workflows
open Quote
let logging = LoggerFactory.Create(fun _ -> ())
let configuration = ConfigurationBuilder().Build()
let connection = Fcqrs.connect DBType.Sqlite "Data Source=quotes.db"
let api = Fcqrs.actor configuration logging (Some connection) "quotes"
let prices =
    { new IPriceService with
        member _.Fetch(_, _) = task { return 12.5m } }
let mailer =
    { new IMailer with
        member _.Send(_, _, _) = task { return "sent" } }
// snippet: 4
// snippet: 5
