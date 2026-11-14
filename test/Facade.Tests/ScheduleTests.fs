module ScheduleTests

// Schedules on a real actor system: the dispatcher starts a run at each due time, and pause, change,
// delete, a stale index, a dropped workflow, a crash between deciding a run and starting it, and
// downtime under each catch-up policy behave as the design says. Tests that move time use
// ShiftedScheduler and a one-second check interval, so the dispatcher notices the jump.

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Threading
open Expecto
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Common
open FCQRS.FSharp
open FCQRS.Workflows

/// The runs a test saw, by run ID.
type private Runs = ConcurrentDictionary<string, ScheduledRun<string>>

/// A logger provider that keeps every line.
type private Capture() =
    let lines = ConcurrentQueue<LogLevel * string * string>()
    member _.Lines = List.ofSeq lines
    interface ILoggerProvider with
        member _.CreateLogger(category) =
            { new ILogger with
                member _.BeginScope<'TState when 'TState: not null>(_state: 'TState) : IDisposable | null = null
                member _.IsEnabled(_level) = true
                member _.Log<'TState>(level, _eventId, state: 'TState, error: exn | null, formatter: Func<'TState, exn | null, string>) =
                    lines.Enqueue(level, category, formatter.Invoke(state, error)) }
        member _.Dispose() = ()

let private newDb () = Path.Combine(Path.GetTempPath(), $"fcqrs_schedule_{Guid.NewGuid():N}.db")

let private deleteDb (db: string) =
    SqliteConnection.ClearAllPools()
    for path in [ db; db + "-wal"; db + "-shm" ] do
        if File.Exists path then File.Delete path

let private withDb (run: string -> unit) =
    let db = newDb ()
    try run db finally deleteDb db

let private sync (work: Async<'T>) = Async.RunSynchronously(work, 30000)

let private eventuallyWithin (seconds: float) what (condition: unit -> bool) =
    let deadline = DateTime.UtcNow.AddSeconds seconds
    while not (condition ()) && DateTime.UtcNow < deadline do
        Thread.Sleep 50
    Expect.isTrue (condition ()) what

let private eventually what condition = eventuallyWithin 20.0 what condition

let private second = TimeSpan.FromSeconds 1.0
let private hour = TimeSpan.FromHours 1.0

let private runIdOf (run: ScheduledRun<string>) = $"""{run.Schedule}@{run.Due.ToString "O"}"""

/// Records its input in `runs` under the run ID it should have, and returns the input.
let private remind (runs: Runs) =
    Workflow.define "remind" (fun (run: ScheduledRun<string>) -> workflow {
        do! activity "record" Retry.none (fun _ -> task { runs[runIdOf run] <- run })
        return run.Input
    })

/// Records its input and then fails.
let private broken (runs: Runs) =
    Workflow.define "broken" (fun (run: ScheduledRun<string>) -> workflow {
        do! activity "record" Retry.none (fun _ -> task { runs[runIdOf run] <- run })
        failwith "the run failed"
        return ()
    })

type private Node =
    { Api: IActor
      Remind: WorkflowHandle<ScheduledRun<string>, string> option
      Schedules: ScheduleHandle }

    member this.Stop() = this.Api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

/// Boots an actor system on `db`, moves its clock by `shift`, registers the workflows `register`
/// returns, and the dispatcher.
let private bootWith (db: string) (name: string) (shift: TimeSpan) (loggers: ILoggerFactory) (register: IActor -> ScheduledWorkflow list * WorkflowHandle<ScheduledRun<string>, string> option) =
    let settings =
        [ "config:akka:scheduler:implementation", "FCQRS.Scheduler+ShiftedScheduler, FCQRS"
          "config:akka:fcqrs:schedule-check-interval", "1" ]
    let configuration =
        VerifySerialization.configuration()
            .AddInMemoryCollection([ for key, value in settings -> KeyValuePair<string, string | null>(key, value) ])
            .Build()
    let api = Fcqrs.actor configuration loggers (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={db};")) name
    (api.System.Scheduler :?> FCQRS.Scheduler.ShiftedScheduler).Shift <- shift
    let runs, remind = register api
    let schedules = Schedules.register api runs
    Fcqrs.wireSagaStarters api []
    { Api = api; Remind = remind; Schedules = schedules }

let private boot db name shift (runs: Runs) =
    bootWith db name shift Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance (fun api ->
        let handle = Workflows.register api (remind runs)
        [ ScheduledWorkflow.ofHandle handle ], Some handle)

let private view (node: Node) id =
    match Schedules.state node.Schedules id with
    | Some view -> view
    | None -> failtest $"no view of {id}"

let private hasView (node: Node) id = (Schedules.state node.Schedules id).IsSome

let private dues (runs: Runs) = runs.Values |> Seq.map _.Due |> Seq.sort |> List.ofSeq

let private everyFires =
    testCase "schedules: an every-second schedule starts a run at each due time, with the due time in its ID and input" <| fun _ ->
        withDb <| fun db ->
            let runs = Runs()
            let node = boot db "ScheduleEvery" TimeSpan.Zero runs
            try
                sync (Schedules.create node.Schedules "ada daily" "remind" "drink water" (ScheduleRule.Every second) CatchUp.Latest)
                eventuallyWithin 5.0 "three runs within 5 s" (fun () -> runs.Count >= 3)
                for KeyValue(id, run) in runs do
                    Expect.equal run.Schedule "ada daily" "the run names its schedule"
                    Expect.equal run.Input "drink water" "the run gets the schedule's input"
                    Expect.equal run.Due.Kind DateTimeKind.Utc "the due time is UTC"
                    Expect.equal id $"""ada daily@{run.Due.ToString "O"}""" "the run ID is the schedule ID and the due time"
                    Expect.notEqual (sync (node.Remind.Value.Status id)) WorkflowStatus.NotStarted "a workflow with that ID exists"
                let dues = dues runs
                let created = (view node "ada daily").Anchor
                for due in dues do
                    Expect.equal ((due - created).Ticks % second.Ticks) 0L "every due time is a whole number of intervals after the creation"
                Expect.equal (List.distinct dues) dues "one run per due time"
            finally
                node.Stop()

let private pauseStops =
    testCase "schedules: a paused schedule starts no run, and a resumed one skips the due times inside the pause" <| fun _ ->
        withDb <| fun db ->
            let runs = Runs()
            let node = boot db "SchedulePause" TimeSpan.Zero runs
            try
                sync (Schedules.create node.Schedules "p" "remind" "x" (ScheduleRule.Every second) CatchUp.All)
                eventually "a first run" (fun () -> runs.Count >= 1)
                sync (Schedules.pause node.Schedules "p")
                let paused = DateTime.UtcNow
                // A run decided before the pause still starts.
                Thread.Sleep 500
                let before = runs.Count
                Thread.Sleep 3000
                Expect.equal runs.Count before "no run starts while paused"
                eventually "the index shows the pause" (fun () -> (view node "p").Paused)
                Expect.isNone (view node "p").NextDue "a paused schedule has no next due time"
                let resuming = DateTime.UtcNow
                sync (Schedules.resume node.Schedules "p")
                eventually "runs start again" (fun () -> runs.Count >= before + 2)
                for due in dues runs do
                    Expect.isTrue (due <= paused || due > resuming) $"no run for {due:O}, inside the pause"
            finally
                node.Stop()

let private staleIndexRefused =
    testCase "schedules: a schedule paused while the index is held back starts no run, and the index catches up" <| fun _ ->
        withDb <| fun db ->
            let runs = Runs()
            let node = boot db "ScheduleStale" TimeSpan.Zero runs
            try
                sync (Schedules.create node.Schedules "s" "remind" "x" (ScheduleRule.Every second) CatchUp.Latest)
                eventually "a first run" (fun () -> runs.Count >= 1)
                node.Schedules.Dispatcher.HoldIndex()
                sync (Schedules.pause node.Schedules "s")
                Thread.Sleep 500
                let before = runs.Count
                let refusals = node.Schedules.Dispatcher.Refusals
                Thread.Sleep 3000
                Expect.isFalse (view node "s").Paused "the held index does not know of the pause"
                Expect.isGreaterThan node.Schedules.Dispatcher.Refusals refusals "the dispatcher fired from the stale index and was refused"
                Expect.equal runs.Count before "no run starts"
                node.Schedules.Dispatcher.ReleaseIndex()
                eventually "the index shows the pause" (fun () -> (view node "s").Paused)
                Thread.Sleep 1500
                Expect.equal runs.Count before "still no run"
            finally
                node.Stop()

let private changeApplies =
    testCase "schedules: a change gives later runs the new input and interval" <| fun _ ->
        withDb <| fun db ->
            let runs = Runs()
            let node = boot db "ScheduleChange" TimeSpan.Zero runs
            try
                sync (Schedules.create node.Schedules "c" "remind" "a" (ScheduleRule.Every second) CatchUp.Latest)
                eventually "a first run" (fun () -> runs.Count >= 1)
                sync (Schedules.change node.Schedules "c" "remind" "b" (ScheduleRule.Every(TimeSpan.FromSeconds 2.0)) CatchUp.Latest)
                let changed = DateTime.UtcNow
                eventually "two runs with the new input" (fun () -> runs.Values |> Seq.filter (fun run -> run.Input = "b") |> Seq.length >= 2)
                let view = view node "c"
                Expect.equal view.Rule (ScheduleRule.Every(TimeSpan.FromSeconds 2.0)) "the index has the new rule"
                for run in runs.Values do
                    if run.Due > changed then
                        Expect.equal run.Input "b" $"the run for {run.Due:O} has the new input"
                        Expect.equal ((run.Due - view.Anchor).Ticks % (2L * second.Ticks)) 0L "it counts two seconds from the change"
                    elif run.Input = "b" then
                        Expect.isTrue (run.Due >= view.Anchor) "a new-input run is after the change"
            finally
                node.Stop()

let private deleteStops =
    testCase "schedules: a deleted schedule starts no run and takes no command" <| fun _ ->
        withDb <| fun db ->
            let runs = Runs()
            let node = boot db "ScheduleDelete" TimeSpan.Zero runs
            try
                sync (Schedules.create node.Schedules "d" "remind" "x" (ScheduleRule.Every second) CatchUp.Latest)
                eventually "a first run" (fun () -> runs.Count >= 1)
                sync (Schedules.delete node.Schedules "d")
                Thread.Sleep 500
                let before = runs.Count
                Thread.Sleep 3000
                Expect.equal runs.Count before "no run starts after the delete"
                eventually "the index shows the delete" (fun () -> (view node "d").Deleted)
                Expect.throwsT<InvalidOperationException> (fun () -> sync (Schedules.pause node.Schedules "d")) "a pause is refused"
                sync (Schedules.delete node.Schedules "d")
            finally
                node.Stop()

let private createDeletedRefused =
    testCase "schedules: a create for a deleted ID is refused, and a repeated create is accepted" <| fun _ ->
        withDb <| fun db ->
            let runs = Runs()
            let node = boot db "ScheduleRecreate" TimeSpan.Zero runs
            try
                let create input = Schedules.create node.Schedules "r" "remind" input (ScheduleRule.Every hour) CatchUp.Latest
                sync (create "x")
                sync (create "x")
                Expect.throwsT<InvalidOperationException> (fun () -> sync (create "y")) "a different content for the ID is refused"
                sync (Schedules.delete node.Schedules "r")
                Expect.throwsT<InvalidOperationException> (fun () -> sync (create "x")) "the deleted ID is refused"
                Expect.throwsT<InvalidOperationException> (fun () -> sync (Schedules.pause node.Schedules "nobody")) "a schedule that does not exist is refused"
            finally
                node.Stop()

let private unregisteredRefused =
    testCase "schedules: a create naming an unregistered workflow, a wrong input type, or a bad ID raises" <| fun _ ->
        withDb <| fun db ->
            let runs = Runs()
            let node = boot db "ScheduleUnregistered" TimeSpan.Zero runs
            try
                let every = ScheduleRule.Every hour
                Expect.throwsT<InvalidOperationException> (fun () -> sync (Schedules.create node.Schedules "u" "missing" "x" every CatchUp.Latest)) "an unregistered workflow"
                Expect.throwsT<InvalidOperationException> (fun () -> sync (Schedules.create node.Schedules "u" "remind" 42 every CatchUp.Latest)) "an input the workflow does not take"
                Expect.throwsT<ArgumentException> (fun () -> sync (Schedules.create node.Schedules "a~b" "remind" "x" every CatchUp.Latest)) "an ID with '~'"
                Expect.throwsT<ArgumentException> (fun () -> sync (Schedules.create node.Schedules " " "remind" "x" every CatchUp.Latest)) "a blank ID"
                sync (Schedules.create node.Schedules "u" "remind" "x" every CatchUp.Latest)
                Expect.throwsT<InvalidOperationException> (fun () -> sync (Schedules.change node.Schedules "u" "missing" "x" every CatchUp.Latest)) "a change to an unregistered workflow"
                Expect.throwsT<InvalidOperationException> (fun () -> Schedules.register node.Api [] |> ignore) "a second dispatcher on one actor system"
            finally
                node.Stop()

let private droppedWorkflowWaits =
    testCase "schedules: a schedule whose workflow is not registered logs an error and fires nothing until it is" <| fun _ ->
        withDb <| fun db ->
            let runs = Runs()
            let first = boot db "ScheduleDropped" TimeSpan.Zero runs
            try
                sync (Schedules.create first.Schedules "w" "remind" "x" (ScheduleRule.Every second) CatchUp.Latest)
                eventually "a first run" (fun () -> runs.Count >= 1)
            finally
                first.Stop()

            let capture = new Capture()
            use loggers = LoggerFactory.Create(fun builder -> builder.AddProvider capture |> ignore)
            let second = bootWith db "ScheduleDropped" TimeSpan.Zero loggers (fun _ -> [], None)
            let lastDue =
                try
                    eventually "the index has the schedule" (fun () -> hasView second "w")
                    let lastDue = (view second "w").LastDue
                    Thread.Sleep 3000
                    Expect.equal (view second "w").LastDue lastDue "no fire is accepted"
                    let errors =
                        capture.Lines |> List.filter (fun (level, _, text) -> level = LogLevel.Error && text.Contains "remind" && text.Contains "w")
                    Expect.isNonEmpty errors "an error names the schedule and its workflow"
                    lastDue
                finally
                    second.Stop()

            let restarted = DateTime.UtcNow
            let third = boot db "ScheduleDropped" TimeSpan.Zero runs
            try
                eventually "runs resume when the workflow is registered" (fun () -> dues runs |> List.exists (fun due -> due > restarted))
                Expect.isTrue ((view third "w").LastDue > lastDue) "a later due time was accepted"
            finally
                third.Stop()

let private crashBetween =
    testCase "schedules: a crash after a run is decided and before it starts starts the run once after the restart" <| fun _ ->
        withDb <| fun db ->
            let runs = Runs()
            let first = boot db "ScheduleCrash" TimeSpan.Zero runs
            let decided = Threading.Tasks.TaskCompletionSource<string * DateTime>()
            first.Schedules.Dispatcher.AfterFiring <- fun id due ->
                decided.TrySetResult((id, due)) |> ignore
                false
            let shift = hour + TimeSpan.FromSeconds 2.0
            let id, due =
                try
                    sync (Schedules.create first.Schedules "k" "remind" "x" (ScheduleRule.Every hour) CatchUp.Latest)
                    (first.Api.System.Scheduler :?> FCQRS.Scheduler.ShiftedScheduler).Shift <- shift
                    Expect.isTrue (decided.Task.Wait(TimeSpan.FromSeconds 20.0)) "the dispatcher decided the run"
                    decided.Task.Result
                finally
                    first.Stop()
            Expect.equal runs.Count 0 "no run started before the crash"

            let second = boot db "ScheduleCrash" shift runs
            try
                let runId = $"""k@{due.ToString "O"}"""
                eventually "the run starts after the restart" (fun () -> runs.ContainsKey runId)
                eventually "its start is reported" (fun () -> let view = view second id in view.LastDue = Some due && view.LastStarted)
                Expect.equal runs.Count 1 "exactly one run"
                Expect.equal (sync (second.Remind.Value.Result runId (TimeSpan.FromSeconds 20.0))) (WorkflowOutcome.Finished "x") "the run finished"
            finally
                second.Stop()

/// Creates an hourly schedule, stops, moves the clock 3.5 hours and restarts: three due times were
/// missed while nothing ran.
let private downtime (policy: CatchUp) (expected: int) =
    testCase $"schedules: after downtime over three due times, {policy} starts {expected} runs" <| fun _ ->
        withDb <| fun db ->
            let runs = Runs()
            let name = $"ScheduleDowntime{policy}"
            let first = boot db name TimeSpan.Zero runs
            let anchor =
                try
                    sync (Schedules.create first.Schedules "h" "remind" "x" (ScheduleRule.Every hour) policy)
                    eventually "the index has the schedule" (fun () -> hasView first "h")
                    (view first "h").Anchor
                finally
                    first.Stop()

            let second = boot db name (TimeSpan.FromHours 3.5) runs
            try
                eventually "the index has the schedule" (fun () -> hasView second "h")
                if expected > 0 then eventually $"{expected} runs" (fun () -> runs.Count >= expected)
                Thread.Sleep 3000
                Expect.equal runs.Count expected $"{expected} runs, no more"
                let missed = [ for k in 4 - expected .. 3 -> anchor + TimeSpan.FromHours(float k) ]
                Expect.equal (dues runs) missed "the runs are for the missed due times the policy keeps"
                Expect.equal (view second "h").NextDue (Some(anchor + TimeSpan.FromHours 4.0)) "the next due time is the first in the future"
            finally
                second.Stop()

let private failedRunContinues =
    testCase "schedules: a run that faults does not stop the schedule" <| fun _ ->
        withDb <| fun db ->
            let runs = Runs()
            let mutable handle = None
            let node =
                bootWith db "ScheduleFault" TimeSpan.Zero Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance (fun api ->
                    let failing = Workflows.register api (broken runs)
                    handle <- Some failing
                    [ ScheduledWorkflow.ofHandle failing ], None)
            try
                sync (Schedules.create node.Schedules "f" "broken" "x" (ScheduleRule.Every second) CatchUp.Latest)
                eventually "two runs" (fun () -> runs.Count >= 2)
                for id in runs.Keys do
                    eventually $"{id} faulted" (fun () ->
                        match sync (handle.Value.Status id) with
                        | WorkflowStatus.Failed _ -> true
                        | _ -> false)
            finally
                node.Stop()

let private snapshotRecovery =
    testCase "schedules: a schedule recovers from its snapshot and keeps firing" <| fun _ ->
        withDb <| fun db ->
            let runs = Runs()
            let first = boot db "ScheduleSnapshot" TimeSpan.Zero runs
            let anchor =
                try
                    sync (Schedules.create first.Schedules "n" "remind" "x" (ScheduleRule.Every hour) CatchUp.All)
                    eventually "the index has the schedule" (fun () -> hasView first "n")
                    (view first "n").Anchor
                finally
                    first.Stop()

            // Twenty missed runs store 41 events, past the default snapshot interval of 30.
            let second = boot db "ScheduleSnapshot" (TimeSpan.FromHours 20.5) runs
            try
                eventuallyWithin 60.0 "twenty runs" (fun () -> runs.Count >= 20)
            finally
                second.Stop()

            let third = boot db "ScheduleSnapshot" (TimeSpan.FromHours 21.5) runs
            try
                eventually "the twenty-first run" (fun () -> runs.Count >= 21)
                Expect.equal (List.last (dues runs)) (anchor + TimeSpan.FromHours 21.0) "the next due time after the snapshot"
                Expect.equal runs.Count 21 "one run per due time"
            finally
                third.Stop()

let tests =
    testSequenced (
        testList "schedules" [
            everyFires
            pauseStops
            staleIndexRefused
            changeApplies
            deleteStops
            createDeletedRefused
            unregisteredRefused
            droppedWorkflowWaits
            crashBetween
            downtime CatchUp.Latest 1
            downtime CatchUp.All 3
            downtime CatchUp.None 0
            failedRunContinues
            snapshotRecovery
        ]
    )
