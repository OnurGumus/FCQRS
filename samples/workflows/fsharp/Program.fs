open System
open System.IO
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Actor
open FCQRS.FSharp
open FCQRS.Workflows
open Quote

// `--crash` stops the process in the middle of the last step.
let crash = Environment.GetCommandLineArgs() |> Array.contains "--crash"

let database = Path.Combine(AppContext.BaseDirectory, "quotes.db")
let logging = LoggerFactory.Create(fun _ -> ())
let configuration = ConfigurationBuilder().Build()
let connection = Fcqrs.connect DBType.Sqlite $"Data Source={database}"
let api = Fcqrs.actor configuration logging (Some connection) "quotes"

// Stand-ins for external systems. The price service fails twice.
let mutable priceCalls = 0

let prices =
    { new IPriceService with
        member _.Fetch(key, symbol) =
            task {
                priceCalls <- priceCalls + 1
                printfn "  price service called for %s (key %s)" symbol key
                if priceCalls < 3 then
                    failwith "the price service is busy"
                return 12.5m
            } }

let mailer =
    { new IMailer with
        member _.Send(key, customer, total) =
            task {
                printfn "  mailer called (key %s)" key
                if crash then
                    printfn "  the process stops before this step is stored"
                    exit 1
                return $"quote of {total} sent to {customer}"
            } }

// docs:register
// Registering the workflow also resumes the instances that were running when
// the process last stopped.
let quotes = Workflows.register api (definition prices mailer)
// docs:end

// docs:start
let id = "q-1001"
let request = { Customer = "Ada"; Symbol = "ABC"; Quantity = 100 }

match quotes.Start id request |> Async.RunSynchronously with
| StartResult.Started ->
    printfn "started %s" id
    // The quote is over 1000, so the workflow waits for this signal.
    Workflows.signal quotes id "approval" true |> Async.RunSynchronously
| StartResult.AlreadyStarted status ->
    printfn "%s is already in the journal: %A" id status

// Waits for the workflow to end. The workflow does not depend on this wait.
let outcome =
    quotes.Result id (TimeSpan.FromSeconds 30.0) |> Async.RunSynchronously
// docs:end

match outcome with
| WorkflowOutcome.Finished quote ->
    printfn "finished: %s (approved: %b)" quote.Receipt quote.Approved
| other -> printfn "ended without a quote: %A" other

printfn "price service calls in this run: %d" priceCalls
api.Stop().Wait()
