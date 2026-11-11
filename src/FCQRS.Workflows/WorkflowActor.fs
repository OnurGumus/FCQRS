namespace FCQRS.Workflows

open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Akka.Cluster.Sharding
open Akkling
open Akkling.Persistence
open Microsoft.Extensions.Logging
open FCQRS.Common
open FCQRS.AkklingHelpers
open FCQRS.Workflows.Protocol

module internal WorkflowActor =

    /// A workflow with its types erased, as the actor runs it.
    type Definition =
        { Name: string
          EncodeResult: objnull -> Payload
          Body: Payload -> Workflow<objnull> }

    /// An activity step that is running in this process.
    type private RunningStep =
        { Cancellation: CancellationTokenSource
          Attempt: int
          Policy: RetryPolicy
          Span: Activity | null }

    let private source = new ActivitySource(Telemetry.WorkflowActivitySourceName)

    let private meter = new Metrics.Meter(WorkflowTelemetry.MeterName)
    let private startedCount = meter.CreateCounter<int64> "fcqrs.workflow.started"
    let private finishedCount = meter.CreateCounter<int64> "fcqrs.workflow.finished"
    let private faultedCount = meter.CreateCounter<int64> "fcqrs.workflow.faulted"
    let private cancelledCount = meter.CreateCounter<int64> "fcqrs.workflow.cancelled"
    let private runningCount = meter.CreateUpDownCounter<int64> "fcqrs.workflow.running"
    let private parkedCount = meter.CreateUpDownCounter<int64> "fcqrs.workflow.parked"
    let private stepDuration = meter.CreateHistogram<float>("fcqrs.workflow.step.duration", "ms")
    let private retryCount = meter.CreateCounter<int64> "fcqrs.workflow.activity.retries"
    let private recoveryDuration = meter.CreateHistogram<float>("fcqrs.workflow.recovery.duration", "ms")
    let private recoveryRecords = meter.CreateHistogram<int64> "fcqrs.workflow.recovery.records"

    /// No single timer runs longer than this. A timer that fires early is armed again.
    let private longestTimer = TimeSpan.FromHours 12.0

    let private retryDelay (schedule: RetrySchedule) (attempt: int) : TimeSpan =
        match schedule with
        | FixedInterval interval -> interval
        | Backoff(initial, factor, max) ->
            let grown = initial.TotalMilliseconds * Math.Pow(factor, float (attempt - 1))
            let capped = Math.Min(grown, max.TotalMilliseconds)
            // A zero interval grown without bound is not a number; fall back to the cap.
            let capped = if Double.IsNaN capped then max.TotalMilliseconds else capped
            // Stretch only, as saga expectations do, so steps released together do not retry together.
            TimeSpan.FromMilliseconds(Math.Ceiling(capped * (1.0 + Random.Shared.NextDouble() * 0.25)))

    /// `at + span`, or the latest representable time when that overflows.
    let private later (at: DateTime) (span: TimeSpan) : DateTime =
        if span >= DateTime.MaxValue - at then DateTime.MaxValue else at + span

    let behavior (definition: Definition) (api: IActor) (mailbox: Eventsourced<obj>) =
        let log = api.LoggerFactory.CreateLogger "FCQRS.Workflows"
        let id = Uri.UnescapeDataString mailbox.Self.Path.Name
        let self = mailbox.UntypedContext.Self
        let scheduler = mailbox.System.Scheduler
        let clock () = scheduler.Now.UtcDateTime

        // A supervisor restart keeps the mailbox. Messages this incarnation did not send to itself
        // are ignored, so an attempt outcome or timer of the previous incarnation changes nothing.
        let epoch = Random.Shared.NextInt64()
        let mutable passivationRequested = false
        let created = Stopwatch.StartNew()
        let mutable recovered = 0L
        let nameTag = KeyValuePair<string, objnull>("fcqrs.workflow.name", box definition.Name)
        // What this instance has added to the running and parked counts, taken back when it stops.
        let mutable countedRunning = false
        let mutable countedParked = false

        let countRunning (running: bool) =
            if running <> countedRunning then
                countedRunning <- running
                runningCount.Add((if running then 1L else -1L), nameTag)

        let countParked (isParked: bool) =
            if isParked <> countedParked then
                countedParked <- isParked
                parkedCount.Add((if isParked then 1L else -1L), nameTag)

        let history = History()
        let running = Dictionary<string, RunningStep>()
        let timers = Dictionary<string, ResizeArray<ICancelable>>()
        let waiters = ResizeArray<IActorRef>()
        let mutable started: (Payload * string option) option = None
        let mutable final: WorkflowRecord option = None
        let mutable parked: string option = None
        let mutable machine: Machine option = None
        let mutable startSender: IActorRef option = None
        /// The senders of signals being stored, in the order their records will be applied.
        let signalSenders = Queue<IActorRef>()
        let cancelSenders = Queue<IActorRef>()

        let traceParent () : ActivityContext voption =
            match started with
            | Some(_, Some traceparent) ->
                let mutable context = Unchecked.defaultof<ActivityContext>

                if ActivityContext.TryParse(traceparent, null, &context) then
                    ValueSome context
                else
                    ValueNone
            | _ -> ValueNone

        let tag (span: Activity | null) (path: string option) (name: string option) =
            match span with
            | null -> ()
            | span ->
                span.SetTag("fcqrs.workflow.name", definition.Name) |> ignore
                span.SetTag("fcqrs.workflow.id", id) |> ignore
                path |> Option.iter (fun path -> span.SetTag("fcqrs.workflow.step.path", path) |> ignore)
                name |> Option.iter (fun name -> span.SetTag("fcqrs.workflow.step.name", name) |> ignore)

        let startSpan (name: string) (parent: ActivityContext voption) : Activity | null =
            match parent with
            | ValueSome parent -> source.StartActivity(name, ActivityKind.Internal, parent)
            | ValueNone -> source.StartActivity(name, ActivityKind.Internal)

        /// A span that marks a point in the workflow: it starts and ends at once.
        let mark (name: string) (path: string option) (step: string option) (waited: TimeSpan option) (error: string option) =
            // The actor has no ambient span of its own; do not leave one behind for the next message.
            let previous = Activity.Current

            match startSpan name (traceParent ()) with
            | null -> ()
            | span ->
                tag span path step
                waited |> Option.iter (fun waited -> span.SetTag("fcqrs.workflow.waited_ms", int64 waited.TotalMilliseconds) |> ignore)
                error |> Option.iter (fun error -> span.SetStatus(ActivityStatusCode.Error, error) |> ignore)
                span.Dispose()

            Activity.Current <- previous

        let status () : WorkflowStatus =
            match final, parked, started, machine with
            | Some(WorkflowRecord.Finished _), _, _, _ -> WorkflowStatus.Completed
            | Some(WorkflowRecord.Faulted(error, _)), _, _, _ -> WorkflowStatus.Failed error
            | Some _, _, _, _ -> WorkflowStatus.Cancelled
            | None, Some reason, _, _ -> WorkflowStatus.Parked reason
            | None, None, None, _ -> WorkflowStatus.NotStarted
            | None, None, Some _, Some machine ->
                WorkflowStatus.Running [ for KeyValue(path, step) in machine.Pending -> $"{path} {step.Leaf.Name}" ]
            | None, None, Some _, None -> WorkflowStatus.Running []

        let resultReply () : ResultReply option =
            match final, started with
            | Some(WorkflowRecord.Finished(result, _)), _ -> Some(ResultFinished result)
            | Some(WorkflowRecord.Faulted(error, _)), _ -> Some(ResultFaulted error)
            | Some _, _ -> Some ResultCancelled
            | None, None -> Some ResultNotStarted
            | None, Some _ -> None

        let schedule (path: string) (delay: TimeSpan) (message: obj) =
            let delay = if delay < TimeSpan.Zero then TimeSpan.Zero else delay

            let ofStep =
                match timers.TryGetValue path with
                | true, ofStep -> ofStep
                | _ ->
                    let ofStep = ResizeArray<ICancelable>()
                    timers[path] <- ofStep
                    ofStep

            ofStep.Add(scheduler.ScheduleTellOnceCancelable(delay, self, message, self))

        /// Cancels the timers of a step that is over.
        let forget (path: string) =
            match timers.TryGetValue path with
            | true, ofStep ->
                timers.Remove path |> ignore

                for timer in ofStep do
                    timer.Cancel()
            | _ -> ()

        let release (path: string) (error: string option) =
            forget path
            // Ending a span makes its parent current; the actor keeps no ambient span.
            let previous = Activity.Current

            match running.TryGetValue path with
            | true, step ->
                running.Remove path |> ignore
                step.Cancellation.Cancel()
                step.Cancellation.Dispose()

                match step.Span with
                | null -> ()
                | span ->
                    error |> Option.iter (fun error -> span.SetStatus(ActivityStatusCode.Error, error) |> ignore)
                    span.Dispose()
            | _ -> ()

            Activity.Current <- previous

        let cleanup () =
            for path in List.ofSeq running.Keys do
                release path None

            for path in List.ofSeq timers.Keys do
                forget path

        /// Idle instances leave memory: one that was never started, and one that has ended.
        let passivate () =
            passivationRequested <- true
            mailbox.UntypedContext.Parent.Tell(Passivate(StopEntity), self)

        let startAttempt (step: PendingStep) (policy: RetryPolicy) (run: AttemptEnv -> Task<objnull>) (attempt: int) =
            let path = step.Path
            let previous = Activity.Current

            let cancellation, stepSpan =
                match running.TryGetValue path with
                | true, current ->
                    running[path] <- { current with Attempt = attempt }
                    current.Cancellation, current.Span
                | _ ->
                    let span = startSpan $"Step:{step.Leaf.Name}" (traceParent ())
                    tag span (Some path) (Some step.Leaf.Name)
                    let cancellation = new CancellationTokenSource()

                    running[path] <-
                        { Cancellation = cancellation
                          Attempt = attempt
                          Policy = policy
                          Span = span }

                    cancellation, span

            Activity.Current <- previous

            let parent =
                match stepSpan with
                | null -> traceParent ()
                | span -> ValueSome span.Context

            // One token per attempt, so a timed-out attempt is cancelled without ending the step.
            let attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource cancellation.Token

            let context: AttemptContext =
                { IdempotencyKey = $"{definition.Name}/{id}/{path}"
                  Attempt = attempt
                  Cancellation = attemptCancellation.Token }

            let work () : Task =
                task {
                    let! outcome =
                        task {
                            use span = startSpan $"Attempt:{step.Leaf.Name}" parent
                            tag span (Some path) (Some step.Leaf.Name)

                            match span with
                            | null -> ()
                            | span -> span.SetTag("fcqrs.workflow.attempt", attempt) |> ignore

                            try
                                let call =
                                    run
                                        { Api = api
                                          Context = context
                                          WorkflowName = definition.Name
                                          WorkflowId = id
                                          Path = path }

                                match policy.AttemptTimeout with
                                | Some timeout ->
                                    let! first = Task.WhenAny(call, Task.Delay timeout)

                                    if obj.ReferenceEquals(first, call) then
                                        let! value = call
                                        return Ok value
                                    else
                                        attemptCancellation.Cancel()
                                        return Error(TimeoutException $"The attempt did not finish within {timeout}." :> exn)
                                | None ->
                                    let! value = call
                                    return Ok value
                            with error ->
                                match span with
                                | null -> ()
                                | span -> span.SetStatus(ActivityStatusCode.Error, error.Message) |> ignore

                                return Error error
                        }

                    attemptCancellation.Dispose()

                    self.Tell(
                        { AttemptPath = path
                          Attempt = attempt
                          AttemptEpoch = epoch
                          Outcome = outcome },
                        ActorRefs.NoSender
                    )
                }

            Task.Run(work) |> ignore

        let stepFailed (step: PendingStep) (kind: ErrorKind) (error: exn) : WorkflowRecord =
            // A command that got no reply may still have been applied.
            let kind =
                match step.Leaf.Kind, kind, error with
                | _, _, (:? ChildEndedException) -> ErrorKind.ChildFaulted
                | StepKind.Command, (ErrorKind.Exhausted | ErrorKind.DeadlineExceeded), _ -> ErrorKind.OutcomeUnknown
                | _ -> kind

            WorkflowRecord.StepFailed(step.Path, step.Leaf.Name, step.Leaf.Kind, Payload.errorOf kind error, clock ())

        let dueOf (step: PendingStep) : DateTime option =
            step.Detail |> Option.map (fun detail -> DateTime.Parse(detail, null, Globalization.DateTimeStyles.RoundtripKind))

        let armTimer (step: PendingStep) (due: DateTime) =
            let remaining = due - clock ()
            schedule step.Path (if remaining > longestTimer then longestTimer else remaining) { TimerPath = step.Path; TimerEpoch = epoch }

        /// Starts a step, or resumes one that was in progress when the process stopped. Returns the
        /// records to store when the step is already over.
        let beginStep (step: PendingStep) : WorkflowRecord list =
            let scheduledAt = step.ScheduledAt |> Option.defaultWith clock

            match step.Leaf.Op with
            | ActivityOp(policy, run) ->
                match policy.Deadline with
                | Some deadline when later scheduledAt deadline <= clock () ->
                    [ stepFailed step ErrorKind.DeadlineExceeded (TimeoutException $"The step's deadline of {deadline} passed.") ]
                | deadline ->
                    deadline
                    |> Option.iter (fun deadline ->
                        let remaining = later scheduledAt deadline - clock ()
                        // A deadline further away than the longest timer is checked again when that fires.
                        schedule step.Path (if remaining > longestTimer then longestTimer else remaining) { DeadlinePath = step.Path; DeadlineEpoch = epoch })

                    startAttempt step policy run 1
                    []
            | TimerOp _ ->
                armTimer step (dueOf step |> Option.defaultValue scheduledAt)
                []
            | SignalOp _ ->
                // Only a wait with a timeout has a due time.
                dueOf step |> Option.iter (armTimer step)
                []
            | ValueOp _ -> []

        let perform (emits: Emit list) : WorkflowRecord list =
            [ for emit in emits do
                  match emit with
                  | Store record -> record
                  | Begin step -> yield! beginStep step
                  | Abandon path -> release path None ]

        /// Runs a machine call. A journal that no longer matches the body parks the instance.
        let guarded (call: unit -> Emit list) : WorkflowRecord list =
            try
                perform (call ())
            with error ->
                // A step at the wrong path, or a stored value the code can no longer read. An
                // exception here would otherwise restart the actor into the same failure forever.
                parked <- Some error.Message
                countRunning false
                countParked true
                // The body ran against a journal it does not match: nothing of it may continue.
                machine <- None
                cleanup ()
                log.LogError(error, "Workflow {Workflow} {Id} is parked: its journal does not match its code.", definition.Name, id)
                []

        let runBody (input: Payload) : WorkflowRecord list =
            let created = Machine(history, definition.EncodeResult, clock)
            machine <- Some created
            guarded (fun () -> created.Run(WorkflowCore.protect (fun () -> definition.Body input)))

        let applyLive (record: WorkflowRecord) : WorkflowRecord list =
            match record with
            | WorkflowRecord.Started(input, _, traceparent, _) ->
                started <- Some(input, traceparent)
                startedCount.Add(1L, nameTag)
                countRunning true
                mark $"Workflow.Start:{definition.Name}" None None None None

                startSender
                |> Option.iter (fun sender -> sender.Tell({ Created = true; Status = WorkflowStatus.Running [] }, self))

                startSender <- None
                runBody input
            | WorkflowRecord.StepScheduled _ ->
                match machine with
                | Some machine -> guarded (fun () -> machine.Apply record)
                | None -> []
            | WorkflowRecord.StepCompleted(path, name, kind, _, at)
            | WorkflowRecord.StepFailed(path, name, kind, _, at) ->
                let error =
                    match record with
                    | WorkflowRecord.StepFailed(_, _, _, error, _) -> Some $"{error.Kind}: {error.Message}"
                    | _ -> None

                let waited =
                    match history.Steps.TryGetValue path with
                    | true, Scheduled(_, _, scheduledAt, _) -> Some(at - scheduledAt)
                    | _ -> None

                waited
                |> Option.iter (fun waited ->
                    stepDuration.Record(waited.TotalMilliseconds, nameTag, KeyValuePair<string, objnull>("fcqrs.workflow.step.kind", box (string kind))))

                if kind = StepKind.Activity || kind = StepKind.Command then
                    release path error
                else
                    forget path
                    mark $"Step:{name}" (Some path) (Some name) waited error

                match machine with
                | Some machine -> guarded (fun () -> machine.Apply record)
                | None -> []
            | WorkflowRecord.Finished _
            | WorkflowRecord.Faulted _
            | WorkflowRecord.Cancelled _ ->
                final <- Some record
                cleanup ()
                countRunning false
                countParked false

                match record with
                | WorkflowRecord.Finished _ -> finishedCount.Add(1L, nameTag)
                | WorkflowRecord.Faulted _ -> faultedCount.Add(1L, nameTag)
                | _ -> cancelledCount.Add(1L, nameTag)

                let error =
                    match record with
                    | WorkflowRecord.Faulted(error, _) -> Some $"{error.Kind}: {error.Message}"
                    | _ -> None

                mark $"Workflow.End:{definition.Name}" None None None error

                match resultReply () with
                | Some reply ->
                    for waiter in waiters do
                        waiter.Tell(reply, self)
                | None -> ()

                waiters.Clear()
                passivate ()
                []
            | WorkflowRecord.SignalReceived _ ->
                if signalSenders.Count > 0 then
                    signalSenders.Dequeue().Tell(SignalAccepted, self)

                match machine with
                | Some machine -> guarded (fun () -> machine.Apply record)
                // A parked instance keeps the signal for the code that will match its journal.
                | None ->
                    history.Apply record
                    []
            | WorkflowRecord.CancelRequested _ ->
                if cancelSenders.Count > 0 then
                    cancelSenders.Dequeue().Tell(CancelAccepted, self)

                match machine with
                | Some machine -> guarded (fun () -> machine.Apply record)
                | None ->
                    history.Apply record
                    []

        let applyRecovered (record: WorkflowRecord) =
            recovered <- recovered + 1L
            history.Apply record

            match record with
            | WorkflowRecord.Started(input, _, traceparent, _) -> started <- Some(input, traceparent)
            | WorkflowRecord.Finished _
            | WorkflowRecord.Faulted _
            | WorkflowRecord.Cancelled _ -> final <- Some record
            | _ -> ()

        let attemptDone (message: AttemptDone) : WorkflowRecord list =
            match machine, running.TryGetValue message.AttemptPath with
            | Some machine, (true, current) when current.Attempt = message.Attempt && message.AttemptEpoch = epoch ->
                match machine.Pending.TryGetValue message.AttemptPath, current.Policy with
                | (true, step), policy ->
                    match message.Outcome with
                    | Ok value ->
                        try
                            let result = step.Leaf.Encode value
                            [ WorkflowRecord.StepCompleted(step.Path, step.Leaf.Name, step.Leaf.Kind, result, clock ()) ]
                        with error ->
                            // A result that cannot be stored cannot be replayed either.
                            [ stepFailed step ErrorKind.NotRetryable error ]
                    | Error error ->
                        let retryable =
                            try
                                policy.ShouldRetry error
                            with _ ->
                                false

                        let deadlinePassed =
                            match policy.Deadline, step.ScheduledAt with
                            | Some deadline, Some scheduledAt -> later scheduledAt deadline <= clock ()
                            | _ -> false

                        if not retryable then
                            [ stepFailed step ErrorKind.NotRetryable error ]
                        elif policy.MaxAttempts |> Option.exists (fun most -> message.Attempt >= most) then
                            [ stepFailed step ErrorKind.Exhausted error ]
                        elif deadlinePassed then
                            [ stepFailed step ErrorKind.DeadlineExceeded error ]
                        else
                            log.LogWarning(
                                error,
                                "Workflow {Workflow} {Id}: attempt {Attempt} of step {Step} failed and will be retried.",
                                definition.Name,
                                id,
                                message.Attempt,
                                step.Leaf.Name
                            )

                            retryCount.Add(1L, nameTag)

                            schedule
                                step.Path
                                (retryDelay policy.Schedule message.Attempt)
                                { RetryPath = step.Path
                                  NextAttempt = message.Attempt + 1
                                  RetryEpoch = epoch }

                            []
                | (false, _), _ -> []
            // The step ended before this outcome arrived, or a newer attempt replaced it.
            | _ -> []

        let store (records: WorkflowRecord list) (next: Effect<obj>) : Effect<obj> =
            match records with
            | [] -> next
            | records -> PersistAll(records |> Seq.map box) <@> next

        let rec loop () =
            actor {
                let! (message: obj) = mailbox.Receive()

                match message with
                | Recovering mailbox (:? WorkflowRecord as record) ->
                    applyRecovered record
                    return! loop ()
                | Persisted mailbox (:? WorkflowRecord as record) -> return! store (applyLive record) (loop ())
                | :? Akka.Persistence.RecoveryCompleted ->
                    if recovered > 0L then
                        recoveryDuration.Record(created.Elapsed.TotalMilliseconds, nameTag)
                        recoveryRecords.Record(recovered, nameTag)

                    match started, final with
                    | Some(input, _), None ->
                        countRunning true
                        return! store (runBody input) (loop ())
                    | Some _, Some _ ->
                        // Remembered because the process stopped before its passivation finished.
                        passivate ()
                        return! loop ()
                    | None, _ -> return! loop ()
                | :? StartWorkflow as start ->
                    match started with
                    | Some _ ->
                        mailbox.UntypedContext.Sender.Tell({ Created = false; Status = status () }, self)

                        if final.IsSome then
                            passivate ()

                        return! loop ()
                    | None ->
                        startSender <- Some mailbox.UntypedContext.Sender

                        return!
                            store [ WorkflowRecord.Started(start.Input, clock (), start.Traceparent, start.Parent) ] (loop ())
                | :? GetStatus ->
                    mailbox.UntypedContext.Sender.Tell(status (), self)

                    if started.IsNone || final.IsSome then
                        passivate ()

                    return! loop ()
                | :? AwaitResult ->
                    match resultReply () with
                    | Some reply ->
                        mailbox.UntypedContext.Sender.Tell(reply, self)
                        passivate ()
                    | None -> waiters.Add mailbox.UntypedContext.Sender

                    return! loop ()
                | :? SendSignal as signal ->
                    match started, final with
                    | Some _, None ->
                        signalSenders.Enqueue mailbox.UntypedContext.Sender

                        return!
                            store
                                [ WorkflowRecord.SignalReceived(signal.SignalName, signal.SignalPayload, clock (), signal.SignalTraceparent) ]
                                (loop ())
                    | _ ->
                        mailbox.UntypedContext.Sender.Tell(SignalRefused(status ()), self)
                        passivate ()
                        return! loop ()
                | :? CancelWorkflow ->
                    match started, final with
                    | None, _ ->
                        mailbox.UntypedContext.Sender.Tell(CancelRefused(status ()), self)
                        passivate ()
                        return! loop ()
                    | Some _, Some _ ->
                        // Already over: there is nothing to cancel.
                        mailbox.UntypedContext.Sender.Tell(CancelAccepted, self)
                        passivate ()
                        return! loop ()
                    | Some _, None ->
                        cancelSenders.Enqueue mailbox.UntypedContext.Sender
                        let time = clock ()

                        let cancelled: StepError =
                            { Kind = ErrorKind.Cancelled
                              ExceptionType = typeof<OperationCanceledException>.FullName |> Unchecked.nonNull
                              Message = "The workflow was cancelled." }

                        // The request and the failure of every pending step are one write, so a
                        // restart never finds a request whose steps are still pending.
                        let consequences =
                            match machine with
                            | Some machine ->
                                [ for KeyValue(path, step) in machine.Pending do
                                      if step.ScheduledAt.IsSome then
                                          step.Leaf.OnCancel
                                          |> Option.iter (fun onCancel ->
                                              try
                                                  onCancel api
                                              with error ->
                                                  log.LogWarning(error, "Workflow {Workflow} {Id}: cancelling step {Step} failed.", definition.Name, id, step.Leaf.Name))

                                          WorkflowRecord.StepFailed(path, step.Leaf.Name, step.Leaf.Kind, cancelled, time) ]
                            // A parked instance cannot run its cleanup: it ends.
                            | None -> [ WorkflowRecord.Cancelled time ]

                        return! store (WorkflowRecord.CancelRequested time :: consequences) (loop ())
                | :? AttemptDone as outcome -> return! store (attemptDone outcome) (loop ())
                | :? RetryDue as retry ->
                    match machine, running.TryGetValue retry.RetryPath with
                    | Some machine, (true, current) when current.Attempt = retry.NextAttempt - 1 && retry.RetryEpoch = epoch ->
                        match machine.Pending.TryGetValue retry.RetryPath with
                        | true, step ->
                            match step.Leaf.Op with
                            | ActivityOp(policy, run) -> startAttempt step policy run retry.NextAttempt
                            | _ -> ()
                        | _ -> ()
                    | _ -> ()

                    return! loop ()
                | :? DeadlinePassed as deadline when deadline.DeadlineEpoch = epoch ->
                    match machine |> Option.bind (fun machine -> Option.ofObj (machine.Pending.GetValueOrDefault deadline.DeadlinePath |> box)) with
                    | Some(:? PendingStep as step) ->
                        match step.Leaf.Op, step.ScheduledAt with
                        | ActivityOp({ Deadline = Some limit }, _), Some scheduledAt when later scheduledAt limit > clock () ->
                            // The timer was capped; arm it for the time that remains.
                            let remaining = later scheduledAt limit - clock ()
                            schedule step.Path (if remaining > longestTimer then longestTimer else remaining) { DeadlinePath = step.Path; DeadlineEpoch = epoch }
                            return! loop ()
                        | _ ->
                            let message = "The step's deadline passed."
                            return! store [ stepFailed step ErrorKind.DeadlineExceeded (TimeoutException message) ] (loop ())
                    | _ -> return! loop ()
                | :? TimerFired as timer when timer.TimerEpoch = epoch ->
                    match machine |> Option.bind (fun machine -> Option.ofObj (machine.Pending.GetValueOrDefault timer.TimerPath |> box)) with
                    | Some(:? PendingStep as step) ->
                        match beginTimerOrComplete step with
                        | Some record -> return! store [ record ] (loop ())
                        | None -> return! loop ()
                    | _ -> return! loop ()
                | LifecycleEvent PostStop ->
                    cleanup ()
                    countRunning false
                    countParked false
                    return! loop ()
                | PersistentLifecycleEvent(PersistRejected(error, _, sequenceNr)) ->
                    // The sequence number is used up, so the next record would leave a gap that stops
                    // transactional projections. Crash, as aggregates and sagas do.
                    log.LogError(error, "The journal rejected workflow {Workflow} {Id}'s record at sequence {SequenceNr}. Terminating the process.", definition.Name, id, sequenceNr)
                    fatalFailFast null "Process terminated because the journal rejected a workflow record" error
                    return! loop ()
                | PersistentLifecycleEvent(PersistFailed(error, _, sequenceNr)) ->
                    log.LogError(error, "Workflow {Workflow} {Id} could not store a record at sequence {SequenceNr}; the instance stops and is restarted.", definition.Name, id, sequenceNr)
                    return! loop ()
                | PersistentLifecycleEvent(ReplayFailed(error, _)) ->
                    log.LogError(error, "Workflow {Workflow} {Id} could not recover from the journal; the instance stops.", definition.Name, id)
                    return! loop ()
                | :? StopEntity ->
                    if passivationRequested && started.IsSome && final.IsNone then
                        // This instance asked to leave memory while it was idle, and was started
                        // before the stop arrived. A stop forgets a remembered instance, so ask the
                        // region for it again: it recovers and carries on.
                        try
                            let region = ClusterSharding.Get(mailbox.System).ShardRegion definition.Name

                            let wake: Akkling.Cluster.Sharding.ShardEnvelope =
                                { ShardId = DEFAULT_SHARD
                                  EntityId = id
                                  Message = Wake }

                            region.Tell(wake, ActorRefs.NoSender)
                        with error ->
                            log.LogWarning(error, "Workflow {Workflow} {Id} could not ask to be recovered after its stop.", definition.Name, id)

                    return! Stop
                | _ -> return! loop ()
            }

        /// A fired timer completes its step, unless it fired early: then it is armed again.
        and beginTimerOrComplete (step: PendingStep) : WorkflowRecord option =
            let due =
                match step.Detail with
                | Some detail -> DateTime.Parse(detail, null, Globalization.DateTimeStyles.RoundtripKind)
                | None -> clock ()

            let remaining = due - clock ()

            if remaining > TimeSpan.FromMilliseconds 1.0 then
                schedule step.Path (if remaining > longestTimer then longestTimer else remaining) { TimerPath = step.Path; TimerEpoch = epoch }
                None
            else
                let result = if step.Leaf.Kind = StepKind.Signal then Payload.timedOut else Payload.ofValue ()
                Some(WorkflowRecord.StepCompleted(step.Path, step.Leaf.Name, step.Leaf.Kind, result, clock ()))

        loop ()
