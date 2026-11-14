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

let private withSettings name (settings: (string * string) list) (run: IActor -> unit) =
    let db = newDb ()
    let api = boot db name settings
    try
        run api
    finally
        stop api
        deleteDb db

let private withSystem name (run: IActor -> unit) = withSettings name [] run

/// How long an instance that is not running stays in memory after its last request.
let private idleTimeKey = "config:akka:fcqrs:workflow-idle-timeout"

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
        // With no idle time, an instance asks to leave memory as soon as it has answered.
        withSettings "WorkflowStartRace" [ idleTimeKey, "0s" ] <| fun api ->
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
            // An unknown instance asks to leave memory after it answers a status request. The start
            // can arrive before the instance has stopped.
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

/// Whether the gated aggregate replies. Static, so it outlives an actor system.
let mutable private gatedReplies = false

type GatedCommand = Knock
type GatedEvent = Opened

let private commandRecoveryTest =
    testCase "workflow: a workflow that was waiting for a command's reply sends the command again soon after a restart" <| fun _ ->
        let db = newDb ()
        let gated (api: IActor) =
            let handle =
                Fcqrs.aggregate api
                    { Name = "Gated"
                      Initial = 0
                      Decide = fun (_: Command<GatedCommand>) _ -> if gatedReplies then PersistEvent Opened else IgnoreEvent
                      Fold = fun _ state -> state + 1
                      Snapshots = NoSnapshots
                      Passivation = PassivationPolicy.Default }
            Fcqrs.wireSagaStarters api []
            handle
        let definition door =
            Workflow.define "knocker" (fun (_: unit) -> workflow {
                let! (_: GatedEvent) = send door "door" Knock
                return "opened"
            })
        try
            gatedReplies <- false
            let first = boot db "WorkflowCommandRecovery" []
            let flow = Workflows.register first (definition (gated first))
            sync (flow.Start "k1" ()) |> ignore
            eventually "the workflow is waiting for the reply" (fun () -> sync (flow.Status "k1") = WorkflowStatus.Running [ "0 send" ])
            stop first
            gatedReplies <- true
            let second = boot db "WorkflowCommandRecovery" []
            try
                let timer = Diagnostics.Stopwatch.StartNew()
                let flow = Workflows.register second (definition (gated second))
                Expect.equal (sync (flow.Result "k1" wait)) (WorkflowOutcome.Finished "opened") "the command was sent again and answered"
                printfn "workflow command recovery: the recovered command step finished %d ms after registration" timer.ElapsedMilliseconds
                Expect.isLessThan timer.Elapsed (TimeSpan.FromSeconds 5.0) "the resend does not wait for a command timeout"
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
        let cleanupGate = new ManualResetEventSlim(false)
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
                            seen.Add "release started"
                            do! Task.Run(fun () -> cleanupGate.Wait())
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
            eventually "the cleanup step is running" (fun () -> seen |> Seq.contains "release started")
            // A caller that retries its cancel request must not fail the cleanup that is running.
            sync (flow.Cancel "b1")
            cleanupGate.Set()
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

// ---------------------------------------------------------------------------
// Akka.Streams and metrics.
// ---------------------------------------------------------------------------

open Akka.Streams.Dsl

let private streamTest =
    testCase "workflow: a stream starts one workflow per element, keeps to its limit, and a replayed stream starts none again" <| fun _ ->
        withSystem "WorkflowStream" <| fun api ->
            let calls = ConcurrentDictionary<int, int>()
            let mutable running = 0
            let mutable widest = 0
            let flow =
                Workflows.register api (Workflow.define "square" (fun (value: int) -> workflow {
                    let! squared = activity "square" Retry.none (fun _ -> task {
                        calls.AddOrUpdate(value, 1, fun _ seen -> seen + 1) |> ignore
                        let now = Interlocked.Increment &running
                        lock calls (fun () -> widest <- max widest now)
                        do! Task.Delay 30
                        Interlocked.Decrement &running |> ignore
                        return value * value })
                    return squared
                }))
            let run () =
                Source.From([ 1 .. 20 ])
                    .Via(WorkflowFlow.run flow (fun value -> $"sq{value}") 4 wait)
                    .RunWith(Sink.Seq(), api.Materializer)
                    .Result
                |> List.ofSeq
            let first = run ()
            Expect.equal first [ for value in 1 .. 20 -> struct ($"sq{value}", WorkflowOutcome.Finished(value * value)) ] "outcomes come out in input order"
            Expect.isLessThanOrEqual widest 4 "no more than four workflows ran at once"
            // The same elements again, as an upstream that replays from its offset would send them.
            Expect.equal (run ()) first "a replayed stream gets the same outcomes"
            Expect.isTrue (calls.Values |> Seq.forall ((=) 1)) "and runs no activity again"
            let starts =
                Source.From([ 20; 21 ])
                    .Via(WorkflowFlow.start flow (fun value -> $"sq{value}") 2)
                    .RunWith(Sink.Seq(), api.Materializer)
                    .Result
                |> List.ofSeq
            Expect.equal
                starts
                [ struct ("sq20", StartResult.AlreadyStarted WorkflowStatus.Completed); struct ("sq21", StartResult.Started) ]
                "a start flow reports which elements started a workflow"

let private metricsTest =
    testCase "workflow: the meter counts starts, ends and retries, and times steps" <| fun _ ->
        let measured = ConcurrentDictionary<string, float>()
        use listener = new Metrics.MeterListener()
        listener.InstrumentPublished <- fun instrument listener -> if instrument.Meter.Name = WorkflowTelemetry.MeterName then listener.EnableMeasurementEvents instrument
        let add (name: string) (value: float) = measured.AddOrUpdate(name, value, fun _ seen -> seen + value) |> ignore
        // A span of tags cannot leave the callback, so each callback reads its own.
        listener.SetMeasurementEventCallback<int64>(
            Metrics.MeasurementCallback<int64>(fun instrument value tags _ ->
                let mutable ours = false
                for tag in tags do
                    if tag.Key = "fcqrs.workflow.name" && string tag.Value = "metered" then ours <- true
                if ours then add instrument.Name (float value)))
        listener.SetMeasurementEventCallback<float>(
            Metrics.MeasurementCallback<float>(fun instrument value tags _ ->
                let mutable ours = false
                for tag in tags do
                    if tag.Key = "fcqrs.workflow.name" && string tag.Value = "metered" then ours <- true
                if ours then add instrument.Name value))
        listener.Start()
        withSystem "WorkflowMetrics" <| fun api ->
            let mutable calls = 0
            let flow =
                Workflows.register api (Workflow.define "metered" (fun (fail: bool) -> workflow {
                    let! (_: int) = activity "flaky" { fast with MaxAttempts = Some 3 } (fun _ -> task {
                        if Interlocked.Increment &calls < 3 then failwith "not yet"
                        return 1 })
                    do! sleep (TimeSpan.FromMilliseconds 30.0)
                    if fail then failwith "broken"
                    return 1
                }))
            sync (flow.Start "m1" false) |> ignore
            sync (flow.Result "m1" wait) |> ignore
            sync (flow.Start "m2" true) |> ignore
            sync (flow.Result "m2" wait) |> ignore
            eventually "both ends were counted" (fun () -> measured.ContainsKey "fcqrs.workflow.finished" && measured.ContainsKey "fcqrs.workflow.faulted")
            Expect.equal measured["fcqrs.workflow.started"] 2.0 "two workflows started"
            Expect.equal measured["fcqrs.workflow.finished"] 1.0 "one finished"
            Expect.equal measured["fcqrs.workflow.faulted"] 1.0 "one faulted"
            Expect.equal measured["fcqrs.workflow.activity.retries"] 2.0 "two attempts were retried"
            Expect.equal measured["fcqrs.workflow.running"] 0.0 "none is running at the end"
            Expect.isGreaterThan measured["fcqrs.workflow.step.duration"] 0.0 "step durations were recorded"

// ---------------------------------------------------------------------------
// Edges: a command past its deadline, a parked instance that was cancelling, results that
// cannot be read, idle instances, and the names records are stored under.
// ---------------------------------------------------------------------------

let private lateCommandTest =
    testCase "workflow: a command that was waiting for its reply is sent again after a restart, even past its deadline" <| fun _ ->
        let db = newDb ()
        let gated (api: IActor) =
            let handle =
                Fcqrs.aggregate api
                    { Name = "LateGated"
                      Initial = 0
                      Decide = fun (_: Command<GatedCommand>) _ -> if gatedReplies then PersistEvent Opened else IgnoreEvent
                      Fold = fun _ state -> state + 1
                      Snapshots = NoSnapshots
                      Passivation = PassivationPolicy.Default }
            Fcqrs.wireSagaStarters api []
            handle
        let deadline = TimeSpan.FromSeconds 5.0
        let policy: RetryPolicy =
            { Retry.none with
                Schedule = FixedInterval(TimeSpan.FromMilliseconds 50.0)
                MaxAttempts = None
                Deadline = Some deadline
                ShouldRetry = fun error -> error :? TimeoutException || error :? OperationCanceledException }
        let definition door =
            Workflow.define "late-knocker" (fun (_: unit) -> workflow {
                try
                    let! (_: GatedEvent) = sendWith policy door "door" Knock
                    return "opened"
                with :? WorkflowStepFailedException as failed ->
                    return string failed.Error.Kind
            })
        try
            gatedReplies <- false
            let first = boot db "WorkflowLateCommand" []
            let flow = Workflows.register first (definition (gated first))
            let sinceStart = Diagnostics.Stopwatch.StartNew()
            sync (flow.Start "k1" ()) |> ignore
            eventually "the workflow is waiting for the reply" (fun () -> sync (flow.Status "k1") = WorkflowStatus.Running [ "0 send" ])
            stop first
            Expect.isLessThan sinceStart.Elapsed deadline "the process stopped before the deadline"
            // The deadline passes while the process is stopped.
            let remaining = deadline + TimeSpan.FromMilliseconds 500.0 - sinceStart.Elapsed
            if remaining > TimeSpan.Zero then Thread.Sleep remaining
            gatedReplies <- true
            let second = boot db "WorkflowLateCommand" []
            try
                let flow = Workflows.register second (definition (gated second))
                Expect.equal (sync (flow.Result "k1" wait)) (WorkflowOutcome.Finished "opened") "the command was sent again and answered"
            finally
                stop second
        finally
            deleteDb db

let private cancelParkedTest =
    testCase "workflow: a parked instance that was already cancelling can be cancelled" <| fun _ ->
        let db = newDb ()
        let outside = Outside()
        let definition (cleanup: string) =
            Workflow.define "undoing" (fun (_: unit) -> workflow {
                try
                    do! sleep (TimeSpan.FromHours 1.0)
                    return "slept"
                with :? WorkflowStepFailedException as failed when failed.Error.Kind = ErrorKind.Cancelled ->
                    let! (_: int) = activity cleanup (Retry.forever (TimeSpan.FromMinutes 5.0)) (fun context -> task {
                        outside.Count cleanup |> ignore
                        do! Task.Run(fun () -> outside.Gate.Wait context.Cancellation)
                        return 1 })
                    return raise failed
            })
        let parked status = match status with WorkflowStatus.Parked _ -> true | _ -> false
        try
            let first = boot db "WorkflowCancelParked" []
            let flow = Workflows.register first (definition "release")
            sync (flow.Start "c1" ()) |> ignore
            eventually "the workflow is sleeping" (fun () -> sync (flow.Status "c1") = WorkflowStatus.Running [ "0 sleep" ])
            sync (flow.Cancel "c1")
            eventually "the cleanup step is running" (fun () -> outside.Calls.ContainsKey "release")
            stop first

            // The cleanup step was renamed while the instance was in it.
            let renamed = boot db "WorkflowCancelParked" []
            try
                let flow = Workflows.register renamed (definition "renamed")
                eventually "the instance is parked" (fun () -> parked (sync (flow.Status "c1")))
                sync (flow.Cancel "c1")
                eventually "the parked instance ended as cancelled" (fun () -> sync (flow.Status "c1") = WorkflowStatus.Cancelled)
            finally
                stop renamed
        finally
            deleteDb db

let private childResultTest =
    testCase "workflow: a child result the parent cannot read fails the wait once" <| fun _ ->
        withSystem "WorkflowChildResult" <| fun api ->
            let doubling = Workflow.define "doubling" (fun (value: int) -> workflow { return value * 2 })
            Workflows.register api doubling |> ignore
            let parent =
                Workflows.register api (Workflow.define "misreader" (fun (value: int) -> workflow {
                    let! handle = startChild doubling value
                    // The handle names a result type the child does not give.
                    let mistyped: ChildHandle<string list> = { Workflow = handle.Workflow; Id = handle.Id }
                    try
                        let! (_: string list) = awaitChild mistyped
                        return "read"
                    with :? WorkflowStepFailedException as failed ->
                        return string failed.Error.Kind
                }))
            sync (parent.Start "r1" 21) |> ignore
            let outcome =
                try
                    Some(sync (parent.Result "r1" (TimeSpan.FromSeconds 8.0)))
                with :? TimeoutException ->
                    None
            Expect.equal outcome (Some(WorkflowOutcome.Finished "NotRetryable")) "the wait fails with a failure the body can handle"

/// The serializer writes an `Outline` and cannot read one back: an interface names no type to create.
type Outline =
    abstract Sides: int

type Triangle() =
    interface Outline with
        member _.Sides = 3

type Drawing = { Outline: Outline }

let private unreadableResultTest =
    testCase "workflow: an activity result that cannot be read back fails the step and does not park the instance" <| fun _ ->
        withSystem "WorkflowUnreadableResult" <| fun api ->
            let flow =
                Workflows.register api (Workflow.define "drawer" (fun (_: unit) -> workflow {
                    try
                        let! (_: Drawing) = activity "draw" Retry.none (fun _ -> task { return { Outline = Triangle() } })
                        return "drawn"
                    with :? WorkflowStepFailedException as failed ->
                        return string failed.Error.Kind
                }))
            sync (flow.Start "d1" ()) |> ignore
            eventually "the instance stopped running" (fun () -> match sync (flow.Status "d1") with WorkflowStatus.Running _ -> false | _ -> true)
            Expect.equal (sync (flow.Status "d1")) WorkflowStatus.Completed "the instance is not parked"
            Expect.equal (sync (flow.Result "d1" wait)) (WorkflowOutcome.Finished "NotRetryable") "the step failed with a failure the body can handle"

let private idleTest =
    testCase "workflow: an ended instance that a request recovered answers a burst from memory, and leaves memory when idle" <| fun _ ->
        let recoveries = ref 0
        use listener = new Metrics.MeterListener()
        listener.InstrumentPublished <- fun instrument listener ->
            if instrument.Meter.Name = WorkflowTelemetry.MeterName && instrument.Name = "fcqrs.workflow.recovery.records" then
                listener.EnableMeasurementEvents instrument
        listener.SetMeasurementEventCallback<int64>(
            Metrics.MeasurementCallback<int64>(fun _ _ tags _ ->
                let mutable ours = false
                for tag in tags do
                    if tag.Key = "fcqrs.workflow.name" && string tag.Value = "idler" then ours <- true
                if ours then Interlocked.Increment(&recoveries.contents) |> ignore))
        listener.Start()
        withSettings "WorkflowIdle" [ idleTimeKey, "1s" ] <| fun api ->
            let flow =
                Workflows.register api (Workflow.define "idler" (fun (value: int) -> workflow {
                    let! (_: DateTime) = now
                    return value
                }))
            sync (flow.Start "i1" 1) |> ignore
            Expect.equal (sync (flow.Result "i1" wait)) (WorkflowOutcome.Finished 1) "the workflow ended"
            // An instance leaves memory when it ends. Give that time to finish.
            Thread.Sleep 300
            Expect.equal (sync (flow.Status "i1")) WorkflowStatus.Completed "an ended instance reports its status"
            let recovered = recoveries.Value
            for _ in 1 .. 5 do
                Expect.equal (sync (flow.Status "i1")) WorkflowStatus.Completed "and reports it again"
            Expect.equal recoveries.Value recovered "the requests that followed did not replay the journal"
            // Longer than the idle time.
            Thread.Sleep 2500
            Expect.equal (sync (flow.Status "i1")) WorkflowStatus.Completed "an instance that left memory still answers"
            Expect.equal recoveries.Value (recovered + 1) "the idle instance had left memory, and the request recovered it"

let private manifestTest =
    testCase "workflow: each record is journaled under the name its stored rows carry" <| fun _ ->
        use system = Akka.Actor.ActorSystem.Create "workflow-manifests"
        let serializer = FCQRS.ActorSerialization.STJSerializer(system :?> Akka.Actor.ExtendedActorSystem)
        let at = DateTime(2026, 11, 11, 10, 30, 0, DateTimeKind.Utc)
        let payload = Payload.ofValue 1
        let error = { Kind = ErrorKind.Exhausted; ExceptionType = "System.TimeoutException"; Message = "slow" }
        let records =
            [ "Started", WorkflowRecord.Started(payload, at, None, None)
              "StepScheduled", WorkflowRecord.StepScheduled("0", "score", StepKind.Activity, at, None)
              "StepCompleted", WorkflowRecord.StepCompleted("0", "score", StepKind.Activity, payload, at)
              "StepFailed", WorkflowRecord.StepFailed("1", "ask", StepKind.Activity, error, at)
              "SignalReceived", WorkflowRecord.SignalReceived("answer", payload, at, None)
              "CancelRequested", WorkflowRecord.CancelRequested at
              "Finished", WorkflowRecord.Finished(payload, at)
              "Faulted", WorkflowRecord.Faulted(error, at)
              "Cancelled", WorkflowRecord.Cancelled at ]
        for case, record in records do
            let manifest = serializer.Manifest record
            // Journals written since 7.0.0 hold these names. Renaming or moving the type breaks them.
            Expect.equal manifest $"FCQRS.Workflows.WorkflowRecord+{case}, FCQRS.Workflows" $"the name of a {case} row"
            Expect.equal (serializer.FromBinary(serializer.ToBinary record, manifest)) (box record) $"a {case} row reads back"

let tests =
    testSequenced (
        testList "workflow"
            [ startTest; faultTest; retryTest; shortTimerTest; restartTest; timerTest; tracingTest; startRaceTest; concurrentStartTest
              signalTest; signalRestartTest; commandTest; parallelCommandTest; commandRestartTest; commandRecoveryTest; unknownOutcomeTest
              cancelTest; childTest; childRestartTest; parkingTest; streamTest; metricsTest
              lateCommandTest; cancelParkedTest; childResultTest; unreadableResultTest; idleTest; manifestTest ]
    )
