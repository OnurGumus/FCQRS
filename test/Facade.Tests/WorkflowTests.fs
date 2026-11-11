module WorkflowTests

// Workflows on a real actor system: start, status, result, activities with retry, timers,
// recovery after the actor system stops, and tracing.

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
open Microsoft.Extensions.Logging.Abstractions
open FCQRS.Common
open FCQRS.FSharp
open FCQRS.Workflows

let private newDb () = Path.Combine(Path.GetTempPath(), $"fcqrs_workflow_{Guid.NewGuid():N}.db")

let private deleteDb (db: string) =
    SqliteConnection.ClearAllPools()
    for path in [ db; db + "-wal"; db + "-shm" ] do
        if File.Exists path then File.Delete path

let private boot (db: string) (name: string) (settings: (string * string) list) : IActor =
    let configuration =
        VerifySerialization.configuration()
            .AddInMemoryCollection([ for key, value in settings -> KeyValuePair<string, string | null>(key, value) ])
            .Build()
    Fcqrs.actor configuration NullLoggerFactory.Instance (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={db};")) name

let private stop (api: IActor) = api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let private withSystem name (run: IActor -> unit) =
    let db = newDb ()
    let api = boot db name []
    try
        run api
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

let private startTest =
    testCase "workflow: a started workflow runs to its result, and a second start stores nothing" <| fun _ ->
        withSystem "WorkflowStart" <| fun api ->
            let greet =
                Workflows.register api (Workflow.define "greet" (fun (name: string) -> workflow {
                    let! at = now
                    let! id = newGuid
                    return $"hello {name}", at.Kind, id <> Guid.Empty
                }))
            Expect.equal (sync (greet.Status "g1")) WorkflowStatus.NotStarted "an unknown ID was not started"
            Expect.equal (sync (greet.Start "g1" "Ada")) StartResult.Started "the first start is accepted"
            Expect.equal (sync (greet.Result "g1" wait)) (WorkflowOutcome.Finished("hello Ada", DateTimeKind.Utc, true)) "the body's value is the result"
            Expect.equal (sync (greet.Start "g1" "Bob")) (StartResult.AlreadyStarted WorkflowStatus.Completed) "the second start reports the instance"
            Expect.equal (sync (greet.Result "g1" wait)) (WorkflowOutcome.Finished("hello Ada", DateTimeKind.Utc, true)) "the result is unchanged"
            Expect.throwsT<ArgumentException> (fun () -> sync (greet.Start "a~b" "x") |> ignore) "an ID with '~' is refused"
            Expect.throwsT<InvalidOperationException> (fun () -> sync (greet.Result "nobody" wait) |> ignore) "a result needs a started workflow"

let private faultTest =
    testCase "workflow: a body that throws ends as faulted" <| fun _ ->
        withSystem "WorkflowFault" <| fun api ->
            let broken =
                Workflows.register api (Workflow.define "broken" (fun (reason: string) -> workflow {
                    let! (_: DateTime) = now
                    failwith reason
                    return 1
                }))
            sync (broken.Start "b1" "no stock") |> ignore
            match sync (broken.Result "b1" wait) with
            | WorkflowOutcome.Faulted error ->
                Expect.equal error.Kind ErrorKind.BodyFailed "the fault names the body"
                Expect.equal error.Message "no stock" "the fault keeps the message"
            | other -> failtest $"expected a fault, found {other}"
            match sync (broken.Status "b1") with
            | WorkflowStatus.Failed _ -> ()
            | other -> failtest $"expected Failed, found {other}"

let private retryTest =
    testCase "workflow: an activity is retried by its policy, and its failure is the body's to handle" <| fun _ ->
        withSystem "WorkflowRetry" <| fun api ->
            let calls = ConcurrentDictionary<string, int>()
            let keys = ConcurrentBag<string>()
            let count name = calls.AddOrUpdate(name, 1, fun _ seen -> seen + 1)
            let flow =
                Workflows.register api (Workflow.define "retry" (fun (_: unit) -> workflow {
                    let! third =
                        activity "flaky" { fast with MaxAttempts = Some 5 } (fun context -> task {
                            keys.Add context.IdempotencyKey
                            if count "flaky" < 3 then failwith "not yet"
                            return context.Attempt
                        })
                    let! exhausted = workflow {
                        try
                            let! (_: int) = activity "down" { fast with MaxAttempts = Some 2 } (fun _ -> task {
                                count "down" |> ignore
                                return failwith "down"
                            })
                            return "no failure"
                        with :? WorkflowStepFailedException as failed ->
                            return string failed.Error.Kind
                    }
                    let! refused = workflow {
                        try
                            let! (_: int) =
                                activity "invalid" { fast with MaxAttempts = Some 5; ShouldRetry = fun error -> not (error :? ArgumentException) } (fun _ -> task {
                                    count "invalid" |> ignore
                                    return raise (ArgumentException "bad input")
                                })
                            return "no failure"
                        with :? WorkflowStepFailedException as failed ->
                            return string failed.Error.Kind
                    }
                    let! stuck = workflow {
                        try
                            // Ignores its token and never returns.
                            let! (_: int) =
                                activity "stuck" { fast with MaxAttempts = None; Deadline = Some(TimeSpan.FromMilliseconds 300.0); AttemptTimeout = Some(TimeSpan.FromMilliseconds 50.0) } (fun _ ->
                                    count "stuck" |> ignore
                                    TaskCompletionSource<int>().Task)
                            return "no failure"
                        with :? WorkflowStepFailedException as failed ->
                            return string failed.Error.Kind
                    }
                    return third, exhausted, refused, stuck
                }))
            sync (flow.Start "r1" ()) |> ignore
            Expect.equal (sync (flow.Result "r1" wait)) (WorkflowOutcome.Finished(3, "Exhausted", "NotRetryable", "DeadlineExceeded")) "each policy ended its step as declared"
            Expect.equal calls["flaky"] 3 "the flaky activity ran until it succeeded"
            Expect.equal calls["down"] 2 "two attempts were allowed"
            Expect.equal calls["invalid"] 1 "the filter refused a retry"
            Expect.isGreaterThan calls["stuck"] 1 "the attempt timeout moved a stuck attempt on"
            Expect.equal (keys |> Seq.distinct |> List.ofSeq) [ "retry/r1/0" ] "every attempt of a step carries the same idempotency key"

/// State that outlives an actor system, as an external system would.
type private Outside() =
    member val Calls = ConcurrentDictionary<string, int>()
    member val Gate = new ManualResetEventSlim(false)
    member this.Count(name: string) = this.Calls.AddOrUpdate(name, 1, fun _ seen -> seen + 1)

let private transferDefinition (outside: Outside) =
    Workflow.define "resume" (fun (amount: int) -> workflow {
        let! reserved = activity "reserve" (Retry.times 3) (fun _ -> task {
            outside.Count "reserve" |> ignore
            return amount * 2 })
        let! shipped = activity "ship" (Retry.forever (TimeSpan.FromMinutes 5.0)) (fun context -> task {
            outside.Count "ship" |> ignore
            // Blocks until the test opens the gate, as a slow external system would.
            do! Task.Run(fun () -> outside.Gate.Wait context.Cancellation)
            return reserved + 1 })
        return shipped
    })

let private restartTest =
    testCase "workflow: after the actor system stops, a running workflow resumes by itself and repeats no stored step" <| fun _ ->
        let db = newDb ()
        let outside = Outside()
        try
            let first = boot db "WorkflowRestart" []
            let flow = Workflows.register first (transferDefinition outside)
            sync (flow.Start "t1" 20) |> ignore
            eventually "the second activity is running" (fun () -> outside.Calls.ContainsKey "ship")
            stop first

            // Nothing is sent to the workflow: registering it is enough for it to resume.
            let second = boot db "WorkflowRestart" []
            try
                let flow = Workflows.register second (transferDefinition outside)
                eventually "the second activity ran again" (fun () -> outside.Calls["ship"] = 2)
                outside.Gate.Set()
                Expect.equal (sync (flow.Result "t1" wait)) (WorkflowOutcome.Finished 41) "the workflow finished with the stored first result"
                Expect.equal outside.Calls["reserve"] 1 "the stored step did not run again"
            finally
                stop second

            // A finished workflow answers from its journal.
            let third = boot db "WorkflowRestart" []
            try
                let flow = Workflows.register third (transferDefinition outside)
                Expect.equal (sync (flow.Result "t1" wait)) (WorkflowOutcome.Finished 41) "the result is read from the journal"
                Expect.equal outside.Calls["ship"] 2 "a finished workflow runs nothing"
            finally
                stop third
        finally
            deleteDb db

let private timerTest =
    testCase "workflow: a timer's due time is stored, so a restart does not postpone it" <| fun _ ->
        let db = newDb ()
        let shifted = [ "config:akka:scheduler:implementation", "FCQRS.Scheduler+ShiftedScheduler, FCQRS" ]
        let definition =
            Workflow.define "reminder" (fun (text: string) -> workflow {
                let! before = now
                do! sleep (TimeSpan.FromDays 1.0)
                let! after = now
                return text, (after - before).TotalHours >= 24.0
            })
        try
            let first = boot db "WorkflowTimer" shifted
            let flow = Workflows.register first definition
            sync (flow.Start "s1" "wake") |> ignore
            eventually "the workflow is asleep" (fun () -> sync (flow.Status "s1") = WorkflowStatus.Running [ "1 sleep" ])
            stop first

            let second = boot db "WorkflowTimer" shifted
            try
                (second.System.Scheduler :?> FCQRS.Scheduler.ShiftedScheduler).Shift <- TimeSpan.FromDays 1.0
                let flow = Workflows.register second definition
                Expect.equal (sync (flow.Result "s1" wait)) (WorkflowOutcome.Finished("wake", true)) "the timer fired once its stored due time had passed"
            finally
                stop second
        finally
            deleteDb db

let private shortTimerTest =
    testCase "workflow: a short timer fires without a restart" <| fun _ ->
        withSystem "WorkflowShortTimer" <| fun api ->
            let flow =
                Workflows.register api (Workflow.define "nap" (fun (_: unit) -> workflow {
                    do! sleep (TimeSpan.FromMilliseconds 200.0)
                    let! instant = now
                    do! until (instant.AddMilliseconds 100.0)
                    return "awake"
                }))
            sync (flow.Start "n1" ()) |> ignore
            Expect.equal (sync (flow.Result "n1" wait)) (WorkflowOutcome.Finished "awake") "both timers fired"

let private tracingTest =
    testCase "workflow: every span of a run joins the trace of the caller that started it" <| fun _ ->
        let spans = ConcurrentBag<Activity>()
        use listener = new ActivityListener()
        listener.ShouldListenTo <- fun source -> source.Name = Telemetry.WorkflowActivitySourceName || source.Name = "workflow-test"
        listener.Sample <- SampleActivity<ActivityContext>(fun _ -> ActivitySamplingResult.AllDataAndRecorded)
        listener.ActivityStopped <- fun span -> if span.Source.Name = Telemetry.WorkflowActivitySourceName then spans.Add span
        ActivitySource.AddActivityListener listener
        use caller = new ActivitySource("workflow-test")
        let db = newDb ()
        let outside = Outside()
        let inside = ConcurrentBag<string>()
        let definition =
            Workflow.define "traced" (fun (_: unit) -> workflow {
                let! first = activity "first" (Retry.times 2) (fun _ -> task {
                    match Activity.Current with
                    | null -> ()
                    | span -> inside.Add span.OperationName
                    return 1 })
                do! sleep (TimeSpan.FromMilliseconds 50.0)
                let! second = activity "second" (Retry.forever (TimeSpan.FromMinutes 5.0)) (fun context -> task {
                    do! Task.Run(fun () -> outside.Gate.Wait context.Cancellation)
                    return first + 1 })
                return second
            })
        try
            let first = boot db "WorkflowTracing" []
            let flow = Workflows.register first definition
            let traceId =
                use request = caller.StartActivity "request"
                sync (flow.Start "tr1" ()) |> ignore
                (Unchecked.nonNull request).TraceId
            eventually "the second activity is running" (fun () -> spans |> Seq.exists (fun span -> span.OperationName = "Step:sleep"))
            stop first
            let before = spans |> Seq.map (fun span -> span.OperationName) |> Seq.filter (fun name -> name = "Step:first" || name = "Attempt:first") |> Seq.length

            let second = boot db "WorkflowTracing" []
            try
                let flow = Workflows.register second definition
                outside.Gate.Set()
                Expect.equal (sync (flow.Result "tr1" wait)) (WorkflowOutcome.Finished 2) "the workflow finished"
                eventually "the end span was emitted" (fun () -> spans |> Seq.exists (fun span -> span.OperationName = "Workflow.End:traced"))
            finally
                stop second

            let names = spans |> Seq.map (fun span -> span.OperationName) |> Seq.distinct |> Set.ofSeq
            Expect.isTrue
                (Set.isSubset (set [ "Workflow.Start:traced"; "Step:first"; "Attempt:first"; "Step:sleep"; "Step:second"; "Attempt:second"; "Workflow.End:traced" ]) names)
                $"the run emitted its spans: {names}"
            Expect.isTrue (spans |> Seq.forall (fun span -> span.TraceId = traceId)) "every span, before and after the restart, is in the caller's trace"
            Expect.equal (List.ofSeq inside) [ "Attempt:first" ] "the attempt span is current inside the activity"
            let attempt = spans |> Seq.find (fun span -> span.OperationName = "Attempt:first")
            let step = spans |> Seq.find (fun span -> span.OperationName = "Step:first")
            Expect.equal attempt.ParentSpanId step.SpanId "an attempt is a child of its step"
            let slept = spans |> Seq.find (fun span -> span.OperationName = "Step:sleep")
            Expect.isSome (slept.TagObjects |> Seq.tryFind (fun tag -> tag.Key = "fcqrs.workflow.waited_ms")) "a wait's span says how long it waited"
            let after = spans |> Seq.map (fun span -> span.OperationName) |> Seq.filter (fun name -> name = "Step:first" || name = "Attempt:first") |> Seq.length
            Expect.equal after before "the restart emitted no span for a step stored before it"
            Expect.isTrue
                (spans |> Seq.forall (fun span -> span.TagObjects |> Seq.forall (fun tag -> tag.Key.StartsWith "fcqrs.workflow.")))
                "spans carry only workflow tags, never inputs or results"
        finally
            deleteDb db

let private startRaceTest =
    testCase "workflow: a status request that races a start does not put the started workflow to sleep" <| fun _ ->
        withSystem "WorkflowStartRace" <| fun api ->
            let ran = ConcurrentDictionary<string, bool>()
            let flow =
                Workflows.register api (Workflow.define "raced" (fun (key: string) -> workflow {
                    do! sleep (TimeSpan.FromMilliseconds 300.0)
                    let! (_: int) = activity "mark" Retry.none (fun _ -> task {
                        ran[key] <- true
                        return 1 })
                    return key
                }))
            let ids = [ for index in 1 .. 40 -> $"id{index}" ]
            // An unknown instance leaves memory after it answers a status request. The start can
            // arrive before that happens.
            for id in ids do
                [ async { let! _ = flow.Status id in return () }
                  async { let! _ = flow.Start id id in return () } ]
                |> Async.Parallel
                |> sync
                |> ignore
            // No further message is sent: each workflow must finish by itself.
            eventually "every started workflow ran its activity" (fun () -> ran.Count = ids.Length)

let private concurrentStartTest =
    testCase "workflow: of two starts sent together, one starts the workflow" <| fun _ ->
        withSystem "WorkflowConcurrentStart" <| fun api ->
            let flow = Workflows.register api (Workflow.define "once" (fun (text: string) -> workflow {
                do! sleep (TimeSpan.FromMilliseconds 100.0)
                return text }))
            for index in 1 .. 10 do
                let results =
                    [ flow.Start $"c{index}" "first"; flow.Start $"c{index}" "second" ] |> Async.Parallel |> sync
                Expect.equal (results |> Array.filter ((=) StartResult.Started) |> Array.length) 1 "exactly one start was accepted"
                match sync (flow.Result $"c{index}" wait) with
                | WorkflowOutcome.Finished text -> Expect.contains [ "first"; "second" ] text "the accepted start's input ran"
                | other -> failtest $"expected a result, found {other}"

// ---------------------------------------------------------------------------
// Signals, aggregate commands and parallel branches on a real actor system.
// ---------------------------------------------------------------------------

let private signalTest =
    testCase "workflow: signals are stored, consumed oldest first, and refused when no workflow is running" <| fun _ ->
        withSystem "WorkflowSignal" <| fun api ->
            let gate = new ManualResetEventSlim(false)
            let flow =
                Workflows.register api (Workflow.define "approval" (fun (_: unit) -> workflow {
                    // Holds the body back, so the first signals arrive before their waits.
                    let! (_: int) = activity "hold" Retry.none (fun _ -> task {
                        do! Task.Run(fun () -> gate.Wait())
                        return 1 })
                    let! (first: string) = waitSignal "answer"
                    let! (second: string) = waitSignal "answer"
                    let! (missing: string option) = waitSignalFor "answer" (TimeSpan.FromMilliseconds 200.0)
                    let! (late: string) = waitSignal "answer"
                    return first, second, missing, late
                }))
            Expect.throwsT<InvalidOperationException> (fun () -> sync (Workflows.signal flow "a1" "answer" "early") |> ignore) "an unknown workflow takes no signal"
            sync (flow.Start "a1" ()) |> ignore
            sync (Workflows.signal flow "a1" "answer" "one")
            sync (Workflows.signal flow "a1" "answer" "two")
            gate.Set()
            eventually "the third wait timed out" (fun () -> sync (flow.Status "a1") = WorkflowStatus.Running [ "4 answer" ])
            sync (Workflows.signal flow "a1" "answer" "three")
            Expect.equal (sync (flow.Result "a1" wait)) (WorkflowOutcome.Finished("one", "two", None, "three")) "each wait got the oldest stored signal, and the timed-out wait got none"
            Expect.throwsT<InvalidOperationException> (fun () -> sync (Workflows.signal flow "a1" "answer" "too late") |> ignore) "a finished workflow takes no signal"

let private signalRestartTest =
    testCase "workflow: a stored signal is still there after the actor system stops" <| fun _ ->
        let db = newDb ()
        let outside = Outside()
        let definition =
            Workflow.define "patient" (fun (_: unit) -> workflow {
                let! (_: int) = activity "hold" (Retry.forever (TimeSpan.FromMinutes 5.0)) (fun context -> task {
                    do! Task.Run(fun () -> outside.Gate.Wait context.Cancellation)
                    return 1 })
                let! (answer: int) = waitSignal "answer"
                return answer
            })
        try
            let first = boot db "WorkflowSignalRestart" []
            let flow = Workflows.register first definition
            sync (flow.Start "p1" ()) |> ignore
            sync (Workflows.signal flow "p1" "answer" 42)
            stop first
            let second = boot db "WorkflowSignalRestart" []
            try
                let flow = Workflows.register second definition
                outside.Gate.Set()
                Expect.equal (sync (flow.Result "p1" wait)) (WorkflowOutcome.Finished 42) "the signal stored before the restart completes the wait after it"
            finally
                stop second
        finally
            deleteDb db

open Account

let private accountsOf (api: IActor) =
    let accounts =
        Fcqrs.aggregate api
            { Name = "Account"
              Initial = initial
              Decide = decide
              Fold = fold
              Snapshots = Default
              Passivation = PassivationPolicy.Default }
    Fcqrs.wireSagaStarters api []
    accounts

let private balance (accounts: AggregateHandle<AccountCommand, AccountEvent>) (account: string) =
    // A withdrawal larger than any balance is rejected with the balance.
    match (sync (accounts.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId account) (Withdraw 1_000_000_000m) (fun _ -> true))).EventDetails with
    | Rejected reason -> reason
    | other -> string other

/// A transfer as a workflow: take the money, deliver it, and give it back if delivery is refused.
let private transfer (accounts: AggregateHandle<AccountCommand, AccountEvent>) =
    Workflow.define "transfer" (fun (transferId: string, source: string, target: string, amount: decimal) -> workflow {
        let! taken = send accounts source (SendTransfer(transferId, target, amount))
        match taken with
        | TransferSent _ ->
            let! delivered = send accounts target (ReceiveTransfer(transferId, source, amount))
            match delivered with
            | TransferReceived _ -> return "moved"
            | _ ->
                let! (_: AccountEvent) = send accounts source (RefundTransfer(transferId, target, amount))
                return "refunded"
        | Rejected reason -> return reason
        | other -> return string other
    })

let private commandTest =
    testCase "workflow: a workflow sends commands to aggregates and decides on their replies" <| fun _ ->
        withSystem "WorkflowCommand" <| fun api ->
            let accounts = accountsOf api
            let transfers = Workflows.register api (transfer accounts)
            let command account details =
                sync (accounts.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId account) details (fun _ -> true)) |> ignore
            command "alice" (Open "Alice")
            command "alice" (Deposit 100m)
            command "bob" (Open "Bob")
            sync (transfers.Start "t1" ("t1", "alice", "bob", 30m)) |> ignore
            Expect.equal (sync (transfers.Result "t1" wait)) (WorkflowOutcome.Finished "moved") "the money moved"
            // Carol has no account, so delivery is refused and the money goes back.
            sync (transfers.Start "t2" ("t2", "alice", "carol", 50m)) |> ignore
            Expect.equal (sync (transfers.Result "t2" wait)) (WorkflowOutcome.Finished "refunded") "the refused delivery was refunded"
            sync (transfers.Start "t3" ("t3", "alice", "bob", 500m)) |> ignore
            Expect.equal (sync (transfers.Result "t3" wait)) (WorkflowOutcome.Finished "Insufficient funds: 70 available") "a rejection is a reply the body reads"
            Expect.equal (balance accounts "alice") "Insufficient funds: 70 available" "Alice paid once for t1 and was refunded t2"
            Expect.equal (balance accounts "bob") "Insufficient funds: 30 available" "Bob received t1 once"

let private parallelCommandTest =
    testCase "workflow: parallel commands to one aggregate each get their own reply" <| fun _ ->
        withSystem "WorkflowParallelCommand" <| fun api ->
            let accounts = accountsOf api
            let flow =
                Workflows.register api (Workflow.define "deposits" (fun (account: string) -> workflow {
                    let! (_: AccountEvent) = send accounts account (Open "Dana")
                    let! replies = all [ for amount in [ 1m; 2m; 4m; 8m ] -> send accounts account (Deposit amount) ]
                    let! first = race [ workflow { do! sleep (TimeSpan.FromSeconds 30.0)
                                                   return "timer" }
                                        workflow { let! (_: AccountEvent) = send accounts account (Deposit 16m)
                                                   return "deposit" } ]
                    return replies, first
                }))
            sync (flow.Start "d1" "dana") |> ignore
            Expect.equal
                (sync (flow.Result "d1" wait))
                (WorkflowOutcome.Finished([ Deposited 1m; Deposited 2m; Deposited 4m; Deposited 8m ], "deposit"))
                "each branch received the reply to its own command, and the race took the first branch to finish"
            Expect.equal (balance accounts "dana") "Insufficient funds: 31 available" "every deposit was applied once"

let private commandRestartTest =
    testCase "workflow: a command whose reply is stored is not sent again after a restart" <| fun _ ->
        let db = newDb ()
        let outside = Outside()
        let definition accounts =
            Workflow.define "saver" (fun (account: string) -> workflow {
                let! (_: AccountEvent) = send accounts account (Open "Eve")
                let! (_: AccountEvent) = send accounts account (Deposit 10m)
                let! (_: int) = activity "hold" (Retry.forever (TimeSpan.FromMinutes 5.0)) (fun context -> task {
                    outside.Count "hold" |> ignore
                    do! Task.Run(fun () -> outside.Gate.Wait context.Cancellation)
                    return 1 })
                let! (_: AccountEvent) = send accounts account (Deposit 5m)
                return "saved"
            })
        try
            let first = boot db "WorkflowCommandRestart" []
            let flow = Workflows.register first (definition (accountsOf first))
            sync (flow.Start "s1" "eve") |> ignore
            eventually "the workflow is holding" (fun () -> outside.Calls.ContainsKey "hold")
            stop first
            let second = boot db "WorkflowCommandRestart" []
            try
                let accounts = accountsOf second
                let flow = Workflows.register second (definition accounts)
                outside.Gate.Set()
                Expect.equal (sync (flow.Result "s1" wait)) (WorkflowOutcome.Finished "saved") "the workflow finished after the restart"
                Expect.equal (balance accounts "eve") "Insufficient funds: 15 available" "the first deposit was not sent again"
            finally
                stop second
        finally
            deleteDb db

type SilentCommand = Ask
type SilentEvent = Answered

let private unknownOutcomeTest =
    testCase "workflow: a command that gets no reply ends as outcome unknown, not as a rejection" <| fun _ ->
        let db = newDb ()
        // Each attempt waits one second for a reply.
        let api = boot db "WorkflowUnknown" [ "config:akka:fcqrs:command-timeout", "1s" ]
        try
            let silent =
                Fcqrs.aggregate api
                    { Name = "Silent"
                      Initial = 0
                      Decide = fun (_: Command<SilentCommand>) _ -> (IgnoreEvent: EventAction<SilentEvent>)
                      Fold = fun _ state -> state
                      Snapshots = NoSnapshots
                      Passivation = PassivationPolicy.Default }
            Fcqrs.wireSagaStarters api []
            let policy =
                { Retry.none with
                    Schedule = FixedInterval(TimeSpan.FromMilliseconds 50.0)
                    MaxAttempts = Some 2
                    ShouldRetry = fun error -> error :? TimeoutException }
            let flow =
                Workflows.register api (Workflow.define "asker" (fun (_: unit) -> workflow {
                    try
                        let! (_: SilentEvent) = sendWith policy silent "quiet" Ask
                        return "answered"
                    with :? WorkflowStepFailedException as failed ->
                        return string failed.Error.Kind
                }))
            sync (flow.Start "u1" ()) |> ignore
            Expect.equal (sync (flow.Result "u1" wait)) (WorkflowOutcome.Finished "OutcomeUnknown") "no reply means the outcome is unknown"
        finally
            stop api
            deleteDb db

// ---------------------------------------------------------------------------
// Cancellation, child workflows, and parking.
// ---------------------------------------------------------------------------

let private cancelTest =
    testCase "workflow: a cancelled workflow fails its pending steps, runs its cleanup, and stays cancelled" <| fun _ ->
        let db = newDb ()
        let seen = ConcurrentBag<string>()
        let definition =
            Workflow.define "booking" (fun (clean: bool) -> workflow {
                try
                    let! (_: int list) =
                        all [ for name in [ "hotel"; "flight" ] ->
                                activity name (Retry.forever (TimeSpan.FromMinutes 5.0)) (fun context -> task {
                                    seen.Add $"{name} started"
                                    try
                                        do! Task.Delay(Timeout.Infinite, context.Cancellation)
                                    with :? OperationCanceledException ->
                                        seen.Add $"{name} token cancelled"
                                        raise (OperationCanceledException())
                                    return 1 }) ]
                    return "booked"
                with :? WorkflowStepFailedException as failed when failed.Error.Kind = ErrorKind.Cancelled ->
                    if clean then
                        let! (_: int) = activity "release" Retry.none (fun _ -> task {
                            seen.Add "released"
                            return 1 })
                        ()
                    return raise failed
            })
        try
            let first = boot db "WorkflowCancel" []
            let flow = Workflows.register first definition
            Expect.throwsT<InvalidOperationException> (fun () -> sync (flow.Cancel "nobody")) "an unknown workflow cannot be cancelled"
            sync (flow.Start "b1" true) |> ignore
            eventually "both branches are running" (fun () -> seen |> Seq.filter (fun text -> text.EndsWith "started") |> Seq.length = 2)
            sync (flow.Cancel "b1")
            Expect.equal (sync (flow.Result "b1" wait)) WorkflowOutcome.Cancelled "the workflow ended as cancelled"
            eventually "both activities saw their token cancelled" (fun () -> seen |> Seq.filter (fun text -> text.EndsWith "token cancelled") |> Seq.length = 2)
            Expect.contains seen "released" "the cleanup step ran"
            sync (flow.Cancel "b1")
            Expect.equal (sync (flow.Status "b1")) WorkflowStatus.Cancelled "cancelling again changes nothing"
            stop first
            let second = boot db "WorkflowCancel" []
            try
                let flow = Workflows.register second definition
                Expect.equal (sync (flow.Result "b1" wait)) WorkflowOutcome.Cancelled "the workflow is still cancelled after a restart"
                Expect.equal (seen |> Seq.filter ((=) "released") |> Seq.length) 1 "the cleanup step did not run again"
            finally
                stop second
        finally
            deleteDb db

let private childTest =
    testCase "workflow: a parent starts children, waits for some, and sees a child's fault" <| fun _ ->
        withSystem "WorkflowChild" <| fun api ->
            let detachedDone = new ManualResetEventSlim(false)
            let doubleIt = Workflow.define "double" (fun (value: int) -> workflow {
                do! sleep (TimeSpan.FromMilliseconds 50.0)
                return value * 2 })
            let failing = Workflow.define "failing" (fun (reason: string) -> workflow {
                let! (_: DateTime) = now
                failwith reason
                return 0 })
            let slow = Workflow.define "slow" (fun (_: unit) -> workflow {
                do! sleep (TimeSpan.FromMilliseconds 400.0)
                let! (_: int) = activity "tell" Retry.none (fun _ -> task {
                    detachedDone.Set()
                    return 1 })
                return "done" })
            let unregistered = Workflow.define "unregistered" (fun (_: unit) -> workflow { return 1 })
            let children = Workflows.register api doubleIt
            Workflows.register api failing |> ignore
            let slowHandle = Workflows.register api slow
            let parent =
                Workflows.register api (Workflow.define "parent" (fun (value: int) -> workflow {
                    let! detached = startChild slow ()
                    let! doubled = child doubleIt value
                    let! both = all [ child doubleIt 10; child doubleIt 20 ]
                    let! fault = workflow {
                        try
                            let! (_: int) = child failing "no stock"
                            return "no fault"
                        with :? WorkflowStepFailedException as failed ->
                            return $"{failed.Error.Kind}"
                    }
                    let! missing = workflow {
                        try
                            let! (_: int) = child unregistered ()
                            return "no failure"
                        with :? WorkflowStepFailedException as failed ->
                            let named = failed.Error.Message.Contains "not registered"
                            return $"{failed.Error.Kind} {named}"
                    }
                    return detached.Id, doubled, both, fault, missing
                }))
            sync (parent.Start "p1" 21) |> ignore
            Expect.equal
                (sync (parent.Result "p1" wait))
                (WorkflowOutcome.Finished("p1:0", 42, [ 20; 40 ], "ChildFaulted", "NotRetryable True"))
                "the parent got each child's outcome"
            Expect.equal (sync (children.Result "p1:1" wait)) (WorkflowOutcome.Finished 42) "a child is an ordinary instance with a derived ID"
            Expect.isTrue (detachedDone.Wait(TimeSpan.FromSeconds 20.0)) "the detached child finished after its parent"
            Expect.equal (sync (slowHandle.Result "p1:0" wait)) (WorkflowOutcome.Finished "done") "and has its own result"

let private childRestartTest =
    testCase "workflow: a parent restarted while its child runs gets the child's result, and cancelling a parent cancels the child it waits for" <| fun _ ->
        let db = newDb ()
        let outside = Outside()
        let worker = Workflow.define "worker" (fun (value: int) -> workflow {
            let! result = activity "work" (Retry.forever (TimeSpan.FromMinutes 5.0)) (fun context -> task {
                outside.Count $"work {value}" |> ignore
                do! Task.Run(fun () -> outside.Gate.Wait context.Cancellation)
                return value + 1 })
            return result })
        let boss = Workflow.define "boss" (fun (value: int) -> workflow {
            let! result = child worker value
            return result * 10 })
        try
            let first = boot db "WorkflowChildRestart" []
            Workflows.register first worker |> ignore
            let flow = Workflows.register first boss
            sync (flow.Start "x1" 1) |> ignore
            sync (flow.Start "x2" 2) |> ignore
            eventually "both children are working" (fun () -> outside.Calls.ContainsKey "work 1" && outside.Calls.ContainsKey "work 2")
            stop first
            let second = boot db "WorkflowChildRestart" []
            try
                let workers = Workflows.register second worker
                let flow = Workflows.register second boss
                eventually "both children resumed" (fun () -> outside.Calls["work 1"] = 2 && outside.Calls["work 2"] = 2)
                sync (flow.Cancel "x2")
                Expect.equal (sync (flow.Result "x2" wait)) WorkflowOutcome.Cancelled "the cancelled parent ended"
                Expect.equal (sync (workers.Result "x2:0" wait)) WorkflowOutcome.Cancelled "the child it waited for was cancelled with it"
                outside.Gate.Set()
                Expect.equal (sync (flow.Result "x1" wait)) (WorkflowOutcome.Finished 20) "the other parent got its child's result after the restart"
                match sync (workers.Status "x1:0") with
                | WorkflowStatus.Completed -> ()
                | other -> failtest $"the child finished once, found {other}"
            finally
                stop second
        finally
            deleteDb db

let private parkingTest =
    testCase "workflow: an instance whose journal does not match its code is parked, and resumes with code that matches" <| fun _ ->
        let db = newDb ()
        let outside = Outside()
        let definition (second: string) =
            Workflow.define "evolving" (fun (value: int) -> workflow {
                let! first = activity "first" Retry.none (fun _ -> task { return value + 1 })
                let! last = activity second (Retry.forever (TimeSpan.FromMinutes 5.0)) (fun context -> task {
                    outside.Count second |> ignore
                    do! Task.Run(fun () -> outside.Gate.Wait context.Cancellation)
                    return first + 1 })
                return last })
        let parked status = match status with WorkflowStatus.Parked _ -> true | _ -> false
        try
            let first = boot db "WorkflowParking" []
            let flow = Workflows.register first (definition "second")
            sync (flow.Start "e1" 1) |> ignore
            sync (flow.Start "e2" 1) |> ignore
            eventually "both are in their second step" (fun () -> outside.Calls.ContainsKey "second" && outside.Calls["second"] = 2)
            stop first

            // The second step was renamed while instances were in it.
            let renamed = boot db "WorkflowParking" []
            try
                let flow = Workflows.register renamed (definition "renamed")
                eventually "the instance is parked" (fun () -> parked (sync (flow.Status "e1")))
                Expect.isFalse (outside.Calls.ContainsKey "renamed") "a parked instance runs nothing"
                // A parked instance cannot run cleanup, so cancelling it ends it.
                sync (flow.Cancel "e2")
                Expect.equal (sync (flow.Result "e2" wait)) WorkflowOutcome.Cancelled "a parked instance can be cancelled"
            finally
                stop renamed

            let restored = boot db "WorkflowParking" []
            try
                let flow = Workflows.register restored (definition "second")
                outside.Gate.Set()
                Expect.equal (sync (flow.Result "e1" wait)) (WorkflowOutcome.Finished 3) "with matching code the instance resumes and finishes"
            finally
                stop restored
        finally
            deleteDb db

let tests =
    testSequenced (
        testList "workflow"
            [ startTest; faultTest; retryTest; shortTimerTest; restartTest; timerTest; tracingTest; startRaceTest; concurrentStartTest
              signalTest; signalRestartTest; commandTest; parallelCommandTest; commandRestartTest; unknownOutcomeTest
              cancelTest; childTest; childRestartTest; parkingTest ]
    )
