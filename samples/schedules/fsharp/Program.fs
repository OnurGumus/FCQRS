open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Actor
open FCQRS.FSharp
open FCQRS.Workflows
open Jobs

// The journal and the schedules' index are in this file.
let database = Path.Combine(AppContext.BaseDirectory, "jobs.db")
let logging = LoggerFactory.Create(fun _ -> ())
let configuration = ConfigurationBuilder().Build()
let connection = Fcqrs.connect DBType.Sqlite $"Data Source={database}"
let api = Fcqrs.actor configuration logging (Some connection) "jobs"

// Stand-ins for the backup store and the billing system. Each remembers the
// keys it has seen, in memory only.
let copies = ConcurrentDictionary<string, string>()

let store = {
    new IBackupStore with
        member _.Copy(key, folder) =
            task {
                let copied = copies.TryAdd(key, folder)
                if copied then printfn "  copied %s" folder
                return copied
            }
}

let invoices = ConcurrentDictionary<string, string>()

let billing = {
    new IBilling with
        member _.Issue(number, customer) =
            task {
                let issued = invoices.TryAdd(number, customer)
                if issued then printfn "  issued invoice %s" number
                return issued
            }
}

// Waits for a condition, for at most 30 seconds.
let waitFor (what: string) (condition: unit -> bool) =
    let deadline = DateTime.UtcNow.AddSeconds 30.0
    while not (condition ()) do
        if DateTime.UtcNow > deadline then
            failwith $"timed out waiting for {what}"
        Thread.Sleep 100

// docs:register
let backups = Workflows.register api (backup store)
let invoicing = Workflows.register api (invoice billing)

// The dispatcher starts only the workflows in this list.
let schedules =
    Schedules.register api [
        ScheduledWorkflow.ofHandle backups
        ScheduledWorkflow.ofHandle invoicing
    ]

// A schedule is an aggregate, and an aggregate stores nothing until the saga
// starters are wired. Wire them after the last registration.
Fcqrs.wireSagaStarters api []
// docs:end

// docs:create
// A create with the content of the stored schedule stores nothing, so the
// program creates its schedules at every start.

// After downtime, only the latest backup matters: CatchUp.Latest.
Schedules.createLatest schedules "nightly-backup" "backup" "/srv/app/data"
    (ScheduleRule.Daily(TimeOnly(2, 0), zone))
|> Async.RunSynchronously

// Every week must be invoiced: CatchUp.All runs each missed Monday.
Schedules.create schedules "weekly-invoice" "invoice" "Ada"
    (ScheduleRule.Weekly([ DayOfWeek.Monday ], TimeOnly(8, 0), zone))
    CatchUp.All
|> Async.RunSynchronously
// docs:end

// docs:state
// The index reads the schedules through a projection, so a schedule appears
// in it shortly after its create returns. NextDue is in UTC.
let nextRun (id: string) =
    waitFor id (fun () -> (Schedules.state schedules id).IsSome)
    let next = (Schedules.state schedules id).Value.NextDue.Value
    TimeZoneInfo.ConvertTimeFromUtc(
        next, TimeZoneInfo.FindSystemTimeZoneById zone)

let backupAt = nextRun "nightly-backup"
printfn "nightly-backup: next run at %s %s time"
    (backupAt.ToString "HH:mm") zone
let invoiceAt = nextRun "weekly-invoice"
printfn "weekly-invoice: next run on %O at %s %s time"
    invoiceAt.DayOfWeek (invoiceAt.ToString "HH:mm") zone
// docs:end

// docs:demo
// A deleted schedule's ID cannot be used again, so each start creates a demo
// schedule with a new ID. CatchUp.All runs every due time, even one whose
// run starts late.
let demo = $"demo-{Guid.NewGuid():N}"
printfn "demo: a backup every 2 seconds"

Schedules.create schedules demo "backup" "demo-data"
    (ScheduleRule.Every(TimeSpan.FromSeconds 2.0)) CatchUp.All
|> Async.RunSynchronously

let runs () =
    copies.Keys |> Seq.filter (fun key -> key.Contains demo) |> Seq.length

waitFor "three demo runs" (fun () -> runs () >= 3)
// An Every rule counts from the creation.
let created = (Schedules.state schedules demo).Value.Anchor
Schedules.delete schedules demo |> Async.RunSynchronously
printfn "demo: deleted"

// A run's workflow ID is the schedule's ID, "@", and the due time in UTC.
for run in 1..3 do
    let due = created.AddSeconds(2.0 * float run)
    let id = $"""{demo}@{due.ToString "O"}"""
    let outcome =
        backups.Result id (TimeSpan.FromSeconds 10.0) |> Async.RunSynchronously
    let copied =
        match outcome with
        | WorkflowOutcome.Finished copied -> copied
        | other -> failwith $"{id} ended with {other}"
    let result = if copied then "copied" else "already copied"
    printfn $"demo: run {run}, {2 * run} s after creation: {result}"

// A run decided before the delete still starts. The schedule leaves the index
// once no such run is waiting.
waitFor "the demo schedule to leave the index" (fun () ->
    (Schedules.state schedules demo).IsNone)
// docs:end

printfn "demo: no run is waiting"
api.Stop().Wait()
