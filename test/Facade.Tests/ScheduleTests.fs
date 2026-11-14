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
let private recorder (name: string) (runs: Runs) =
    Workflow.define name (fun (run: ScheduledRun<string>) -> workflow {
        do! activity "record" Retry.none (fun _ -> task { runs[runIdOf run] <- run })
        return run.Input
    })

let private remind (runs: Runs) = recorder "remind" runs

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
/// `bootWith`, with more settings, and the saga starters wired only when `wire` is true.
let private bootConfigured (db: string) (name: string) (shift: TimeSpan) (loggers: ILoggerFactory) (extra: (string * string) list) (wire: bool) (register: IActor -> ScheduledWorkflow list * WorkflowHandle<ScheduledRun<string>, string> option) =
    let settings =
        [ "config:akka:scheduler:implementation", "FCQRS.Scheduler+ShiftedScheduler, FCQRS"
          "config:akka:fcqrs:schedule-check-interval", "1" ]
        @ extra
    let configuration =
        VerifySerialization.configuration()
            .AddInMemoryCollection([ for key, value in settings -> KeyValuePair<string, string | null>(key, value) ])
            .Build()
    let api = Fcqrs.actor configuration loggers (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={db};")) name
    (api.System.Scheduler :?> FCQRS.Scheduler.ShiftedScheduler).Shift <- shift
    let runs, remind = register api
    let schedules = Schedules.register api runs
    if wire then Fcqrs.wireSagaStarters api []
    { Api = api; Remind = remind; Schedules = schedules }

let private bootWith (db: string) (name: string) (shift: TimeSpan) (loggers: ILoggerFactory) (register: IActor -> ScheduledWorkflow list * WorkflowHandle<ScheduledRun<string>, string> option) =
    bootConfigured db name shift loggers [] true register

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

/// Expects no run for a due time after `stored`. A run decided before a command may still start
/// after it, but its due time is no later than the command's stored time. `stored` is the time the
/// command returned, which is on the same clock and no earlier than the time it stored.
let private noRunAfter (runs: Runs) (stored: DateTime) what =
    let late = dues runs |> List.filter (fun due -> due > stored)
    Expect.isEmpty late $"{what}: no run for a due time after {stored:O}"

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
                Thread.Sleep 3000
                noRunAfter runs paused "while paused"
                eventually "the index shows the pause" (fun () -> (view node "p").Paused)
                Expect.isNone (view node "p").NextDue "a paused schedule has no next due time"
                let resuming = DateTime.UtcNow
                sync (Schedules.resume node.Schedules "p")
                eventually "runs start again" (fun () -> dues runs |> List.filter (fun due -> due > resuming) |> List.length >= 2)
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
                let refusals = node.Schedules.Dispatcher.Refusals
                sync (Schedules.pause node.Schedules "s")
                let paused = DateTime.UtcNow
                Thread.Sleep 3000
                Expect.isFalse (view node "s").Paused "the held index does not know of the pause"
                Expect.isGreaterThan node.Schedules.Dispatcher.Refusals refusals "the dispatcher fired from the stale index and was refused"
                noRunAfter runs paused "while the index is held"
                node.Schedules.Dispatcher.ReleaseIndex()
                eventually "the index shows the pause" (fun () -> (view node "s").Paused)
                Thread.Sleep 1500
                noRunAfter runs paused "after the index caught up"
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
                let deleted = DateTime.UtcNow
                Thread.Sleep 3000
                noRunAfter runs deleted "after the delete"
                eventually "the index drops the deleted schedule" (fun () -> not (hasView node "d"))
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


let private shiftTo (node: Node) (shift: TimeSpan) =
    (node.Api.System.Scheduler :?> FCQRS.Scheduler.ShiftedScheduler).Shift <- shift

let private nullLoggers = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance

/// Registers no workflow.
let private noWorkflows (_: IActor) = [], None

/// Registers "remind", recording into `runs`.
let private remindOnly (runs: Runs) (api: IActor) =
    let handle = Workflows.register api (remind runs)
    [ ScheduledWorkflow.ofHandle handle ], Some handle

/// Registers "remind" and "other", recording into `runs` and `others`.
let private remindAndOther (runs: Runs) (others: Runs) (api: IActor) =
    let handle = Workflows.register api (remind runs)
    let other = Workflows.register api (recorder "other" others)
    [ ScheduledWorkflow.ofHandle handle; ScheduledWorkflow.ofHandle other ], Some handle

/// Clock shift past the first due time of an hourly schedule.
let private pastFirstHour = hour + TimeSpan.FromSeconds 2.0

/// Creates the hourly schedule "k" for "remind", moves the clock past its first due time, and stops
/// the dispatcher right after that Firing is stored, as a crash would. `between` runs before the
/// actor system stops. Returns the due time decided.
let private crashAfterFiring db name register (between: Node -> unit) =
    let first = bootWith db name TimeSpan.Zero nullLoggers register
    let decided = Threading.Tasks.TaskCompletionSource<DateTime>()
    first.Schedules.Dispatcher.AfterFiring <- fun _ due ->
        decided.TrySetResult due |> ignore
        false
    try
        sync (Schedules.create first.Schedules "k" "remind" "x" (ScheduleRule.Every hour) CatchUp.Latest)
        shiftTo first pastFirstHour
        Expect.isTrue (decided.Task.Wait(TimeSpan.FromSeconds 20.0)) "the dispatcher decided the run"
        between first
        decided.Task.Result
    finally
        first.Stop()

/// Error lines that say a workflow is not registered.
let private unregisteredErrors (capture: Capture) =
    capture.Lines
    |> List.filter (fun (level, _, text) -> level = LogLevel.Error && text.Contains "is not registered")
    |> List.map (fun (_, _, text) -> text)
    |> List.distinct

let private allLogsEachDue =
    testCase "schedules: under CatchUp.All, a schedule whose workflow is not registered logs an error at each due time" <| fun _ ->
        withDb <| fun db ->
            let first = bootWith db "ScheduleAllLogs" TimeSpan.Zero nullLoggers (remindOnly (Runs()))
            try
                sync (Schedules.create first.Schedules "a" "remind" "x" (ScheduleRule.Every hour) CatchUp.All)
            finally
                first.Stop()

            let capture = new Capture()
            use loggers = LoggerFactory.Create(fun builder -> builder.AddProvider capture |> ignore)
            // Two due times have passed; the schedule would accept the older one.
            let second = bootWith db "ScheduleAllLogs" (TimeSpan.FromHours 2.5) loggers noWorkflows
            try
                eventually "an error for the due times passed" (fun () -> (unregisteredErrors capture).Length >= 1)
                Thread.Sleep 1500
                Expect.equal (unregisteredErrors capture).Length 1 "one error while no further due time passes"
                shiftTo second (TimeSpan.FromHours 3.5)
                eventually "a second error at the next due time" (fun () -> (unregisteredErrors capture).Length >= 2)
            finally
                second.Stop()

let private pendingLogsEachDue =
    testCase "schedules: a decided run whose workflow is not registered logs an error at each due time" <| fun _ ->
        withDb <| fun db ->
            crashAfterFiring db "SchedulePendingLogs" (remindOnly (Runs())) ignore |> ignore
            let capture = new Capture()
            use loggers = LoggerFactory.Create(fun builder -> builder.AddProvider capture |> ignore)
            let second = bootWith db "SchedulePendingLogs" pastFirstHour loggers noWorkflows
            try
                eventually "an error for the decided run" (fun () -> (unregisteredErrors capture).Length >= 1)
                Thread.Sleep 1500
                Expect.equal (unregisteredErrors capture).Length 1 "one error while no further due time passes"
                shiftTo second (pastFirstHour + hour)
                eventually "a second error at the next due time" (fun () -> (unregisteredErrors capture).Length >= 2)
                Expect.isFalse (view second "k").LastStarted "the run is still not started"
            finally
                second.Stop()

let private decidedWorkflowKept =
    testCase "schedules: a run decided before a change of workflow starts on the workflow it was decided with" <| fun _ ->
        withDb <| fun db ->
            let runs = Runs()
            let others = Runs()
            let due =
                crashAfterFiring db "ScheduleDecidedWorkflow" (remindAndOther runs others) (fun first ->
                    sync (Schedules.change first.Schedules "k" "other" "y" (ScheduleRule.Every hour) CatchUp.Latest))
            let second = bootWith db "ScheduleDecidedWorkflow" pastFirstHour nullLoggers (remindAndOther runs others)
            try
                let runId = $"""k@{due.ToString "O"}"""
                eventually "the run starts after the restart" (fun () -> runs.ContainsKey runId)
                eventually "its start is reported" (fun () -> (view second "k").LastStarted)
                Expect.equal runs[runId].Input "x" "the run has the input it was decided with"
                Expect.isEmpty others "the changed workflow started no run"
                Expect.equal (view second "k").Workflow "other" "the schedule names the new workflow"
            finally
                second.Stop()

let private deletedLeavesIndex =
    testCase "schedules: a deleted schedule stays in the index until its decided run has started" <| fun _ ->
        withDb <| fun db ->
            let runs = Runs()
            let due =
                crashAfterFiring db "ScheduleDeletedPending" (remindOnly runs) (fun first ->
                    sync (Schedules.delete first.Schedules "k")
                    eventually "the index shows the delete, with the run waiting" (fun () ->
                        match Schedules.state first.Schedules "k" with
                        | Some view -> view.Deleted && not view.LastStarted
                        | None -> false))
            let second = bootWith db "ScheduleDeletedPending" pastFirstHour nullLoggers (remindOnly runs)
            try
                eventually "the decided run starts after the restart" (fun () -> runs.ContainsKey $"""k@{due.ToString "O"}""")
                eventually "the index drops the schedule once the run started" (fun () -> not (hasView second "k"))
                Thread.Sleep 1500
                Expect.equal runs.Count 1 "no other run"
            finally
                second.Stop()

let private checkIntervalRejected =
    testCase "schedules: a check interval that is not positive is refused at registration" <| fun _ ->
        for value in [ "0"; "-5"; "0s" ] do
            withDb <| fun db ->
                let configuration =
                    VerifySerialization.configuration()
                        .AddInMemoryCollection([ KeyValuePair<string, string | null>("config:akka:fcqrs:schedule-check-interval", value) ])
                        .Build()
                let api = Fcqrs.actor configuration nullLoggers (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={db};")) "ScheduleInterval"
                try
                    let error = Expect.throwsC (fun () -> Schedules.register api [] |> ignore) id
                    Expect.isTrue (error :? Akka.Configuration.ConfigurationException) $"{value}: a configuration error, got {error.GetType()}"
                    Expect.stringContains error.Message "akka.fcqrs.schedule-check-interval" $"{value}: the error names the key"
                finally
                    api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

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

let private invalidRuleRaises =
    testCase "schedules: a rule or policy that cannot produce due times raises ArgumentException before anything is stored" <| fun _ ->
        withDb <| fun db ->
            let node = boot db "ScheduleInvalidRule" TimeSpan.Zero (Runs())
            try
                let at = TimeOnly(9, 0)
                let create rule policy = Schedules.create node.Schedules "v" "remind" "x" rule policy
                for rule in [ ScheduleRule.Daily(at, "Mars/Base")
                              ScheduleRule.Daily(at, "Europe")
                              ScheduleRule.Daily(at, "")
                              ScheduleRule.Weekly([], at, "UTC")
                              ScheduleRule.Weekly([ enum<DayOfWeek> 9 ], at, "UTC")
                              ScheduleRule.Every TimeSpan.Zero
                              ScheduleRule.Every(TimeSpan.FromDays 367.0) ] do
                    Expect.throwsT<ArgumentException> (fun () -> sync (create rule CatchUp.Latest)) $"create with {rule}"
                for rule in [ ScheduleRule.Daily(at, Unchecked.defaultof<string>); ScheduleRule.Weekly(Unchecked.defaultof<DayOfWeek list>, at, "UTC") ] do
                    Expect.throwsT<ArgumentException> (fun () -> sync (create rule CatchUp.Latest)) $"create with {rule}"
                Expect.throwsT<ArgumentNullException> (fun () -> sync (create Unchecked.defaultof<ScheduleRule> CatchUp.Latest)) "create with no rule"
                Expect.throwsT<ArgumentNullException> (fun () -> sync (create (ScheduleRule.Every hour) Unchecked.defaultof<CatchUp>)) "create with no policy"
                sync (create (ScheduleRule.Every hour) CatchUp.Latest)
                Expect.throwsT<ArgumentException> (fun () -> sync (Schedules.change node.Schedules "v" "remind" "x" (ScheduleRule.Daily(at, "Europe")) CatchUp.Latest)) "change to an unreadable zone"
                eventually "the index has the schedule" (fun () -> hasView node "v")
                Expect.equal (view node "v").Rule (ScheduleRule.Every hour) "only the valid create was stored"
            finally
                node.Stop()

/// The length of the longest persistence ID in the journal that starts with `prefix`.
let private longestPersistenceId (db: string) (prefix: string) =
    use connection = new SqliteConnection($"Data Source={db}")
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- "SELECT MAX(LENGTH(persistence_id)) FROM journal WHERE substr(persistence_id, 1, length($prefix)) = $prefix"
    command.Parameters.AddWithValue("$prefix", prefix) |> ignore
    Convert.ToInt32(command.ExecuteScalar())

let private longIdRaises =
    testCase "schedules: an ID whose escaped persistence IDs do not fit in 255 characters raises ArgumentException" <| fun _ ->
        withDb <| fun db ->
            let runs = Runs()
            let node = boot db "ScheduleLongId" TimeSpan.Zero runs
            try
                let create id = Schedules.create node.Schedules id "remind" "x" (ScheduleRule.Every second) CatchUp.Latest
                // A run's persistence ID is "remind/default-shard/" and the escaped run ID: 50
                // characters besides the escaped workflow name and schedule ID.
                Expect.throwsT<ArgumentException> (fun () -> sync (create (String('a', 200)))) "a run's persistence ID of 256 characters"
                // 'ş' escapes to "%C5%9F": 34 of them are 204 characters.
                Expect.throwsT<ArgumentException> (fun () -> sync (create (String('ş', 34)))) "a short ID that escapes long"
                let longest = String('a', 199)
                sync (create longest)
                Expect.throwsT<ArgumentException> (fun () -> sync (Schedules.change node.Schedules (String('a', 200)) "remind" "x" (ScheduleRule.Every second) CatchUp.Latest)) "change checks the length too"
                eventually "a run" (fun () -> runs.Count >= 1)
                Expect.equal (longestPersistenceId db "remind/") 255 "the run's persistence ID is 255 characters"
                Expect.equal (longestPersistenceId db "fcqrs-schedule/") (29 + 199) "the schedule's persistence ID is its prefix and the escaped ID"
            finally
                node.Stop()

let private unwiredWaits =
    testCase "schedules: before the saga starters are wired, commands raise and the dispatcher fires nothing" <| fun _ ->
        withDb <| fun db ->
            let runs = Runs()
            let first = boot db "ScheduleUnwired" TimeSpan.Zero runs
            try
                sync (Schedules.create first.Schedules "z" "remind" "x" (ScheduleRule.Every hour) CatchUp.Latest)
            finally
                first.Stop()

            // A short handshake timeout: an aggregate that stores an event before the starters are
            // wired terminates the process after it.
            let second =
                bootConfigured db "ScheduleUnwired" pastFirstHour nullLoggers [ "config:akka:fcqrs:saga-start-timeout", "2" ] false (remindOnly runs)
            try
                let error = Expect.throwsC (fun () -> sync (Schedules.create second.Schedules "y" "remind" "x" (ScheduleRule.Every hour) CatchUp.Latest)) id
                Expect.isTrue (error :? InvalidOperationException) $"an InvalidOperationException, got {error.GetType()}"
                Expect.stringContains error.Message "Fcqrs.wireSagaStarters" "the error names the call that wires them"
                Expect.throwsT<InvalidOperationException> (fun () -> sync (Schedules.pause second.Schedules "z")) "a pause raises too"
                eventually "the index has the schedule" (fun () -> hasView second "z")
                Thread.Sleep 5000
                Expect.isEmpty runs "the due run waits for the starters"
                Fcqrs.wireSagaStarters second.Api []
                eventually "the run starts once they are wired" (fun () -> runs.Count = 1)
            finally
                second.Stop()

let private stuckStartAlone =
    testCase "schedules: a start that hangs delays only its own schedule, which is tried again a retry delay after it fails" <| fun _ ->
        withDb <| fun db ->
            let runs = Runs()
            let began = ConcurrentQueue<DateTime>()
            let failed = ConcurrentQueue<DateTime>()
            // A start that answers only after four seconds, with the timeout an ask would raise.
            let hanging =
                ScheduledWorkflow("hang", typeof<string>, fun _ _ _ _ ->
                    async {
                        began.Enqueue DateTime.UtcNow
                        do! Async.Sleep 4000
                        failed.Enqueue DateTime.UtcNow
                        return raise (TimeoutException "The start did not answer.")
                    })
            let node =
                bootWith db "ScheduleStuck" TimeSpan.Zero nullLoggers (fun api ->
                    let handle = Workflows.register api (remind runs)
                    [ ScheduledWorkflow.ofHandle handle; hanging ], Some handle)
            try
                sync (Schedules.create node.Schedules "stuck" "hang" "x" (ScheduleRule.Every second) CatchUp.Latest)
                eventually "the hanging start began" (fun () -> not began.IsEmpty)
                sync (Schedules.create node.Schedules "free" "remind" "y" (ScheduleRule.Every second) CatchUp.Latest)
                Thread.Sleep 8000
                let free = runs.Values |> Seq.filter (fun run -> run.Schedule = "free") |> Seq.length
                Expect.isGreaterThanOrEqual free 6 "the other schedule ran about once a second meanwhile"
                eventually "the hanging start was tried again" (fun () -> began.Count >= 2)
                let attempts = List.ofSeq began
                let failures = List.ofSeq failed
                // retryDelay is min(check interval, 5 s): one second here.
                for retried, failure in List.zip (List.tail attempts) (List.take (attempts.Length - 1) failures) do
                    Expect.isGreaterThanOrEqual (retried - failure).TotalMilliseconds 900.0 "a failed start waits the retry delay from its failure"
            finally
                node.Stop()

let private weeklyDaysNormalized =
    testCase "schedules: the days of a weekly rule are sorted and repeats removed, so a reordered create is the same content" <| fun _ ->
        withDb <| fun db ->
            let node = boot db "ScheduleWeekly" TimeSpan.Zero (Runs())
            try
                let weekly days = ScheduleRule.Weekly(days, TimeOnly(9, 0), "UTC")
                let create days = Schedules.create node.Schedules "wk" "remind" "x" (weekly days) CatchUp.Latest
                sync (create [ DayOfWeek.Thursday; DayOfWeek.Monday ])
                sync (create [ DayOfWeek.Monday; DayOfWeek.Thursday; DayOfWeek.Monday ])
                eventually "the index has the schedule" (fun () -> hasView node "wk")
                Expect.equal (view node "wk").Rule (weekly [ DayOfWeek.Monday; DayOfWeek.Thursday ]) "the stored days are sorted, without repeats"
            finally
                node.Stop()

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
            allLogsEachDue
            pendingLogsEachDue
            decidedWorkflowKept
            deletedLeavesIndex
            checkIntervalRejected
            downtime CatchUp.Latest 1
            downtime CatchUp.All 3
            downtime CatchUp.None 0
            failedRunContinues
            snapshotRecovery
            invalidRuleRaises
            longIdRaises
            unwiredWaits
            stuckStartAlone
            weeklyDaysNormalized
        ]
    )
