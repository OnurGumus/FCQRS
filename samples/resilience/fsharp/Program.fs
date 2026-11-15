open System
open System.IO
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Actor
open FCQRS.FSharp
open FCQRS.Workflows
open Price

// A new journal for every run, so the price service is always called.
let database = Path.Combine(Path.GetTempPath(), $"fcqrs-resilience-{Guid.NewGuid():N}.db")
let logging = LoggerFactory.Create(fun _ -> ())
let configuration = ConfigurationBuilder().Build()
let connection = Fcqrs.connect DBType.Sqlite $"Data Source={database}"
let api = Fcqrs.actor configuration logging (Some connection) "resilience"

// A stand-in that fails its first two calls, as an overloaded service does.
let mutable calls = 0

let prices = {
    new IPriceService with
        member _.Fetch(_, symbol, _) =
            task {
                calls <- calls + 1
                if calls <= 2 then
                    printfn "  call %d: the service failed" calls
                    raise (InvalidOperationException "the price service is busy")
                printfn "  call %d: %s costs 12.5" calls symbol
                return 12.5m
            }
}

let flow = Workflows.register api (definition prices)

flow.Start "p-1" "ABC" |> Async.RunSynchronously |> ignore

let outcome = flow.Result "p-1" (TimeSpan.FromSeconds 30.0) |> Async.RunSynchronously

match outcome with
| WorkflowOutcome.Finished price ->
    // The breaker refused the attempts between the failures and the success,
    // so the service saw fewer calls than the step made attempts.
    printfn "price %M after %d calls to the service" price calls
| other ->
    printfn "the workflow did not finish: %A" other
    exit 1

api.Stop().Wait()
