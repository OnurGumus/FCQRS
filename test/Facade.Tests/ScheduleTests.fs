module ScheduleTests

// Schedules on a real actor system: the dispatcher starts a run at each due time, and pause, change,
// delete, a stale index, a dropped workflow, a crash between deciding a run and starting it, and
// downtime under each catch-up policy behave as the design says. Tests that move time use
// ShiftedScheduler and a one-second check interval, so the dispatcher notices the jump.

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Data.Common
open System.IO
open System.Threading
open Expecto
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open Npgsql
open FCQRS.Common
open FCQRS.FSharp
open FCQRS.Workflows

/// The runs a test saw, by run ID.
type private Runs = ConcurrentDictionary<string, ScheduledRun<string>>

/// A logger provider that keeps every line.
type private Capture() =
    let lines = ConcurrentQueue<LogLevel * string * string>()
    let errors = ConcurrentQueue<LogLevel * string * (exn | null)>()
    member _.Lines = List.ofSeq lines
    /// Every line with the exception it was logged with.
    member _.Errors = List.ofSeq errors
    interface ILoggerProvider with
        member _.CreateLogger(category) =
            { new ILogger with
                member _.BeginScope<'TState when 'TState: not null>(_state: 'TState) : IDisposable | null = null
                member _.IsEnabled(_level) = true
                member _.Log<'TState>(level, _eventId, state: 'TState, error: exn | null, formatter: Func<'TState, exn | null, string>) =
                    let text = formatter.Invoke(state, error)
                    lines.Enqueue(level, category, text)
                    errors.Enqueue(level, text, error) }
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

/// Boots an actor system on `connection`, moves its clock by `shift`, registers the workflows
/// `register` returns, and the dispatcher, with more settings, and the saga starters wired only
/// when `wire` is true.
let private bootOn (connection: FCQRS.Actor.Connection) (name: string) (shift: TimeSpan) (loggers: ILoggerFactory) (extra: (string * string) list) (wire: bool) (register: IActor -> ScheduledWorkflow list * WorkflowHandle<ScheduledRun<string>, string> option) =
    let settings =
        [ "config:akka:scheduler:implementation", "FCQRS.Scheduler+ShiftedScheduler, FCQRS"
          "config:akka:fcqrs:schedule-check-interval", "1" ]
        |> List.filter (fun (key, _) -> not (List.exists (fun (given, _) -> given = key) extra))
        |> fun defaults -> defaults @ extra
    let configuration =
        VerifySerialization.configuration()
            .AddInMemoryCollection([ for key, value in settings -> KeyValuePair<string, string | null>(key, value) ])
            .Build()
    let api = Fcqrs.actor configuration loggers (Some connection) name
    (api.System.Scheduler :?> FCQRS.Scheduler.ShiftedScheduler).Shift <- shift
    let runs, remind = register api
    let schedules = Schedules.register api runs
    if wire then Fcqrs.wireSagaStarters api []
    { Api = api; Remind = remind; Schedules = schedules }

/// `bootOn` on the SQLite file `db`.
let private bootConfigured (db: string) (name: string) (shift: TimeSpan) (loggers: ILoggerFactory) (extra: (string * string) list) (wire: bool) (register: IActor -> ScheduledWorkflow list * WorkflowHandle<ScheduledRun<string>, string> option) =
    bootOn (Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={db};") name shift loggers extra wire register

/// Boots an actor system on `db` with the saga starters wired.
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

/// Registers "remind" with an `int` input, which the stored input "x" cannot be read as, recording
/// into `runs`.
let private remindInt (runs: ConcurrentDictionary<string, ScheduledRun<int>>) (api: IActor) =
    let definition =
        Workflow.define "remind" (fun (run: ScheduledRun<int>) -> workflow {
            do! activity "record" Retry.none (fun _ -> task { runs[$"""{run.Schedule}@{run.Due.ToString "O"}"""] <- run })
            return string run.Input
        })
    let handle = Workflows.register api definition
    [ ScheduledWorkflow.ofHandle handle ], None

let private unreadableInputLost =
    testCase "schedules: a decided run whose stored input the workflow cannot read logs one error, is lost, and the schedule goes on" <| fun _ ->
        withDb <| fun db ->
            let due = crashAfterFiring db "ScheduleUnreadableInput" (remindOnly (Runs())) ignore
            let capture = new Capture()
            use loggers = LoggerFactory.Create(fun builder -> builder.AddProvider capture |> ignore)
            let runs = ConcurrentDictionary<string, ScheduledRun<int>>()
            let second = bootWith db "ScheduleUnreadableInput" pastFirstHour loggers (remindInt runs)
            try
                // Error lines about the decided run, with their exceptions.
                let errorsForDue () =
                    capture.Errors |> List.filter (fun (level, text, _) -> level = LogLevel.Error && text.Contains(due.ToString "O"))
                eventually "an error for the decided run" (fun () -> not (List.isEmpty (errorsForDue ())))
                // A retry comes after the shorter of the check interval (one second) and five seconds.
                Thread.Sleep 3500
                let errors = errorsForDue ()
                Expect.equal errors.Length 1 "one error, and no start is tried again"
                let _, text, error = errors.Head
                Expect.stringContains text "Schedule k " "the error names the schedule"
                Expect.stringContains text "workflow remind " "the error names the workflow"
                Expect.isNotNull error "the error carries the exception"
                eventually "the lost run's start is reported" (fun () -> let view = view second "k" in view.LastDue = Some due && view.LastStarted)
                Expect.isEmpty runs "no run started"
                sync (Schedules.change second.Schedules "k" "remind" 7 (ScheduleRule.Every hour) CatchUp.Latest)
                shiftTo second (pastFirstHour + hour + TimeSpan.FromSeconds 5.0)
                eventually "the next due time runs with the changed input" (fun () -> runs.Values |> Seq.exists (fun run -> run.Due > due && run.Input = 7))
                Expect.equal (errorsForDue ()).Length 1 "still one error"
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

/// Creates an hourly schedule with `create`, stops, moves the clock 3.5 hours and restarts: three
/// due times were missed while nothing ran.
let private downtimeWith (what: string) (name: string) (create: ScheduleHandle -> Async<unit>) (expected: int) =
    testCase $"schedules: after downtime over three due times, {what} starts {expected} runs" <| fun _ ->
        withDb <| fun db ->
            let runs = Runs()
            let first = boot db name TimeSpan.Zero runs
            let anchor =
                try
                    sync (create first.Schedules)
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

let private downtime (policy: CatchUp) (expected: int) =
    downtimeWith $"{policy}" $"ScheduleDowntime{policy}" (fun schedules -> Schedules.create schedules "h" "remind" "x" (ScheduleRule.Every hour) policy) expected

/// A schedule created without a policy catches up as `CatchUp.Latest` does.
let private downtimeDefault =
    downtimeWith "a schedule created without a policy" "ScheduleDowntimeDefault" (fun schedules -> Schedules.createLatest schedules "h" "remind" "x" (ScheduleRule.Every hour)) 1

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
            let ranAt = ConcurrentQueue<DateTime>()
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
            // Records when each run of the other schedule ran.
            let timed =
                Workflow.define "remind" (fun (run: ScheduledRun<string>) -> workflow {
                    do! activity "record" Retry.none (fun _ -> task {
                        runs[runIdOf run] <- run
                        ranAt.Enqueue DateTime.UtcNow })
                    return run.Input
                })
            let node =
                bootWith db "ScheduleStuck" TimeSpan.Zero nullLoggers (fun api ->
                    let handle = Workflows.register api timed
                    [ ScheduledWorkflow.ofHandle handle; hanging ], Some handle)
            try
                sync (Schedules.create node.Schedules "stuck" "hang" "x" (ScheduleRule.Every second) CatchUp.Latest)
                eventually "the hanging start began" (fun () -> not began.IsEmpty)
                sync (Schedules.create node.Schedules "free" "remind" "y" (ScheduleRule.Every second) CatchUp.Latest)
                // While one start hangs for four seconds, the every-second schedule keeps running. A
                // dispatcher that waited for the hanging start would run nothing inside it.
                let ranDuringOneHang () =
                    let ends = List.ofSeq failed
                    let starts = List.ofSeq began |> List.truncate ends.Length
                    List.zip starts ends
                    |> List.exists (fun (start, ending) -> ranAt |> Seq.filter (fun at -> at > start && at < ending) |> Seq.length >= 2)
                eventuallyWithin 30.0 "the other schedule ran at least twice during one hanging start" ranDuringOneHang
                eventually "the hanging start was tried again" (fun () -> began.Count >= 2)
                let attempts = List.ofSeq began
                let failures = List.ofSeq failed
                // retryDelay is min(check interval, 5 s): one second here.
                for retried, failure in List.zip (List.tail attempts) (List.truncate (attempts.Length - 1) failures) do
                    Expect.isGreaterThanOrEqual (retried - failure).TotalMilliseconds 900.0 "a failed start waits the retry delay from its failure"
            finally
                node.Stop()

// A dispatcher fed thousands of schedules that are all due, through its in-memory index, with
// a schedule aggregate that accepts every fire and a workflow that only counts its starts. Counting
// the due times the dispatcher computes keeps the check independent of the machine's speed.
let private burstLinear =
    testCase "schedules: thousands of schedules due at once cost the dispatcher work linear in their number, at most 16 fires at once and one per schedule" <| fun _ ->
        withDb <| fun db ->
            let count = 4000
            let api = Fcqrs.actor (VerifySerialization.configuration().Build()) nullLoggers (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={db};")) "ScheduleBurst"
            try
                Fcqrs.wireSagaStarters api []
                let messageId () = Guid.CreateVersion7().ToString() |> FCQRS.Model.Data.ValueLens.CreateAsResult |> FCQRS.Model.Data.Result.value
                let envelope (id: string) details : Event<ScheduleEvent> =
                    { EventDetails = details
                      CreationDate = DateTime.UtcNow
                      Id = messageId ()
                      Sender = Some(Fcqrs.aggregateId id)
                      CorrelationId = Fcqrs.newCid ()
                      Version = FCQRS.Model.Data.Version.Zero
                      Metadata = Map.empty }
                let input: Payload = { Type = "System.String"; Json = "\"x\"" }
                let send (id: string) (_: FCQRS.Model.Data.CID) command =
                    async {
                        match command with
                        | ScheduleCommand.Fire due -> return envelope id (ScheduleEvent.Firing(due, "burst", input))
                        | ScheduleCommand.Started due -> return envelope id (ScheduleEvent.Fired due)
                        | other -> return envelope id (ScheduleEvent.Refused $"unexpected {other}")
                    }
                let started = ConcurrentDictionary<string, int>()
                let counting = obj ()
                let mutable running = 0
                let mutable most = 0
                let burst =
                    ScheduledWorkflow("burst", typeof<string>, fun _ schedule _ _ ->
                        async {
                            lock counting (fun () ->
                                running <- running + 1
                                most <- max most running)
                            do! Async.Sleep 1
                            started.AddOrUpdate(schedule, 1, fun _ seen -> seen + 1) |> ignore
                            lock counting (fun () -> running <- running - 1)
                        })
                let workflows = Dictionary<string, ScheduledWorkflow>()
                workflows["burst"] <- burst
                let dispatcher = Dispatcher(api, send, workflows, TimeSpan.FromMinutes 1.0, api.LoggerFactory.CreateLogger "burst", ScheduleIndex.store api)
                // Half of the schedules were due five seconds ago, and the dispatcher reads them from
                // its projection. The other half fall due together three seconds from now, which a
                // pass over the index finds. Each schedule's next due time is an hour later.
                let spec = { Workflow = "burst"; Input = input; Rule = ScheduleRule.Every hour; CatchUp = CatchUp.Latest }
                let overdue = DateTime.UtcNow - hour - TimeSpan.FromSeconds 5.0
                let soon = DateTime.UtcNow - hour + TimeSpan.FromSeconds 3.0
                for i in 1..count do
                    let anchor = if i % 2 = 0 then overdue else soon
                    dispatcher.Observe(box (envelope $"s{i}" (ScheduleEvent.Created(spec, anchor))))
                dispatcher.Start()
                eventuallyWithin 120.0 "every schedule started its run" (fun () -> started.Count = count)
                Thread.Sleep 500
                Expect.isTrue (started.Values |> Seq.forall (fun runs -> runs = 1)) "each schedule fired once"
                Expect.isLessThanOrEqual (lock counting (fun () -> most)) 16 "at most 16 fires ran at once"
                // A few computations per schedule: when it is read or found due, when it gets a slot,
                // and after it fired. Looking at every waiting schedule again at each end of a fire
                // costs about count * count / 2.
                Expect.isLessThanOrEqual dispatcher.Computations (int64 (5 * count)) "the due times computed grow linearly with the schedules due"
            finally
                api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let private longCheckInterval =
    testCase "schedules: a check interval longer than a delay can take does not stop the dispatcher while it waits for the saga starters" <| fun _ ->
        withDb <| fun db ->
            let runs = Runs()
            // Longer than the 49.7 days a Task.Delay takes.
            let node = bootConfigured db "ScheduleLongCheck" TimeSpan.Zero nullLoggers [ "config:akka:fcqrs:schedule-check-interval", "60d" ] false (remindOnly runs)
            try
                Thread.Sleep 1000
                Fcqrs.wireSagaStarters node.Api []
                sync (Schedules.create node.Schedules "long" "remind" "x" (ScheduleRule.Every second) CatchUp.Latest)
                eventually "a run starts" (fun () -> runs.Count >= 1)
            finally
                node.Stop()

let private clockStepsBack =
    testCase "schedules: after the clock steps back and is corrected past a due time, the run starts within a few check intervals" <| fun _ ->
        withDb <| fun db ->
            let runs = Runs()
            let node = boot db "ScheduleClockBack" TimeSpan.Zero runs
            try
                sync (Schedules.create node.Schedules "back" "remind" "x" (ScheduleRule.Every hour) CatchUp.Latest)
                eventually "the index has the schedule" (fun () -> hasView node "back")
                // Let the dispatcher plan on the true clock, then on a clock an hour behind.
                Thread.Sleep 2500
                shiftTo node (TimeSpan.FromHours -1.0)
                Thread.Sleep 3000
                // The correction lands two seconds past the first due time. The check interval is
                // one second, so the dispatcher looks again within a second or two.
                shiftTo node pastFirstHour
                eventuallyWithin 10.0 "the run starts after the correction" (fun () -> runs.Count = 1)
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

let private newRulesNormalized =
    testCase "schedules: the days and times of monthly and several-times rules are sorted and repeats removed, so a reordered create is the same content" <| fun _ ->
        withDb <| fun db ->
            let node = boot db "ScheduleRulesNormalized" TimeSpan.Zero (Runs())
            try
                let nine, five = TimeOnly(9, 0), TimeOnly(17, 0)
                let wednesday = DayOfWeek.Wednesday
                let cases =
                    [ "monthly",
                      ScheduleRule.Monthly([ MonthDay.LastDay; MonthDay.Nth(3, wednesday); MonthDay.Day 31; MonthDay.LastDay ], [ five; nine; five ], "UTC"),
                      ScheduleRule.Monthly([ MonthDay.Nth(3, wednesday); MonthDay.Day 31; MonthDay.LastDay ], [ nine; five; nine ], "UTC"),
                      ScheduleRule.Monthly([ MonthDay.Day 31; MonthDay.LastDay; MonthDay.Nth(3, wednesday) ], [ nine; five ], "UTC"),
                      ScheduleRule.Monthly([ MonthDay.Day 31; MonthDay.LastDay; MonthDay.Nth(3, wednesday) ], [ nine ], "UTC")
                      "daily at",
                      ScheduleRule.DailyAt([ five; nine; five ], "UTC"),
                      ScheduleRule.DailyAt([ nine; five ], "UTC"),
                      ScheduleRule.DailyAt([ nine; five ], "UTC"),
                      ScheduleRule.DailyAt([ nine ], "UTC")
                      "weekly at",
                      ScheduleRule.WeeklyAt([ DayOfWeek.Thursday; DayOfWeek.Monday; DayOfWeek.Thursday ], [ five; nine ], "UTC"),
                      ScheduleRule.WeeklyAt([ DayOfWeek.Monday; DayOfWeek.Thursday ], [ nine; nine; five ], "UTC"),
                      ScheduleRule.WeeklyAt([ DayOfWeek.Monday; DayOfWeek.Thursday ], [ nine; five ], "UTC"),
                      ScheduleRule.WeeklyAt([ DayOfWeek.Monday ], [ nine; five ], "UTC") ]
                for what, first, reordered, stored, different in cases do
                    let id = what.Replace(' ', '-')
                    let create rule = Schedules.create node.Schedules id "remind" "x" rule CatchUp.Latest
                    sync (create first)
                    sync (create reordered)
                    Expect.throwsT<InvalidOperationException> (fun () -> sync (create different)) $"{what}: a create with other days or times is refused"
                    eventually $"{what}: the index has the schedule" (fun () -> hasView node id)
                    Expect.equal (view node id).Rule stored $"{what}: the stored days and times are sorted, without repeats"
            finally
                node.Stop()

let private monthlyFires =
    testCase "schedules: a monthly schedule starts a run for each due time missed across a month boundary" <| fun _ ->
        withDb <| fun db ->
            let runs = Runs()
            let rule = ScheduleRule.Monthly([ MonthDay.Nth(1, DayOfWeek.Monday); MonthDay.LastDay ], [ TimeOnly(9, 0) ], "Europe/Oslo")
            let first = boot db "ScheduleMonthly" TimeSpan.Zero runs
            let anchor =
                try
                    sync (Schedules.create first.Schedules "m" "remind" "x" rule CatchUp.All)
                    eventually "the index has the schedule" (fun () -> hasView first "m")
                    (view first "m").Anchor
                finally
                    first.Stop()
            Expect.equal runs.Count 0 "no run before the first due time"

            // Three due times of two a month always span a month boundary.
            let fourDues = List.scan (fun after _ -> ScheduleRule.next rule anchor after) anchor [ 1..4 ] |> List.tail
            let missed, upcoming = List.take 3 fourDues, List.last fourDues
            Expect.notEqual missed.Head.Month (List.last missed).Month "the missed due times span a month boundary"
            let second = boot db "ScheduleMonthly" (List.last missed - DateTime.UtcNow + TimeSpan.FromSeconds 2.0) runs
            try
                eventually "a run for each missed due time" (fun () -> runs.Count >= 3)
                Thread.Sleep 2000
                Expect.equal (dues runs) missed "one run for each, no more"
                eventually "the next due time is the first in the future" (fun () -> (view second "m").NextDue = Some upcoming)
            finally
                second.Stop()

// The stored index. Its table name, row shape and projection name are persistent contracts, so
// these tests spell them out instead of reading them from the code.

/// Where a test's journal lives.
type private Journal =
    | SqliteFile of string
    | Postgres of string

let private connectionOf (journal: Journal) =
    match journal with
    | SqliteFile path -> Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={path};"
    | Postgres text -> Fcqrs.connect FCQRS.Actor.DBType.PostgreSQL15 text

/// Runs `test` on a new SQLite file, or on a new database of the PostgreSQL server `postgres` names.
let private withJournal (postgres: string option) (test: Journal -> unit) =
    match postgres with
    | None -> withDb (SqliteFile >> test)
    | Some root ->
        let database = $"fcqrs_schedule_{Guid.NewGuid():N}"
        let admin (sql: string) =
            use connection = new NpgsqlConnection(root)
            connection.Open()
            use command = connection.CreateCommand()
            command.CommandText <- sql
            command.ExecuteNonQuery() |> ignore
        admin $"CREATE DATABASE {database}"
        try
            let builder = NpgsqlConnectionStringBuilder(root)
            builder.Database <- database
            test (Postgres builder.ConnectionString)
        finally
            NpgsqlConnection.ClearAllPools()
            admin $"DROP DATABASE IF EXISTS {database} WITH (FORCE)"

/// Opens the journal's database. A SQLite connection is not pooled: a pooled one that dropped a
/// table can keep it out of its cached schema after another connection creates it again, and
/// report "no such table" while a writer holds the lock it needs to check the schema.
let private openJournal (journal: Journal) : DbConnection =
    let connection: DbConnection =
        match journal with
        | SqliteFile path -> new SqliteConnection($"Data Source={path};Pooling=False")
        | Postgres text -> new NpgsqlConnection(text)
    connection.Open()
    connection

let private command (connection: DbConnection) (sql: string) (parameters: (string * string) list) =
    let command = connection.CreateCommand()
    command.CommandText <- sql
    for name, value in parameters do
        let parameter = command.CreateParameter()
        parameter.ParameterName <- name
        parameter.Value <- value
        command.Parameters.Add parameter |> ignore
    command

let private tableExists (journal: Journal) (table: string) =
    use connection = openJournal journal
    let sql =
        match journal with
        | SqliteFile _ -> "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name"
        | Postgres _ -> "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = @name"
    use count = command connection sql [ "@name", table ]
    Convert.ToInt64(count.ExecuteScalar()) > 0L

/// Every row of the index table: schedule ID and stored entry.
let private indexRows (journal: Journal) : Map<string, string> =
    use connection = openJournal journal
    use select = command connection "SELECT schedule_id, entry FROM fcqrs_schedule_index" []
    use reader = select.ExecuteReader()
    [ while reader.Read() do
          reader.GetString 0, reader.GetString 1 ]
    |> Map.ofList

/// Leaves the journal as FCQRS.Workflows 7.6.0 left it: no index table, and no progress or lock row
/// for the index's projection.
let private forgetIndex (journal: Journal) =
    let exists = tableExists journal
    use connection = openJournal journal
    if exists "fcqrs_schedule_index" then
        use drop = command connection "DROP TABLE fcqrs_schedule_index" []
        drop.ExecuteNonQuery() |> ignore
    for table in [ "fcqrs_projection_progress"; "fcqrs_projection_progress_lock" ] do
        if exists table then
            use delete = command connection $"DELETE FROM {table} WHERE projection_name = @name" [ "@name", "fcqrs-schedule-index" ]
            delete.ExecuteNonQuery() |> ignore

/// How many schedule events the journal holds.
let private scheduleEvents (journal: Journal) =
    use connection = openJournal journal
    use count = command connection "SELECT COUNT(*) FROM journal WHERE persistence_id LIKE 'fcqrs-schedule/%'" []
    Convert.ToInt64(count.ExecuteScalar())

/// Waits until the node's index has caught up with the journal, before its first plan.
let private ready (node: Node) =
    Expect.isTrue (node.Schedules.Dispatcher.Ready.Wait(TimeSpan.FromSeconds 60.0)) "the index caught up within a minute"

/// The journal events the node's index has read since it started.
let private envelopesRead (node: Node) =
    match node.Schedules.Dispatcher.Index with
    | Some projection -> (projection :?> FCQRS.Projections.IJournalReadCount).EnvelopesRead
    | None -> failtest "the index has no projection"

/// Waits until the node's index has read every schedule event stored so far.
let private indexCaughtUp (node: Node) =
    match node.Schedules.Dispatcher.Index with
    | Some projection -> projection.CatchUpAsync().Wait(TimeSpan.FromSeconds 60.0) |> fun caught -> Expect.isTrue caught "the index caught up"
    | None -> failtest "the index has no projection"

/// An actor system with the schedule aggregate and no dispatcher, so the schedule commands it sends
/// are stored while no index reads them. Returns the system and a function that sends a command and
/// returns the stored reply.
type private WarningLogger(category: string, sink: ConcurrentQueue<string>) =
    interface ILogger with
        member _.BeginScope<'TState when 'TState: not null>(_state: 'TState) : IDisposable | null = null
        member _.IsEnabled(level) = level >= LogLevel.Warning
        member _.Log<'TState>(level, _eventId, state: 'TState, error: exn | null, formatter: Func<'TState, exn | null, string>) =
            if level >= LogLevel.Warning then
                let time = DateTime.UtcNow.ToString("HH:mm:ss.fff")
                let cause = match error with | null -> "" | e -> $" ({e.GetType().Name}: {e.Message})"
                sink.Enqueue($"{time} {level} {category} | {formatter.Invoke(state, error)}{cause}")

type private WarningLoggerFactory(sink: ConcurrentQueue<string>) =
    interface ILoggerFactory with
        member _.CreateLogger(category: string) = WarningLogger(category, sink) :> ILogger
        member _.AddProvider(_provider) = ()
        member _.Dispose() = ()

let private bareSchedules (connection: FCQRS.Actor.Connection) (name: string) =
    let configuration =
        VerifySerialization.configuration()
            .AddInMemoryCollection([ KeyValuePair<string, string | null>("config:akka:scheduler:implementation", "FCQRS.Scheduler+ShiftedScheduler, FCQRS") ])
            .Build()
    // Warnings and errors only. A send that gets no reply reports them: one did so once on CI, in
    // storeHistory, and nothing else said why.
    let warnings = ConcurrentQueue<string>()
    let api = Fcqrs.actor configuration (new WarningLoggerFactory(warnings)) (Some connection) name
    let aggregate =
        Fcqrs.aggregate
            api
            { Name = Schedule.EntityName
              Initial = Schedule.initial
              Decide = Schedule.decide
              Fold = Schedule.fold
              Snapshots = SnapshotPolicy.Default
              Passivation = PassivationPolicy.Default }
    Fcqrs.wireSagaStarters api []
    let mutable sent = 0
    let send (id: string) (command: ScheduleCommand) =
        sent <- sent + 1
        let started = Diagnostics.Stopwatch.StartNew()
        let reply =
            try
                // Longer than the 30-second command timeout, so the subscription's own timeout and
                // its warning come first.
                Async.RunSynchronously(aggregate.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId id) command (fun _ -> true), 40000)
            with error ->
                let logged = String.Join("\n", warnings)
                failtest $"{id}: send {sent}, {command}, failed after {started.Elapsed} with {error.GetType().Name}: {error.Message}\nWarnings and errors logged:\n{logged}"
        match reply.EventDetails with
        | ScheduleEvent.Refused reason -> failtest $"{id}: {command} was refused: {reason}"
        | event -> event
    api, send

let private stopApi (api: IActor) = api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let private hourly (policy: CatchUp) : ScheduleSpec =
    { Workflow = "remind"
      Input = Payload.ofValue "x"
      Rule = ScheduleRule.Every hour
      CatchUp = policy }

/// Stores an hourly schedule `id` under CatchUp.All and `runs` started runs, without a dispatcher.
/// Returns the schedule's anchor.
let private storeHistory (journal: Journal) (id: string) (runs: int) =
    let api, send = bareSchedules (connectionOf journal) "ScheduleHistory"
    try
        let anchor =
            match send id (ScheduleCommand.Create(hourly CatchUp.All)) with
            | ScheduleEvent.Created(_, at) -> at
            | other -> failtest $"created: {other}"
        let clock = api.System.Scheduler :?> FCQRS.Scheduler.ShiftedScheduler
        for k in 1..runs do
            // Half an hour past each due time: CatchUp.All accepts only the most recent 1000.
            clock.Shift <- TimeSpan.FromHours(float k + 0.5)
            let due = anchor + TimeSpan.FromHours(float k)
            send id (ScheduleCommand.Fire due) |> ignore
            send id (ScheduleCommand.Started due) |> ignore
        anchor
    finally
        stopApi api

/// Sends one command to `id` without a dispatcher.
let private storeWhileDown (journal: Journal) (id: string) (command: ScheduleCommand) =
    let api, send = bareSchedules (connectionOf journal) "ScheduleWhileDown"
    try send id command |> ignore finally stopApi api

let private restartReadsNewEvents (postgres: string option) =
    let database = if postgres.IsSome then "PostgreSQL" else "SQLite"
    testCase $"schedules: a restart over 500 runs reads only the events stored since the index last read ({database})" <| fun _ ->
        withJournal postgres <| fun journal ->
            let runs = 500
            let anchor = storeHistory journal "big" runs
            let last = anchor + TimeSpan.FromHours(float runs)
            Expect.isGreaterThan (scheduleEvents journal) (int64 (2 * runs)) "the journal holds the whole run history"

            let first = bootOn (connectionOf journal) "ScheduleIndexRestart" TimeSpan.Zero nullLoggers [] true noWorkflows
            try
                ready first
                Expect.equal (view first "big").LastDue (Some last) "the first start reads the history"
                Expect.isTrue (view first "big").LastStarted "and the last run's start"
            finally
                first.Stop()

            storeWhileDown journal "big" ScheduleCommand.Pause

            let second = bootOn (connectionOf journal) "ScheduleIndexRestart" TimeSpan.Zero nullLoggers [] true noWorkflows
            try
                ready second
                Expect.isTrue (view second "big").Paused "the restart reads the pause stored while it was down"
                Expect.equal (view second "big").LastDue (Some last) "and keeps what it had read before"
                Expect.equal (envelopesRead second) 1L "the restart reads one event, not the history"
            finally
                second.Stop()

let private upgradeBuildsIndex (postgres: string option) =
    let database = if postgres.IsSome then "PostgreSQL" else "SQLite"
    testCase $"schedules: a journal without the index table fills it on the first start, fires the next due time once, and later starts read no old event ({database})" <| fun _ ->
        withJournal postgres <| fun journal ->
            let runs = Runs()
            let connection = connectionOf journal
            let anchor =
                let first = bootOn connection "ScheduleIndexUpgrade" TimeSpan.Zero nullLoggers [] true (remindOnly runs)
                try
                    sync (Schedules.create first.Schedules "u" "remind" "x" (ScheduleRule.Every hour) CatchUp.Latest)
                    eventually "the index has the schedule" (fun () -> hasView first "u")
                    let anchor = (view first "u").Anchor
                    shiftTo first pastFirstHour
                    eventually "the first run started" (fun () -> (view first "u").LastStarted && (view first "u").LastDue = Some(anchor + hour))
                    anchor
                finally
                    first.Stop()

            forgetIndex journal

            let shift = TimeSpan.FromHours 2.0 + TimeSpan.FromSeconds 2.0
            let second = bootOn connection "ScheduleIndexUpgrade" shift nullLoggers [] true (remindOnly runs)
            try
                ready second
                Expect.isTrue (indexRows journal |> Map.containsKey "u") "the first start fills the table"
                Expect.equal (view second "u").LastDue (Some(anchor + hour)) "from the whole history"
                eventually "the next due time runs" (fun () -> runs.Count >= 2)
                eventually "its start is reported" (fun () -> (view second "u").LastStarted && (view second "u").LastDue = Some(anchor + 2.0 * hour))
                Thread.Sleep 1500
                Expect.equal (dues runs) [ anchor + hour; anchor + 2.0 * hour ] "each due time ran once"
                indexCaughtUp second
            finally
                second.Stop()

            let third = bootOn connection "ScheduleIndexUpgrade" shift nullLoggers [] true (remindOnly runs)
            try
                ready third
                Expect.equal (view third "u").LastDue (Some(anchor + 2.0 * hour)) "the restart loads the stored row"
                Expect.equal (envelopesRead third) 0L "a later start reads no event an earlier one read"
                Thread.Sleep 1500
                Expect.equal runs.Count 2 "no due time ran twice"
            finally
                third.Stop()

/// Runs one statement on the journal's database.
let private execute (journal: Journal) (sql: string) =
    use connection = openJournal journal
    use statement = command connection sql []
    statement.ExecuteNonQuery() |> ignore

/// An hourly schedule "u" whose first run started; `damage` then breaks the stored index while the
/// dispatcher is down. The restart must rebuild the index from the journal and run the next due
/// time once. `logged` checks the restart's log lines.
let private indexRecovers (postgres: string option) (what: string) (damage: Journal -> unit) (logged: Capture -> unit) =
    let database = if postgres.IsSome then "PostgreSQL" else "SQLite"
    testCase $"schedules: {what}, the restart rebuilds the index and fires the next due time once ({database})" <| fun _ ->
        withJournal postgres <| fun journal ->
            let runs = Runs()
            let connection = connectionOf journal
            let anchor =
                let first = bootOn connection "ScheduleIndexDamage" TimeSpan.Zero nullLoggers [] true (remindOnly runs)
                try
                    sync (Schedules.create first.Schedules "u" "remind" "x" (ScheduleRule.Every hour) CatchUp.Latest)
                    eventually "the index has the schedule" (fun () -> hasView first "u")
                    let anchor = (view first "u").Anchor
                    shiftTo first pastFirstHour
                    eventually "the first run started" (fun () -> (view first "u").LastStarted && (view first "u").LastDue = Some(anchor + hour))
                    indexCaughtUp first
                    anchor
                finally
                    first.Stop()

            damage journal

            let capture = new Capture()
            use loggers = LoggerFactory.Create(fun builder -> builder.AddProvider capture |> ignore)
            let second = bootOn connection "ScheduleIndexDamage" (TimeSpan.FromHours 2.0 + TimeSpan.FromSeconds 2.0) loggers [] true (remindOnly runs)
            try
                ready second
                Expect.equal (view second "u").LastDue (Some(anchor + hour)) "the index is rebuilt from the journal"
                eventually "the next due time runs" (fun () -> runs.Count >= 2)
                eventually "its start is reported" (fun () -> (view second "u").LastDue = Some(anchor + 2.0 * hour) && (view second "u").LastStarted)
                Thread.Sleep 1500
                Expect.equal (dues runs) [ anchor + hour; anchor + 2.0 * hour ] "each due time ran once"
                indexCaughtUp second
                let row = (indexRows journal)["u"]
                Expect.stringContains row "\"format\":1" "the row is written again"
                logged capture
            finally
                second.Stop()

let private unreadableRow (postgres: string option) =
    indexRecovers
        postgres
        "after a row was stored in a format this version cannot read"
        (fun journal -> execute journal "UPDATE fcqrs_schedule_index SET entry = REPLACE(entry, '\"format\":1', '\"format\":2')")
        (fun capture ->
            let warnings =
                capture.Lines
                |> List.filter (fun (level, _, text) -> level = LogLevel.Warning && text.Contains "fcqrs_schedule_index" && text.Contains "'u'")
            Expect.isNonEmpty warnings "a warning names the table and the row")

let private droppedTable (postgres: string option) =
    indexRecovers postgres "after the index table was dropped and its read positions kept" (fun journal -> execute journal "DROP TABLE fcqrs_schedule_index") ignore

// An actor system whose query journal reads the write journal's file through another connection
// string, which the schedule index cannot use.
let private withMismatchedReader name (test: IActor -> unit) =
    withDb <| fun db ->
        let configuration =
            VerifySerialization.configuration()
                .AddInMemoryCollection(
                    [ KeyValuePair<string, string | null>("config:akka:persistence:query:journal:sql:connection-string", $"Data Source={db};Default Timeout=31;") ]
                )
                .Build()
        let api = Fcqrs.actor configuration nullLoggers (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={db};")) name
        try
            test api
        finally
            stopApi api

// The error a register raises for the mismatched journals, which names the schedule index and both
// HOCON sections, not the projection store a schedules user never made.
let private expectJournalMismatch (api: IActor) =
    let error =
        try
            Schedules.register api [] |> ignore
            failtest "register raises"
        with :? ArgumentException as error ->
            error
    Expect.stringContains error.Message "schedule dispatcher keeps its index in the journal's database" "the message names the schedule index"
    Expect.stringContains error.Message "akka.persistence.journal.sql" "the message names the write journal"
    Expect.stringContains error.Message "akka.persistence.query.journal.sql" "the message names the query journal"
    Expect.isFalse (error.Message.Contains "projection store") "the message does not name a projection store"
    Expect.isNotNull error.InnerException "the store's error is kept as the inner exception"

let private mismatchedReader =
    testCase "schedules: a query journal on another connection string than the write journal raises from register" <| fun _ ->
        withMismatchedReader "ScheduleIndexReader" expectJournalMismatch

let private mismatchedReaderAgain =
    testCase "schedules: a register that raised for its journals raises the same error again, not that a dispatcher exists" <| fun _ ->
        withMismatchedReader "ScheduleIndexReaderAgain" <| fun api ->
            expectJournalMismatch api
            expectJournalMismatch api

let private deleteWhileDown =
    testCase "schedules: a schedule deleted while the dispatcher is down loses its index row" <| fun _ ->
        withJournal None <| fun journal ->
            let connection = connectionOf journal
            let first = bootOn connection "ScheduleIndexDelete" TimeSpan.Zero nullLoggers [] true (remindOnly (Runs()))
            try
                sync (Schedules.create first.Schedules "d" "remind" "x" (ScheduleRule.Every hour) CatchUp.Latest)
                sync (Schedules.create first.Schedules "keep" "remind" "x" (ScheduleRule.Every hour) CatchUp.Latest)
                indexCaughtUp first
                Expect.equal (indexRows journal |> Map.keys |> List.ofSeq) [ "d"; "keep" ] "a row per schedule"
            finally
                first.Stop()

            storeWhileDown journal "d" ScheduleCommand.Delete

            let second = bootOn connection "ScheduleIndexDelete" TimeSpan.Zero nullLoggers [] true (remindOnly (Runs()))
            try
                ready second
                Expect.equal (indexRows journal |> Map.keys |> List.ofSeq) [ "keep" ] "the deleted schedule's row is gone"
                Expect.isFalse (hasView second "d") "the index does not hold it"
                Expect.isTrue (hasView second "keep") "the other schedule stays"
            finally
                second.Stop()

let private changedConnectionString =
    testCase "schedules: the index resumes after the journal's connection string changes" <| fun _ ->
        withDb <| fun db ->
            let first = bootOn (Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={db};") "ScheduleIndexSource" TimeSpan.Zero nullLoggers [] true (remindOnly (Runs()))
            try
                sync (Schedules.create first.Schedules "s" "remind" "x" (ScheduleRule.Every hour) CatchUp.Latest)
                indexCaughtUp first
            finally
                first.Stop()

            let second = bootOn (Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={db};Default Timeout=31;") "ScheduleIndexSource" TimeSpan.Zero nullLoggers [] true (remindOnly (Runs()))
            try
                ready second
                Expect.isTrue (hasView second "s") "the index holds the schedule"
                sync (Schedules.pause second.Schedules "s")
                eventually "the index follows new events" (fun () -> (view second "s").Paused)
            finally
                second.Stop()

let private blank: Payload = { Type = ""; Json = "" }

let private at (day: int) (hourOfDay: int) = DateTime(2026, 11, day, hourOfDay, 0, 0, DateTimeKind.Utc)

let private rowGolden =
    testCase "schedules: an index row's stored text is a persistent contract" <| fun _ ->
        let full: ScheduleState =
            { Created =
                Some(
                    { Workflow = "remind"
                      Input = blank
                      Rule = ScheduleRule.Daily(TimeOnly(9, 30), "Europe/Oslo")
                      CatchUp = CatchUp.All },
                    at 1 8
                )
              Spec =
                Some
                    { Workflow = "report"
                      Input = blank
                      Rule = ScheduleRule.Weekly([ DayOfWeek.Monday; DayOfWeek.Thursday ], TimeOnly(17, 0, 0, 250), "UTC")
                      CatchUp = CatchUp.None }
              Anchor = at 2 10
              PausedAt = Some(at 3 11)
              ResumedAt = Some(at 4 12)
              DeletedAt = Some(at 5 13)
              Last =
                Some
                    { Due = at 2 17
                      Workflow = "remind"
                      Input = blank
                      Started = true } }
        let fullText =
            """{"format":1,"created":{"spec":{"workflow":"remind","rule":{"daily":{"time":"09:30:00.0000000","zone":"Europe/Oslo"}},"catchUp":"All"},"at":"2026-11-01T08:00:00.0000000Z"},"spec":{"workflow":"report","rule":{"weekly":{"days":["Monday","Thursday"],"time":"17:00:00.2500000","zone":"UTC"}},"catchUp":"None"},"anchor":"2026-11-02T10:00:00.0000000Z","pausedAt":"2026-11-03T11:00:00.0000000Z","resumedAt":"2026-11-04T12:00:00.0000000Z","deletedAt":"2026-11-05T13:00:00.0000000Z","last":{"due":"2026-11-02T17:00:00.0000000Z","workflow":"remind","started":true}}"""
        Expect.equal (ScheduleIndex.encode full) fullText "a fully populated row"
        Expect.equal (ScheduleIndex.decode fullText) full "the row reads back as the entry"

        let spec =
            { Workflow = "remind"
              Input = blank
              Rule = ScheduleRule.Every(TimeSpan.FromMinutes 90.0)
              CatchUp = CatchUp.Latest }
        let plain: ScheduleState =
            { Schedule.initial with
                Created = Some(spec, at 1 8)
                Spec = Some spec
                Anchor = at 1 8 }
        let plainText =
            """{"format":1,"created":{"spec":{"workflow":"remind","rule":{"every":"01:30:00"},"catchUp":"Latest"},"at":"2026-11-01T08:00:00.0000000Z"},"spec":{"workflow":"remind","rule":{"every":"01:30:00"},"catchUp":"Latest"},"anchor":"2026-11-01T08:00:00.0000000Z","pausedAt":null,"resumedAt":null,"deletedAt":null,"last":null}"""
        Expect.equal (ScheduleIndex.encode plain) plainText "an every rule, with nothing paused, deleted or run"
        Expect.equal (ScheduleIndex.decode plainText) plain "it reads back"
        Expect.throws (fun () -> ScheduleIndex.decode (plainText.Replace("\"format\":1", "\"format\":2")) |> ignore) "a row of an unknown format is refused"

// The rules added in 7.8.0 are written into the same format: a 7.7 row reads as it did.
let private rowGoldenRules =
    testCase "schedules: an index row's stored text for monthly and several-times rules is a persistent contract" <| fun _ ->
        let row (rule: ScheduleRule) : ScheduleState =
            let spec =
                { Workflow = "remind"
                  Input = blank
                  Rule = rule
                  CatchUp = CatchUp.All }
            { Schedule.initial with
                Created = Some(spec, at 1 8)
                Spec = Some spec
                Anchor = at 1 8 }
        let text (rule: string) =
            $$"""{"format":1,"created":{"spec":{"workflow":"remind","rule":{{rule}},"catchUp":"All"},"at":"2026-11-01T08:00:00.0000000Z"},"spec":{"workflow":"remind","rule":{{rule}},"catchUp":"All"},"anchor":"2026-11-01T08:00:00.0000000Z","pausedAt":null,"resumedAt":null,"deletedAt":null,"last":null}"""
        let cases =
            [ "monthly",
              ScheduleRule.Monthly(
                  [ MonthDay.Day 31; MonthDay.LastDay; MonthDay.Nth(3, DayOfWeek.Wednesday); MonthDay.Last DayOfWeek.Thursday ],
                  [ TimeOnly(9, 0); TimeOnly(17, 30, 0, 250) ],
                  "Europe/Oslo"
              ),
              """{"monthly":{"days":[{"day":31},"lastDay",{"nth":{"n":3,"weekday":"Wednesday"}},{"last":"Thursday"}],"times":["09:00:00.0000000","17:30:00.2500000"],"zone":"Europe/Oslo"}}"""
              "daily at",
              ScheduleRule.DailyAt([ TimeOnly(8, 0); TimeOnly(20, 0) ], "Europe/Oslo"),
              """{"dailyAt":{"times":["08:00:00.0000000","20:00:00.0000000"],"zone":"Europe/Oslo"}}"""
              "weekly at",
              ScheduleRule.WeeklyAt([ DayOfWeek.Monday; DayOfWeek.Friday ], [ TimeOnly(8, 0); TimeOnly(16, 45) ], "UTC"),
              """{"weeklyAt":{"days":["Monday","Friday"],"times":["08:00:00.0000000","16:45:00.0000000"],"zone":"UTC"}}""" ]
        for what, rule, ruleText in cases do
            Expect.equal (ScheduleIndex.encode (row rule)) (text ruleText) $"{what}: the row text"
            Expect.equal (ScheduleIndex.decode (text ruleText)) (row rule) $"{what}: the row reads back"
        for broken in [ """{"monthly":{"days":[{"week":2}],"times":["09:00:00.0000000"],"zone":"UTC"}}"""
                        """{"monthly":{"days":["firstDay"],"times":["09:00:00.0000000"],"zone":"UTC"}}"""
                        """{"monthly":{"days":[null],"times":["09:00:00.0000000"],"zone":"UTC"}}"""
                        """{"dailyAt":{"times":[null],"zone":"UTC"}}""" ] do
            Expect.throwsT<FormatException> (fun () -> ScheduleIndex.decode (text broken) |> ignore) $"refused: {broken}"

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
            unreadableInputLost
            decidedWorkflowKept
            deletedLeavesIndex
            checkIntervalRejected
            downtime CatchUp.Latest 1
            downtime CatchUp.All 3
            downtime CatchUp.None 0
            downtimeDefault
            failedRunContinues
            snapshotRecovery
            invalidRuleRaises
            longIdRaises
            unwiredWaits
            stuckStartAlone
            burstLinear
            longCheckInterval
            clockStepsBack
            weeklyDaysNormalized
            rowGolden
            rowGoldenRules
            newRulesNormalized
            monthlyFires
            restartReadsNewEvents None
            upgradeBuildsIndex None
            deleteWhileDown
            changedConnectionString
            unreadableRow None
            droppedTable None
            mismatchedReader
            mismatchedReaderAgain
            match Environment.GetEnvironmentVariable "FCQRS_TEST_POSTGRES" with
            | null | "" -> ptestCase "schedules: the stored index on PostgreSQL: set FCQRS_TEST_POSTGRES to run" ignore
            | root ->
                testList "schedules: the stored index on PostgreSQL" [
                    restartReadsNewEvents (Some root)
                    upgradeBuildsIndex (Some root)
                    unreadableRow (Some root)
                    droppedTable (Some root)
                ]
        ]
    )
