// include: samples/workflows/fsharp/Quote.fs
open System
open System.Data.Common
open Dapper
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Actor
open FCQRS.FSharp
open FCQRS.ProjectionStorage
open FCQRS.Projections
open FCQRS.Workflows
open Quote
let connectionString = "Data Source=approvals.db"
let logging = LoggerFactory.Create(fun _ -> ())
let configuration = ConfigurationBuilder().Build()
let connection = Fcqrs.connect DBType.Sqlite connectionString
let api = Fcqrs.actor configuration logging (Some connection) "approvals"
let prices =
    { new IPriceService with
        member _.Fetch(_, _) = task { return 12.5m } }
let mailer =
    { new IMailer with
        member _.Send(_, _, _) = task { return "sent" } }
let quotes = Workflows.register api (definition prices mailer)
// snippet: 1
module Approvals =
    open System.Data.Common
    open System.Threading.Tasks
    open Akka.Persistence.Query
    open Dapper
    open FCQRS.Workflows
    // snippet: 2
    // snippet: 3
// snippet: 4
// snippet: 5
module Instances =
    open System
    open System.Data.Common
    open System.Threading.Tasks
    open Akka.Persistence.Query
    open Dapper
    open FCQRS.Workflows
    // snippet: 6
    // snippet: 7
    // snippet: 8
// snippet: 9
