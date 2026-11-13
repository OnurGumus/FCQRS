module WorkflowLoggingTests

// What a workflow writes to ILogger: failures in "FCQRS.Workflows", a run's progress in
// "FCQRS.MessageFlow", the trace and the scope each line carries, and the link from a signal
// wait's span to the caller that sent the signal.

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Common
open FCQRS.FSharp
open FCQRS.Workflows

type private Line =
    { Category: string
      Level: LogLevel
      Text: string
      Error: exn option
      /// The trace of the span that was current when the line was written.
      TraceId: string option
      Scope: Map<string, string> }

type private CaptureLogger(category: string, add: Line -> unit, scopes: unit -> IExternalScopeProvider) =
    interface ILogger with
        member _.BeginScope<'TState when 'TState: not null>(state: 'TState) : IDisposable | null = scopes().Push(box state)
        member _.IsEnabled(_level) = true
        member _.Log<'TState>(level, _eventId, state: 'TState, error: exn | null, formatter: Func<'TState, exn | null, string>) =
            let scope = Dictionary<string, string>()
            scopes().ForEachScope(
                (fun (entry: obj | null) (seen: Dictionary<string, string>) ->
                    match entry with
                    | :? IEnumerable<KeyValuePair<string, obj>> as pairs ->
                        for pair in pairs do
                            seen[pair.Key] <- string pair.Value
                    | _ -> ()),
                scope)
            add
                { Category = category
                  Level = level
                  Text = formatter.Invoke(state, error)
                  Error = Option.ofObj error
                  TraceId =
                    match Activity.Current with
                    | null -> None
                    | span -> Some(span.TraceId.ToHexString())
                  Scope = Map [ for pair in scope -> pair.Key, pair.Value ] }

/// A logger provider that keeps every line, with the scope the logger factory hands it.
type private Capture() =
    let lines = ConcurrentQueue<Line>()
    let mutable scopes: IExternalScopeProvider = LoggerExternalScopeProvider()
    member _.Lines = List.ofSeq lines
    /// The lines of one category that mention `text`.
    member this.Of (category: string) (text: string) =
        this.Lines |> List.filter (fun line -> line.Category = category && line.Text.Contains text)
    interface ILoggerProvider with
        member _.CreateLogger(category) = CaptureLogger(category, lines.Enqueue, (fun () -> scopes)) :> ILogger
        member _.Dispose() = ()
    interface ISupportExternalScope with
        member _.SetScopeProvider(provider) = scopes <- provider

let private workflows = "FCQRS.Workflows"
let private messageFlow = Telemetry.MessageFlowCategory

let private newDb () = Path.Combine(Path.GetTempPath(), $"fcqrs_workflow_log_{Guid.NewGuid():N}.db")

let private deleteDb (db: string) =
    SqliteConnection.ClearAllPools()
    for path in [ db; db + "-wal"; db + "-shm" ] do
        if File.Exists path then File.Delete path

let private boot (db: string) (name: string) (capture: Capture) : IActor =
    let logging = LoggerFactory.Create(fun builder -> builder.SetMinimumLevel(LogLevel.Information).AddProvider capture |> ignore)
    Fcqrs.actor (VerifySerialization.configuration().Build()) logging (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={db};")) name

let private stop (api: IActor) = api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let private withSystem name (run: IActor -> Capture -> unit) =
    let db = newDb ()
    let capture = new Capture()
    let api = boot db name capture
    try
        run api capture
    finally
        stop api
        deleteDb db

let private sync (work: Async<'T>) = Async.RunSynchronously(work, 30000)
let private wait = TimeSpan.FromSeconds 20.0

let private eventually what (condition: unit -> bool) =
    let deadline = DateTime.UtcNow.AddSeconds 20.0
    while not (condition ()) && DateTime.UtcNow < deadline do
        Thread.Sleep 50
    Expect.isTrue (condition ()) what

let private fast: RetryPolicy =
    { Retry.none with
        Schedule = FixedInterval(TimeSpan.FromMilliseconds 20.0) }

let private stepFailureTest =
    testCase "workflow log: a step's final failure is a warning that keeps its exception" <| fun _ ->
        withSystem "WorkflowLogStepFailure" <| fun api capture ->
            let flow =
                Workflows.register api (Workflow.define "declined" (fun (_: unit) -> workflow {
                    try
                        let! (_: int) = activity "charge" Retry.none (fun _ -> task { return failwith "card declined" })
                        return "charged"
                    with :? WorkflowStepFailedException ->
                        return "refused"
                }))
            sync (flow.Start "d1" ()) |> ignore
            Expect.equal (sync (flow.Result "d1" wait)) (WorkflowOutcome.Finished "refused") "the body handled the failure"
            match capture.Of workflows "declined d1" with
            | [ line ] ->
                Expect.equal line.Level LogLevel.Warning "a failure the body may handle is a warning"
                Expect.equal line.Text "Workflow declined d1: step 0 charge failed as Exhausted: card declined" "the line names the step and the kind of failure"
                Expect.equal (line.Error |> Option.map (fun error -> error.Message)) (Some "card declined") "the line keeps the exception, and with it the stack trace"
            | other -> failtest $"expected one line about the failed step, found {other}"

let private faultTest =
    testCase "workflow log: a faulted workflow is an error that keeps the exception the body threw" <| fun _ ->
        withSystem "WorkflowLogFault" <| fun api capture ->
            let flow =
                Workflows.register api (Workflow.define "broken" (fun (_: unit) -> workflow {
                    let! (_: DateTime) = now
                    failwith "no stock"
                    return 1
                }))
            sync (flow.Start "b1" ()) |> ignore
            match sync (flow.Result "b1" wait) with
            | WorkflowOutcome.Faulted _ -> ()
            | other -> failtest $"expected a fault, found {other}"
            match capture.Of workflows "broken b1" with
            | [ line ] ->
                Expect.equal line.Level LogLevel.Error "a fault is final, so it is an error"
                Expect.equal line.Text "Workflow broken b1 faulted: BodyFailed: no stock" "the line names the kind of fault"
                Expect.equal (line.Error |> Option.map (fun error -> error.Message)) (Some "no stock") "the line keeps the body's exception"
            | other -> failtest $"expected one line about the fault, found {other}"

let private progressTest =
    testCase "workflow log: the message-flow log tells a run's progress and never a payload" <| fun _ ->
        withSystem "WorkflowLogProgress" <| fun api capture ->
            let flow =
                Workflows.register api (Workflow.define "narrated" (fun (input: string) -> workflow {
                    let! (_: string) = activity "price" Retry.none (fun _ -> task { return "secret-price" })
                    let! (answer: string) = waitSignal "approval"
                    let! (_: string option) = waitSignalFor "extra" (TimeSpan.FromMilliseconds 50.0)
                    do! sleep (TimeSpan.FromMilliseconds 20.0)
                    return $"{input}/{answer}"
                }))
            sync (flow.Start "n1" "secret-input") |> ignore
            // The signal is sent once the wait is reached, so the lines have one order.
            eventually "the workflow waits for its approval" (fun () -> sync (flow.Status "n1") = WorkflowStatus.Running [ "1 approval" ])
            sync (Workflows.signal flow "n1" "approval" "secret-signal")
            Expect.equal (sync (flow.Result "n1" wait)) (WorkflowOutcome.Finished "secret-input/secret-signal") "the workflow finished"
            Expect.equal
                (capture.Of messageFlow "narrated n1" |> List.map (fun line -> line.Text))
                [ "Workflow narrated n1 started"
                  "Workflow narrated n1 completed step 0 price (Activity)"
                  "Workflow narrated n1 received signal approval"
                  "Workflow narrated n1 completed step 1 approval (Signal)"
                  "Workflow narrated n1 timed out at step 2 extra (Signal)"
                  "Workflow narrated n1 completed step 3 sleep (Timer)"
                  "Workflow narrated n1 finished" ]
                "one line per stored step of the run, in order"
            Expect.isTrue
                (capture.Of messageFlow "narrated n1" |> List.forall (fun line -> line.Level = LogLevel.Information))
                "progress is written at Information"
            Expect.isEmpty
                (capture.Lines |> List.filter (fun line -> line.Text.Contains "secret"))
                "no line holds an input, a step result, a signal payload, or the workflow's result"

let private cancelTest =
    testCase "workflow log: a cancellation is told when it is requested and when the workflow ends" <| fun _ ->
        withSystem "WorkflowLogCancel" <| fun api capture ->
            let flow =
                Workflows.register api (Workflow.define "stopped" (fun (_: unit) -> workflow {
                    let! (answer: string) = waitSignal "never"
                    return answer
                }))
            sync (flow.Start "c1" ()) |> ignore
            eventually "the workflow waits" (fun () -> sync (flow.Status "c1") = WorkflowStatus.Running [ "0 never" ])
            sync (flow.Cancel "c1")
            Expect.equal (sync (flow.Result "c1" wait)) WorkflowOutcome.Cancelled "the workflow was cancelled"
            Expect.equal
                (capture.Of messageFlow "stopped c1" |> List.map (fun line -> line.Text))
                [ "Workflow stopped c1 started"
                  "Workflow stopped c1 was asked to cancel"
                  "Workflow stopped c1 was cancelled" ]
                "the request and the end are told"
            Expect.isEmpty (capture.Of workflows "stopped c1") "a step that a cancellation ended is not a failure to warn about"

let private replayTest =
    testCase "workflow log: a restart writes no progress line for a step stored before it" <| fun _ ->
        let db = newDb ()
        let capture = new Capture()
        let gate = new ManualResetEventSlim(false)
        let definition =
            Workflow.define "replayed" (fun (_: unit) -> workflow {
                let! first = activity "first" Retry.none (fun _ -> task { return 1 })
                let! second = activity "second" (Retry.forever (TimeSpan.FromMinutes 5.0)) (fun context -> task {
                    do! Task.Run(fun () -> gate.Wait context.Cancellation)
                    return first + 1 })
                return second
            })
        let linesWith (text: string) = capture.Of messageFlow text |> List.length
        try
            let first = boot db "WorkflowLogReplay" capture
            let flow = Workflows.register first definition
            sync (flow.Start "r1" ()) |> ignore
            eventually "the first step's line was written" (fun () -> linesWith "replayed r1 completed step 0 first" = 1)
            stop first

            let second = boot db "WorkflowLogReplay" capture
            try
                let flow = Workflows.register second definition
                gate.Set()
                Expect.equal (sync (flow.Result "r1" wait)) (WorkflowOutcome.Finished 2) "the workflow finished after the restart"
                eventually "the end was written" (fun () -> linesWith "replayed r1 finished" = 1)
            finally
                stop second

            Expect.equal (linesWith "replayed r1 started") 1 "the start is told once"
            Expect.equal (linesWith "replayed r1 completed step 0 first") 1 "a replayed step is not told again"
            Expect.equal (linesWith "replayed r1 completed step 1 second") 1 "the step that ran after the restart is told"
        finally
            deleteDb db

let private correlationTest =
    testCase "workflow log: lines carry the run's trace and a scope that names the workflow and the step" <| fun _ ->
        use listener = new ActivityListener()
        listener.ShouldListenTo <- fun source -> source.Name = Telemetry.WorkflowActivitySourceName || source.Name = "workflow-log-test"
        listener.Sample <- SampleActivity<ActivityContext>(fun _ -> ActivitySamplingResult.AllDataAndRecorded)
        ActivitySource.AddActivityListener listener
        use caller = new ActivitySource("workflow-log-test")
        withSystem "WorkflowLogCorrelation" <| fun api capture ->
            let app = api.LoggerFactory.CreateLogger "app"
            let mutable calls = 0
            let flow =
                Workflows.register api (Workflow.define "scoped" (fun (_: unit) -> workflow {
                    let! (_: int) = activity "work" { fast with MaxAttempts = Some 2 } (fun _ -> task {
                        app.LogInformation "inside the activity"
                        if Interlocked.Increment &calls < 2 then failwith "again"
                        return 1 })
                    return 1
                }))
            let traceId =
                use request = caller.StartActivity "request"
                sync (flow.Start "s1" ()) |> ignore
                (Unchecked.nonNull request).TraceId.ToHexString()
            sync (flow.Result "s1" wait) |> ignore
            eventually "the end was written" (fun () -> not (List.isEmpty (capture.Of messageFlow "scoped s1 finished")))

            let inside = capture.Of "app" "inside the activity"
            let retried = capture.Of workflows "scoped s1"
            let progress = capture.Of messageFlow "scoped s1"
            Expect.equal (List.length inside) 2 "the activity ran twice"
            Expect.equal (List.length retried) 1 "the first attempt's failure was logged"
            Expect.equal (List.length progress) 3 "started, one step, finished"
            for line in inside @ retried @ progress do
                Expect.equal line.TraceId (Some traceId) $"'{line.Text}' is in the trace of the caller that started the workflow"
                Expect.equal (line.Scope.TryFind "fcqrs.workflow.name") (Some "scoped") $"'{line.Text}' names the workflow in its scope"
                Expect.equal (line.Scope.TryFind "fcqrs.workflow.id") (Some "s1") $"'{line.Text}' names the instance in its scope"
            let step = inside @ retried @ [ progress[1] ]
            for line in step do
                Expect.equal (line.Scope.TryFind "fcqrs.workflow.step.path") (Some "0") $"'{line.Text}' names the step's path in its scope"
                Expect.equal (line.Scope.TryFind "fcqrs.workflow.step.name") (Some "work") $"'{line.Text}' names the step in its scope"
            Expect.equal
                (inside |> List.map (fun line -> line.Scope.TryFind "fcqrs.workflow.attempt"))
                [ Some "1"; Some "2" ]
                "a line written inside an activity names its attempt"

let private signalLinkTest =
    testCase "workflow log: the span of a signal wait links to the caller that sent the signal" <| fun _ ->
        let spans = ConcurrentBag<Activity>()
        use listener = new ActivityListener()
        listener.ShouldListenTo <- fun source -> source.Name = Telemetry.WorkflowActivitySourceName || source.Name = "workflow-link-test"
        listener.Sample <- SampleActivity<ActivityContext>(fun _ -> ActivitySamplingResult.AllDataAndRecorded)
        listener.ActivityStopped <- fun span -> if span.Source.Name = Telemetry.WorkflowActivitySourceName then spans.Add span
        ActivitySource.AddActivityListener listener
        use caller = new ActivitySource("workflow-link-test")
        withSystem "WorkflowLogSignalLink" <| fun api _ ->
            let flow =
                Workflows.register api (Workflow.define "linked" (fun (_: unit) -> workflow {
                    let! (answer: string) = waitSignal "go"
                    return answer
                }))
            let started =
                use request = caller.StartActivity "start request"
                sync (flow.Start "l1" ()) |> ignore
                (Unchecked.nonNull request).Context
            eventually "the workflow waits for its signal" (fun () -> sync (flow.Status "l1") = WorkflowStatus.Running [ "0 go" ])
            let sender =
                use request = caller.StartActivity "signal request"
                sync (Workflows.signal flow "l1" "go" "now")
                (Unchecked.nonNull request).Context
            Expect.notEqual sender.TraceId started.TraceId "the signal came from another trace"
            Expect.equal (sync (flow.Result "l1" wait)) (WorkflowOutcome.Finished "now") "the workflow finished"
            eventually "the wait's span was emitted" (fun () -> spans |> Seq.exists (fun span -> span.OperationName = "Step:go" && span.GetTagItem "fcqrs.workflow.id" = box "l1"))
            let waited = spans |> Seq.find (fun span -> span.OperationName = "Step:go" && span.GetTagItem "fcqrs.workflow.id" = box "l1")
            Expect.equal waited.TraceId started.TraceId "the wait's span stays in the trace that started the workflow"
            Expect.equal
                [ for link in waited.Links -> link.Context.TraceId, link.Context.SpanId ]
                [ sender.TraceId, sender.SpanId ]
                "and links to the span that sent the signal"

let tests =
    testSequenced (
        testList "workflow log" [ stepFailureTest; faultTest; progressTest; cancelTest; replayTest; correlationTest; signalLinkTest ]
    )
