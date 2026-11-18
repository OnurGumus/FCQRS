open System
open System.Data.Common
open System.IO
open System.Threading
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

// Each run starts with an empty journal, so its output is the same.
let database = Path.Combine(AppContext.BaseDirectory, "approvals.db")
for file in [ database; database + "-wal"; database + "-shm" ] do
    File.Delete file

let connectionString = $"Data Source={database}"
let logging = LoggerFactory.Create(fun _ -> ())
let configuration = ConfigurationBuilder().Build()
let connection = Fcqrs.connect DBType.Sqlite connectionString
let api = Fcqrs.actor configuration logging (Some connection) "approvals"

// Stand-ins for external systems.
let prices = {
    new IPriceService with
        member _.Fetch(_, _) = task { return 12.5m }
}

let mailer = {
    new IMailer with
        member _.Send(_, customer, total) =
            task { return $"quote of {total} sent to {customer}" }
}

let quotes = Workflows.register api (definition prices mailer)

// docs:projection
using (new SqliteConnection(connectionString)) (fun connection ->
    connection.Execute Approvals.createTable |> ignore)

// The projection reads only the quote workflow's journal, and commits each
// row with its read position.
let store =
    SqlProjectionStore(
        ProjectionSqlDialect.Sqlite,
        Func<DbConnection>(fun () -> new SqliteConnection(connectionString)))
let options = TransactionalProjectionOptions("quote-approvals", store)
options.Entities <- [ "quote" ]
let approvals = Fcqrs.transactionalProjection api options Approvals.handle
// docs:end

// docs:instances
using (new SqliteConnection(connectionString)) (fun connection ->
    connection.Execute Instances.createTable |> ignore)

// A second projection, with its own name and progress.
let instanceOptions =
    TransactionalProjectionOptions("workflow-instances", store)
instanceOptions.Entities <- [ "quote" ]
let instances =
    Fcqrs.transactionalProjection api instanceOptions Instances.handle

let listInstances () =
    use connection = new SqliteConnection(connectionString)
    connection.Query<Instances.Instance>(
        "select workflow, id, status, last_step as LastStep
         from workflow_instances order by workflow, id")
    |> List.ofSeq
// docs:end

// docs:list
// The quotes that wait for a person. The list lags the workflows by one
// projection pass.
let waiting () =
    use connection = new SqliteConnection(connectionString)
    connection.Query<string>("select id from quote_approvals order by id")
    |> List.ofSeq
// docs:end

// docs:approve
// Approves one quote. Status asks the instance, which replays its journal
// first when it is not in memory.
let approve id =
    async {
        let! status = quotes.Status id
        if status.IsWaitingFor "approval" then
            do! Workflows.signal quotes id "approval" true
            return true
        else
            return false
    }
// docs:end

// Quantities of 10, 100, and 200 cost 125, 1250, and 2500. The two over
// 1000 wait for a person.
for id, quantity in [ "q-1", 10; "q-2", 100; "q-3", 200 ] do
    let request = { Customer = "Ada"; Symbol = "ABC"; Quantity = quantity }
    quotes.Start id request |> Async.RunSynchronously |> ignore

let result id =
    quotes.Result id (TimeSpan.FromSeconds 30.0) |> Async.RunSynchronously

// Waits until the quotes have fetched their price and reached the approval.
result "q-1" |> ignore
for id in [ "q-2"; "q-3" ] do
    while not ((quotes.Status id |> Async.RunSynchronously).IsWaitingFor "approval") do
        Thread.Sleep 50

approvals.CatchUpAsync().Wait()
printfn "waiting for approval: [%s]" (String.Join("; ", waiting ()))

for id in [ "q-1"; "q-2" ] do
    let approved = approve id |> Async.RunSynchronously
    printfn "approve %s: %b" id approved

match result "q-2" with
| WorkflowOutcome.Finished quote -> printfn "q-2: %s" quote.Receipt
| other -> printfn "q-2 ended without a quote: %A" other

approvals.CatchUpAsync().Wait()
printfn "waiting for approval: [%s]" (String.Join("; ", waiting ()))

instances.CatchUpAsync().Wait()
for instance in listInstances () do
    printfn "%s %s: %s (last step %s)"
        instance.Workflow instance.Id instance.Status instance.LastStep

api.Stop().Wait()
