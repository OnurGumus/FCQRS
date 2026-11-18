module WorkflowTestHarnessTests

// WorkflowTest runs a workflow in memory, without an actor system. These tests check that it
// stores what the actor stores, gives step outcomes, moves time, cancels, restarts, parks a
// journal that does not match, and finds a body that is not deterministic.

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging.Abstractions
open FCQRS.FSharp
open FCQRS.Workflows

type QuoteRequest = { Symbol: string; Quantity: int }

let private policy = Retry.times 3 (TimeSpan.FromSeconds 30.0)

/// Fails the test if an activity runs: the harness gives step outcomes, it runs no activity.
let private never (_: AttemptContext) : Task<'T> = failwith "the harness runs no activity"

let private quote (price: AttemptContext -> Task<decimal>) (mail: AttemptContext -> Task<string>) =
    Workflow.define "quote" (fun (request: QuoteRequest) -> workflow {
        let! unitPrice = activity "price" policy price
        let total = unitPrice * decimal request.Quantity
        let! approved = workflow {
            if total <= 1000m then
                return true
            else
                let! answer = waitSignalFor<bool> "approval" (TimeSpan.FromDays 1.0)
                return defaultArg answer false
        }
        if not approved then
            return $"declined {total.ToString CultureInfo.InvariantCulture}"
        else
            let! receipt = activity "send-quote" policy mail
            return $"{receipt} {total.ToString CultureInfo.InvariantCulture}"
    })

let private large = { Symbol = "ABC"; Quantity = 100 }

let private walkThrough =
    testCase "workflow test: a test gives each step's outcome and reads the journal the body stored" <| fun _ ->
        let test = WorkflowTest.Start(quote never never, large)
        Expect.equal [ for step in test.Waiting -> step.Name ] [ "price" ] "the body waits for the price"
        test.Complete("price", 12.5m)
        let approval = Seq.exactlyOne test.Waiting
        Expect.equal (approval.Name, approval.Kind) ("approval", StepKind.Signal) "a quote over 1000 waits for approval"
        Expect.equal approval.Due (Nullable(WorkflowTest.DefaultStart.AddDays 1.0)) "the wait's timeout is stored"
        Expect.isTrue (test.Status.IsWaitingFor "approval") "Status reports the wait"
        test.Signal("approval", true)
        test.Complete("send-quote", "sent")
        Expect.equal test.Result "sent 1250.0" "the body's result is read back"
        Expect.equal test.Status WorkflowStatus.Completed "the instance ended"
        Expect.equal
            (List.ofSeq test.Summary)
            [ "started"
              "scheduled 0 price"; "completed 0 price"
              "scheduled 1 approval"; "signal approval"; "completed 1 approval"
              "scheduled 2 send-quote"; "completed 2 send-quote"
              "finished" ]
            "one line per stored record"
        test.CheckReplay()

let private timeTest =
    testCase "workflow test: Advance fires a signal wait's timeout and a timer when they are due" <| fun _ ->
        let test = WorkflowTest.Start(quote never never, large)
        test.Complete("price", 12.5m)
        test.Advance(TimeSpan.FromHours 23.0)
        Expect.isTrue (test.Status.IsWaitingFor "approval") "the wait has not timed out"
        test.Advance(TimeSpan.FromHours 1.0)
        Expect.equal test.Result "declined 1250.0" "the timed-out wait gives None"
        test.CheckReplay()
        let napping =
            Workflow.define "nap" (fun () -> workflow {
                do! sleep (TimeSpan.FromMinutes 5.0)
                let! woke = now
                return woke
            })
        let nap = WorkflowTest.Start(napping, ())
        Expect.equal [ for step in nap.Waiting -> step.Kind ] [ StepKind.Timer ] "the body sleeps"
        nap.Advance(TimeSpan.FromMinutes 5.0)
        Expect.equal nap.Result (WorkflowTest.DefaultStart.AddMinutes 5.0) "now reads the test's time"
        nap.CheckReplay()

let private failureTest =
    testCase "workflow test: a failed step reaches the body, and a value it cannot read back fails as NotRetryable" <| fun _ ->
        let handled =
            Workflow.define "handled" (fun () -> workflow {
                try
                    let! (price: decimal) = activity "price" policy never
                    return string price
                with :? WorkflowStepFailedException as failed ->
                    return $"{failed.StepName} {failed.Error.Kind} {failed.Error.Message}"
            })
        let test = WorkflowTest.Start(handled, ())
        test.Fail("price", TimeoutException "slow")
        Expect.equal test.Result "price Exhausted slow" "the body sees the failure"
        test.CheckReplay()
        let unreadable =
            Workflow.define "unreadable" (fun () -> workflow {
                try
                    let! (value: IComparable) = activity "odd" policy never
                    return string value
                with :? WorkflowStepFailedException as failed ->
                    return string failed.Error.Kind
            })
        let odd = WorkflowTest.Start(unreadable, ())
        odd.Complete("odd", 1 :> IComparable)
        Expect.equal odd.Result "NotRetryable" "a result that replay cannot read fails the step"
        let unhandled =
            Workflow.define "unhandled" (fun () -> workflow {
                let! (_: int) = activity "count" policy never
                failwith "broken"
                return 1
            })
        let faulted = WorkflowTest.Start(unhandled, ())
        faulted.Complete("count", 3)
        match faulted.Status with
        | WorkflowStatus.Failed error -> Expect.equal (error.Kind, error.Message) (ErrorKind.BodyFailed, "broken") "the body's exception faults it"
        | other -> failtest $"expected Failed, found {other}"
        Expect.throwsT<FCQRS.Workflows.CSharp.WorkflowFaultedException> (fun () -> faulted.Result |> ignore) "Result raises for a fault"

let private refusalTest =
    testCase "workflow test: a step that does not wait, and a signal to an ended instance, are refused" <| fun _ ->
        let test = WorkflowTest.Start(quote never never, large)
        Expect.throwsT<InvalidOperationException> (fun () -> test.Complete("send-quote", "early")) "send-quote has not started"
        Expect.throwsT<InvalidOperationException> (fun () -> test.Result |> ignore) "the instance has not ended"
        test.Complete("price", 1m)
        Expect.throwsT<InvalidOperationException> (fun () -> test.Complete("approval", true)) "a signal wait takes a signal"
        test.Complete("send-quote", "sent")
        Expect.throwsT<InvalidOperationException> (fun () -> test.Signal("approval", true)) "an ended instance takes no signal"
        Expect.throwsT<ArgumentException> (fun () -> test.Advance(TimeSpan.FromSeconds -1.0)) "time moves forward"

let private cancelTest =
    testCase "workflow test: Cancel fails the waiting steps, the body cleans up, and the instance ends cancelled" <| fun _ ->
        let cleaning =
            Workflow.define "cleaning" (fun () -> workflow {
                try
                    let! (_: string) = activity "reserve" policy never
                    return "reserved"
                with :? WorkflowStepFailedException as failed when failed.Error.Kind = ErrorKind.Cancelled ->
                    let! (_: unit) = activity "release" policy never
                    return raise failed
            })
        let test = WorkflowTest.Start(cleaning, ())
        test.Cancel()
        Expect.equal [ for step in test.Waiting -> step.Name ] [ "release" ] "the cleanup step runs"
        test.Complete("release", ())
        Expect.equal test.Status WorkflowStatus.Cancelled "the body let the cancel pass"
        Expect.equal
            (List.ofSeq test.Summary)
            [ "started"; "scheduled 0 reserve"; "cancel requested"; "failed 0 reserve (Cancelled)"
              "scheduled 1 release"; "completed 1 release"; "cancelled" ]
            "the cancel and its consequences are stored"
        test.CheckReplay()

let private restartTest =
    testCase "workflow test: Restart replays the journal, and a step that was waiting waits again" <| fun _ ->
        let test = WorkflowTest.Start(quote never never, large)
        test.Complete("price", 12.5m)
        let before = List.ofSeq test.Journal
        test.Restart()
        Expect.equal (List.ofSeq test.Journal) before "a restart stores nothing new"
        Expect.equal [ for step in test.Waiting -> step.Name ] [ "approval" ] "the wait resumes; the price is not asked again"
        test.Signal("approval", true)
        test.Complete("send-quote", "sent")
        Expect.equal test.Result "sent 1250.0" "the instance finishes after the restart"
        test.CheckReplay()

let private resumeTest =
    testCase "workflow test: Recover runs new code over an old journal and parks it when a stored step changed" <| fun _ ->
        let old = WorkflowTest.Start(quote never never, large)
        old.Complete("price", 12.5m)
        let renamed =
            Workflow.define "quote" (fun (_: QuoteRequest) -> workflow {
                let! (_: decimal) = activity "unit-price" policy never
                return "renamed"
            })
        let parked = WorkflowTest.Recover(renamed, old.Journal)
        match parked.Status with
        | WorkflowStatus.Parked reason -> Expect.stringContains reason "'price'" "the reason names the stored step"
        | other -> failtest $"expected Parked, found {other}"
        let same = WorkflowTest.Recover(quote never never, old.Journal)
        Expect.equal [ for step in same.Waiting -> step.Name ] [ "approval" ] "matching code resumes at the wait"
        same.Signal("approval", true)
        same.Complete("send-quote", "sent")
        Expect.equal same.Result "sent 1250.0" "the resumed instance finishes"
        same.CheckReplay()

let private nondeterminismTest =
    testCase "workflow test: CheckReplay finds a body that takes another path on replay" <| fun _ ->
        // The body reads state outside any step, which differs between runs.
        let mutable runs = 0
        let fickle =
            Workflow.define "fickle" (fun () -> workflow {
                runs <- runs + 1
                let name = if runs = 1 then "first" else "later"
                let! (value: int) = activity name policy never
                return value
            })
        let test = WorkflowTest.Start(fickle, ())
        test.Complete("first", 1)
        Expect.equal test.Result 1 "the live run finishes"
        let error = Expect.throwsC (fun () -> test.CheckReplay()) id
        match error with
        | :? WorkflowReplayException as replay ->
            Expect.stringContains replay.Message "'scheduled 0 later'" "the error names the step the replay took"
            Expect.isGreaterThanOrEqual replay.RecordsBeforeRestart 1 "the exception names the restart"
        | other -> failtest $"expected WorkflowReplayException, found {other}"

let private parallelTest =
    testCase "workflow test: parallel steps with one name complete in the order they started, and replay agrees" <| fun _ ->
        let fanOut =
            Workflow.define "fan-out" (fun () -> workflow {
                let! (prices: decimal list) = all [ for _ in 1..3 -> activity "price" policy never ]
                return List.sum prices
            })
        let test = WorkflowTest.Start(fanOut, ())
        Expect.equal [ for step in test.Waiting -> step.Path ] [ "0/0/0"; "0/1/0"; "0/2/0" ] "three branches wait"
        for price in [ 1m; 2m; 3m ] do
            test.Complete("price", price)
        Expect.equal test.Result 6m "each branch got its value"
        test.CheckReplay()
        let twoSteps =
            Workflow.define "two-steps" (fun () -> workflow {
                let! (totals: decimal list) =
                    all [ for _ in 1..3 ->
                            workflow {
                                let! (price: decimal) = activity "price" policy never
                                let! (stock: int) = activity "stock" policy never
                                return price * decimal stock
                            } ]
                return List.sum totals
            })
        let staged = WorkflowTest.Start(twoSteps, ())
        staged.Complete("price", 1m)
        Expect.equal
            [ for step in staged.Waiting -> $"{step.Path} {step.Name}" ]
            [ "0/1/0 price"; "0/2/0 price"; "0/0/1 stock" ]
            "a step that starts later is listed later, though it reuses a freed slot"
        staged.Complete("price", 2m)
        staged.Complete("price", 3m)
        for stock in [ 1; 1; 1 ] do
            staged.Complete("stock", stock)
        Expect.equal staged.Result 6m "each branch took the outcomes in the order its steps started"
        staged.CheckReplay()

let private runsTest =
    testCase "workflow test: StartAgain begins a new run after a fault or cancel only, and the run starts from nothing" <| fun _ ->
        let signalled =
            Workflow.define "signalled" (fun (start: int) -> workflow {
                let! (charged: int) = activity "charge" policy never
                let! (go: int) = waitSignal "go"
                return start + charged + go
            })
        let test = WorkflowTest.Start(signalled, 100)
        // A signal stored in the first run, which no wait takes before the cancel.
        test.Signal("go", 1)
        Expect.equal (test.StartAgain 200) (StartResult.AlreadyStarted test.Status) "a running instance is not replaced"
        test.Cancel()
        Expect.equal test.Status WorkflowStatus.Cancelled "the first run was cancelled"
        Expect.equal (test.StartAgain 200) StartResult.Started "a cancelled instance starts again"
        test.Complete("charge", 20)
        Expect.equal [ for step in test.Waiting -> step.Name ] [ "go" ] "the new run does not take the first run's signal"
        test.Signal("go", 3)
        Expect.equal test.Result 223 "the new run has its own input and outcomes"
        match test.StartAgain 300 with
        | StartResult.AlreadyStarted WorkflowStatus.Completed -> ()
        | other -> failtest $"a finished instance is final, found {other}"
        test.CheckReplay()
        test.Restart()
        Expect.equal test.Result 223 "a restart replays the last run"

let private childRunTest =
    testCase "workflow test: a child started by a later run has an ID that names the run" <| fun _ ->
        let child = Workflow.define "child" (fun (n: int) -> workflow { return n })
        let parent =
            Workflow.define "parent" (fun () -> workflow {
                let! handle = startChild child 1
                return handle.Id
            })
        let test = WorkflowTest.Start(parent, ())
        test.Cancel()
        Expect.equal (test.StartAgain()) StartResult.Started "the cancelled parent starts again"
        let childIds =
            [ for record in test.Journal do
                  match record with
                  | WorkflowRecord.StepScheduled(_, "child", StepKind.Child, _, Some childId) -> childId
                  | _ -> () ]
        Expect.equal childIds [ "test:0"; "test#2:0" ] "each run's child has its own ID"
        test.CheckReplay()

let private resumeFaultTest =
    testCase "workflow test: Resume runs the failed step again and keeps the steps that completed before it" <| fun _ ->
        let order =
            Workflow.define "order" (fun () -> workflow {
                let! (reserved: int) = activity "reserve" policy never
                let! (charged: int) = activity "charge" policy never
                return reserved + charged
            })
        let test = WorkflowTest.Start(order, ())
        test.Complete("reserve", 1)
        test.Fail("charge", TimeoutException "the bank is down")
        Expect.isTrue (match test.Status with WorkflowStatus.Failed _ -> true | _ -> false) "the order faulted"
        test.Resume()
        Expect.equal [ for step in test.Waiting -> step.Name ] [ "charge" ] "only the failed step waits again"
        test.Complete("charge", 20)
        Expect.equal test.Result 21 "the order finishes with the stored reservation"
        Expect.equal
            (List.ofSeq test.Summary)
            [ "started"; "scheduled 0 reserve"; "completed 0 reserve"
              "scheduled 1 charge"; "failed 1 charge (Exhausted)"; "faulted (Exhausted)"
              "resumed 1"; "scheduled 1 charge"; "completed 1 charge"; "finished" ]
            "the resume is stored, and the reservation is not scheduled again"
        test.CheckReplay()
        test.Restart()
        Expect.equal test.Result 21 "a restart after the resume reads the result"

let private resumeRefusalTest =
    testCase "workflow test: Resume refuses an instance that did not fault from a step, or ran steps after the failure" <| fun _ ->
        let simple =
            Workflow.define "simple" (fun () -> workflow {
                let! (value: int) = activity "value" policy never
                return value
            })
        let finished = WorkflowTest.Start(simple, ())
        finished.Complete("value", 1)
        Expect.throwsT<InvalidOperationException> (fun () -> finished.Resume()) "a finished instance does not resume"
        let thrower =
            Workflow.define "thrower" (fun () -> workflow {
                let! (value: int) = activity "value" policy never
                if value > 0 then failwith "the body's own error"
                return value
            })
        let thrown = WorkflowTest.Start(thrower, ())
        thrown.Complete("value", 1)
        let error = Expect.throwsC (fun () -> thrown.Resume()) id
        Expect.stringContains error.Message "threw" "the body's own exception would fault again"
        let compensating =
            Workflow.define "compensating" (fun () -> workflow {
                try
                    let! (value: int) = activity "charge" policy never
                    return value
                with :? WorkflowStepFailedException as failed ->
                    let! (_: unit) = activity "refund" policy never
                    return raise failed
            })
        let compensated = WorkflowTest.Start(compensating, ())
        compensated.Fail("charge", TimeoutException "down")
        compensated.Complete("refund", ())
        let refused = Expect.throwsC (fun () -> compensated.Resume()) id
        Expect.stringContains refused.Message "after step 0 charge failed" "a body that compensated does not resume"

/// The harness stores the records the actor stores, in the same order.
let private fidelityTest =
    testCase "workflow test: an instance on an actor system stores the same records as the harness" <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), $"fcqrs_harness_{Guid.NewGuid():N}.db")
        let configuration =
            VerifySerialization.configuration()
                .AddInMemoryCollection([ KeyValuePair<string, string | null>("config:akka:fcqrs:workflow-idle-timeout", "1s") ])
                .Build()
        let api = Fcqrs.actor configuration NullLoggerFactory.Instance (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={db};")) "WorkflowHarness"
        let stored = ConcurrentQueue<WorkflowRecord>()
        try
            let definition = quote (fun _ -> Task.FromResult 12.5m) (fun _ -> Task.FromResult "sent")
            let quotes = Workflows.register api definition
            use _projection =
                Fcqrs.projection api (Projection.single FromStart (fun message ->
                    match message with
                    | :? WorkflowRecord as record -> stored.Enqueue record
                    | _ -> ())
                    |> Projection.ofEntities [ "quote" ])
            Async.RunSynchronously(quotes.Start "q-1" large) |> ignore
            let deadline = DateTime.UtcNow.AddSeconds 20.0
            while not ((Async.RunSynchronously(quotes.Status "q-1")).IsWaitingFor "approval") && DateTime.UtcNow < deadline do
                Thread.Sleep 50
            Async.RunSynchronously(Workflows.signal quotes "q-1" "approval" true)
            Expect.equal (Async.RunSynchronously(quotes.Result "q-1" (TimeSpan.FromSeconds 20.0))) (WorkflowOutcome.Finished "sent 1250.0") "the actor finished"
            while stored.Count < 9 && DateTime.UtcNow < deadline do
                Thread.Sleep 50
            let test = WorkflowTest.Start(definition, large)
            test.Complete("price", 12.5m)
            test.Signal("approval", true)
            test.Complete("send-quote", "sent")
            // Times differ between the two runs; everything else is the same.
            let shape (record: WorkflowRecord) =
                match record with
                | WorkflowRecord.Started(input, _, _, _) -> $"started {input}"
                | WorkflowRecord.StepScheduled(path, name, kind, _, _) -> $"scheduled {path} {name} {kind}"
                | WorkflowRecord.StepCompleted(path, name, kind, result, _) -> $"completed {path} {name} {kind} {result}"
                | WorkflowRecord.SignalReceived(name, payload, _, _) -> $"signal {name} {payload}"
                | WorkflowRecord.Finished(result, _) -> $"finished {result}"
                | other -> string other
            Expect.equal [ for record in test.Journal -> shape record ] [ for record in stored -> shape record ] "the same records in the same order"
        finally
            api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore
            SqliteConnection.ClearAllPools()
            for path in [ db; db + "-wal"; db + "-shm" ] do
                if File.Exists path then File.Delete path

let tests =
    testList "workflow test"
        [ walkThrough; timeTest; failureTest; refusalTest; cancelTest; restartTest; resumeTest
          nondeterminismTest; parallelTest; runsTest; childRunTest; resumeFaultTest; resumeRefusalTest; fidelityTest ]
