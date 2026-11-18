namespace FCQRS.Workflows

open System
open System.Collections.Generic
open System.Globalization
open FCQRS.Workflows.CSharp

/// A step that a workflow under test waits on.
type WaitingStep =
    { /// The step's position in the body, as the journal stores it.
      Path: string
      /// The step's name.
      Name: string
      /// What the step does.
      Kind: StepKind
      /// When a timer is due, or when a signal wait times out. Null for other steps.
      Due: Nullable<DateTime> }

/// Raised by `WorkflowTest.CheckReplay` when a restart of the instance stores a journal that differs
/// from the one the test produced, or parks it.
type WorkflowReplayException(message: string, recordsBeforeRestart: int) =
    inherit Exception(message)
    /// How many stored records the restart that differed replayed.
    member _.RecordsBeforeRestart = recordsBeforeRestart

/// One run of a workflow instance in memory: the journal it stores, and what the body waits on.
/// It feeds the replay machine the way the workflow actor does, without an actor system.
type internal TestRun(name: string, id: string, body: Payload -> Workflow<objnull>, encodeResult: objnull -> Payload, live: bool) =
    let journal = ResizeArray<WorkflowRecord>()
    let times = ResizeArray<DateTime>()
    /// The positions of the records the test gave: the start, step outcomes, fired timers,
    /// signals, and cancels. The machine stores the others.
    let inputs = HashSet<int>()
    /// The positions of the records each write of the test stored together.
    let batches = ResizeArray<int list>()
    let mutable history = History()
    let mutable machine: Machine option = None
    let mutable parked: string option = None
    let mutable final: WorkflowRecord option = None
    let mutable input: Payload option = None
    /// The run the journal is in: each `Started` record begins one.
    let mutable run = 0
    /// The order the waiting steps started in, by path. A dictionary's order is not that order.
    let began = Dictionary<string, int64>()
    let mutable beginCount = 0L

    member val Now = DateTime.MinValue with get, set

    member _.Journal = journal
    member _.Times = times
    member _.Inputs = inputs
    member _.Batches = batches
    member _.Body = body
    member _.EncodeResult = encodeResult
    member _.Machine = machine
    member _.Parked = parked
    member _.Final = final
    member _.Input = input

    /// When the step at `path` began, relative to the others.
    member _.BeganAt(path: string) =
        match began.TryGetValue path with
        | true, order -> order
        | _ -> Int64.MaxValue

    member private this.Append(record: WorkflowRecord, given: bool) =
        if given then
            inputs.Add journal.Count |> ignore

        journal.Add record
        times.Add this.Now

        match record with
        | WorkflowRecord.Started(payload, _, _, _) ->
            run <- run + 1
            input <- Some payload
            final <- None
            parked <- None
        | WorkflowRecord.Resumed _ -> final <- None
        | WorkflowRecord.Finished _
        | WorkflowRecord.Faulted _
        | WorkflowRecord.Cancelled _ -> final <- Some record
        | _ -> ()

    /// Runs a machine call. A journal that does not match the body parks the instance, as the
    /// actor does.
    member private _.Guarded(call: unit -> Emit list) : WorkflowRecord list =
        try
            [ for emit in call () do
                  match emit with
                  | Store(WorkflowRecord.StepScheduled(path, stepName, kind, at, None) as record) ->
                      // The start of a child step holds the child's ID.
                      let child =
                          machine
                          |> Option.bind (fun machine ->
                              match machine.Pending.TryGetValue path with
                              | true, step -> step.Leaf.Child
                              | _ -> None)

                      match child with
                      | Some childOf ->
                          let runKey = if run <= 1 then id else $"{id}#{run}"
                          let struct (_, childId) = childOf runKey path
                          WorkflowRecord.StepScheduled(path, stepName, kind, at, Some childId)
                      | None -> record
                  | Store record -> record
                  | Begin step ->
                      beginCount <- beginCount + 1L
                      began[step.Path] <- beginCount
                  | Abandon _ -> () ]
        with error ->
            parked <- Some error.Message
            machine <- None
            []

    member private this.RunBody(payload: Payload) : WorkflowRecord list =
        let created = Machine(history, encodeResult, fun () -> this.Now)
        machine <- Some created
        this.Guarded(fun () -> created.Run(WorkflowCore.protect (fun () -> body payload)))

    member private this.ApplyLive(record: WorkflowRecord) : WorkflowRecord list =
        match record with
        | WorkflowRecord.Started(payload, _, _, _) ->
            // A run after the first replays nothing of the runs before it.
            history.Apply record
            this.RunBody payload
        | WorkflowRecord.Resumed _ ->
            // The step's failure is set aside, and the body replays up to it and runs it again.
            history.Apply record

            match input with
            | Some payload -> this.RunBody payload
            | None -> []
        | _ ->
            match machine with
            | Some machine -> this.Guarded(fun () -> machine.Apply record)
            | None ->
                history.Apply record
                []

    /// Stores the records, then applies each one; the records that applying them stores come
    /// after the whole batch, as the actor's persist order has them.
    member private this.Store(records: WorkflowRecord list, given: bool) =
        if given then
            batches.Add [ journal.Count .. journal.Count + records.Length - 1 ]

        let work = Queue<WorkflowRecord * bool>()

        for record in records do
            work.Enqueue((record, given))

        while work.Count > 0 do
            let batch = [ while work.Count > 0 do yield work.Dequeue() ]

            for record, given in batch do
                this.Append(record, given)

            for record, _ in batch do
                for stored in this.ApplyLive record do
                    work.Enqueue((stored, false))

        if live then
            this.Settle()

    /// Fires the timers and signal timeouts whose due time has passed, as the actor's timers do.
    member this.Settle() =
        let due () =
            match machine with
            | None -> None
            | Some machine ->
                machine.Pending.Values
                |> Seq.choose (fun step ->
                    match step.ScheduledAt, step.Leaf.Kind, step.Detail with
                    | Some _, (StepKind.Timer | StepKind.Signal), Some detail ->
                        let at = DateTime.Parse(detail, null, DateTimeStyles.RoundtripKind)
                        if at <= this.Now then Some(at, step) else None
                    | _ -> None)
                |> Seq.sortBy (fun (at, step) -> at, step.Path)
                |> Seq.tryHead

        let mutable next = due ()

        while next.IsSome do
            let _, step = next.Value
            let result = if step.Leaf.Kind = StepKind.Signal then Payload.timedOut else Payload.ofValue ()
            this.Store([ WorkflowRecord.StepCompleted(step.Path, step.Leaf.Name, step.Leaf.Kind, result, this.Now) ], true)
            next <- due ()

    /// Stores a record the test gives, and continues the body.
    member this.Give(record: WorkflowRecord) = this.Store([ record ], true)

    /// Gives several records in one write, as a cancel stores them.
    member this.GiveAll(records: WorkflowRecord list) = this.Store(records, true)

    /// Loads stored records without running the body, as recovery reads the journal.
    member this.Load(records: (WorkflowRecord * DateTime * bool) seq) =
        for record, at, given in records do
            this.Now <- at
            this.Append(record, given)

    /// Runs the body over the journal, as a recovered instance does. An ended instance runs nothing.
    member this.Recover() =
        history <- History()

        for record in journal do
            history.Apply record

        machine <- None
        parked <- None

        match input, final with
        | Some payload, None ->
            let stored = this.RunBody payload
            this.Store(stored, false)
        | _ -> ()

    member _.Status: WorkflowStatus =
        match final, parked, input, machine with
        | Some(WorkflowRecord.Finished _), _, _, _ -> WorkflowStatus.Completed
        | Some(WorkflowRecord.Faulted(error, _)), _, _, _ -> WorkflowStatus.Failed error
        | Some _, _, _, _ -> WorkflowStatus.Cancelled
        | None, Some reason, _, _ -> WorkflowStatus.Parked reason
        | None, None, None, _ -> WorkflowStatus.NotStarted
        | None, None, Some _, Some machine -> WorkflowStatus.Running [ for KeyValue(path, step) in machine.Pending -> $"{path} {step.Leaf.Name}" ]
        | None, None, Some _, None -> WorkflowStatus.Running []

    member _.Name = name
    member _.Id = id

/// A workflow instance run in memory, for tests. It stores the records the workflow actor would
/// store, without an actor system or a database. Activities, commands, and child workflows do not
/// run: the test gives each step's outcome with `Complete` or `Fail`. Time moves only with
/// `Advance`, which fires timers and signal timeouts. Retry policies are not applied: the outcome a
/// test gives is the step's final one. Start one with `WorkflowTest.Start`.
[<Sealed>]
type WorkflowTest<'Input, 'Result> internal (run: TestRun, given: int) =

    let ended () = run.Final.IsSome

    let describe (record: WorkflowRecord) =
        match record with
        | WorkflowRecord.Started _ -> "started"
        | WorkflowRecord.StepScheduled(path, name, _, _, _) -> $"scheduled {path} {name}"
        | WorkflowRecord.StepCompleted(path, name, _, _, _) -> $"completed {path} {name}"
        | WorkflowRecord.StepFailed(path, name, _, error, _) -> $"failed {path} {name} ({error.Kind})"
        | WorkflowRecord.SignalReceived(name, _, _, _) -> $"signal {name}"
        | WorkflowRecord.CancelRequested _ -> "cancel requested"
        | WorkflowRecord.Finished _ -> "finished"
        | WorkflowRecord.Faulted(error, _) -> $"faulted ({error.Kind})"
        | WorkflowRecord.Cancelled _ -> "cancelled"
        | WorkflowRecord.Resumed(path, _) -> $"resumed {path}"

    let waiting () =
        match run.Machine with
        | None -> []
        | Some machine ->
            [ for KeyValue(path, step) in machine.Pending |> Seq.sortBy (fun pair -> run.BeganAt pair.Key) do
                  if step.ScheduledAt.IsSome then
                      let due =
                          match step.Leaf.Kind, step.Detail with
                          | (StepKind.Timer | StepKind.Signal), Some detail ->
                              Nullable(DateTime.Parse(detail, null, DateTimeStyles.RoundtripKind))
                          | _ -> Nullable()

                      { Path = path
                        Name = step.Leaf.Name
                        Kind = step.Leaf.Kind
                        Due = due } ]

    /// The step named `step` that waits for an outcome the test gives.
    let answerable (step: string) =
        let found =
            match run.Machine with
            | None -> None
            | Some machine ->
                machine.Pending.Values
                |> Seq.sortBy (fun pending -> run.BeganAt pending.Path)
                |> Seq.tryFind (fun pending ->
                    pending.ScheduledAt.IsSome
                    && pending.Leaf.Name = step
                    && (match pending.Leaf.Kind with
                        | StepKind.Activity
                        | StepKind.Command
                        | StepKind.Child -> true
                        | _ -> false))

        match found with
        | Some pending -> pending
        | None ->
            let names = waiting () |> List.map (fun step -> $"{step.Name} ({step.Kind})") |> String.concat ", "
            let waits = if names = "" then "nothing" else names
            invalidOp $"No activity, command, or child step named '{step}' is waiting. The workflow waits for {waits}. Use Signal for a signal wait and Advance for a timer."

    /// The workflow's name.
    member _.Name = run.Name

    /// The instance's ID, `test`. It is part of a child workflow's ID.
    member _.Id = run.Id

    /// The time the test runs at. It starts at the time given to `Start` and moves with `Advance`.
    member _.Now = run.Now

    /// Every record the instance stored, in journal order.
    member _.Journal: IReadOnlyList<WorkflowRecord> = run.Journal.AsReadOnly()

    /// One line per stored record, such as `"scheduled 0 price"` or `"completed 0 price"`: the
    /// step's path and name. Step values are in `Journal`.
    member _.Summary: IReadOnlyList<string> = [ for record in run.Journal -> describe record ] :> IReadOnlyList<string>

    /// The steps the body waits on, in the order they started.
    member _.Waiting: IReadOnlyList<WaitingStep> = waiting () :> IReadOnlyList<WaitingStep>

    /// Where the instance is, as `Status` reports it for a registered workflow.
    member _.Status: WorkflowStatus = run.Status

    /// How the instance ended, or `None` while it runs or is parked.
    member _.Outcome: WorkflowOutcome<'Result> option =
        match run.Final with
        | Some(WorkflowRecord.Finished(result, _)) -> Some(WorkflowOutcome.Finished(Payload.toValue<'Result> result))
        | Some(WorkflowRecord.Faulted(error, _)) -> Some(WorkflowOutcome.Faulted error)
        | Some _ -> Some WorkflowOutcome.Cancelled
        | None -> None

    /// The body's result. Raises `WorkflowFaultedException` or `WorkflowCancelledException` when the
    /// instance ended otherwise, and `InvalidOperationException` while it has not ended.
    member this.Result: 'Result =
        match this.Outcome with
        | Some(WorkflowOutcome.Finished result) -> result
        | Some(WorkflowOutcome.Faulted error) -> raise (WorkflowFaultedException(run.Name, run.Id, error))
        | Some WorkflowOutcome.Cancelled -> raise (WorkflowCancelledException(run.Name, run.Id))
        | None -> invalidOp $"The workflow has not ended: {run.Status}."

    /// Completes the waiting activity, command, or child step named `step` with `value`, which is
    /// stored and read back as the actor does. A value the step cannot read back fails the step as
    /// `NotRetryable`. Raises `InvalidOperationException` when no such step waits, and
    /// `ArgumentException` when `value` cannot be stored as the step's result.
    member _.Complete<'T>(step: string, value: 'T) : unit =
        let pending = answerable step

        let result =
            try
                pending.Leaf.Encode(box value)
            with error ->
                raise (ArgumentException($"A {typeof<'T>.Name} cannot be stored as the result of step '{step}': {error.Message}", nameof value, error))

        let record =
            try
                pending.Leaf.Decode result |> ignore
                WorkflowRecord.StepCompleted(pending.Path, pending.Leaf.Name, pending.Leaf.Kind, result, run.Now)
            with error ->
                WorkflowRecord.StepFailed(pending.Path, pending.Leaf.Name, pending.Leaf.Kind, Payload.errorOf ErrorKind.NotRetryable error, run.Now)

        run.Give record

    /// Fails the waiting activity, command, or child step named `step` with `kind`. A command that
    /// failed as `Exhausted` or `DeadlineExceeded` is stored as `OutcomeUnknown`, as the actor
    /// stores it. The body sees `WorkflowStepFailedException`.
    member _.Fail(step: string, error: exn, kind: ErrorKind) : unit =
        let pending = answerable step

        let kind =
            match pending.Leaf.Kind, kind with
            | StepKind.Command, (ErrorKind.Exhausted | ErrorKind.DeadlineExceeded) -> ErrorKind.OutcomeUnknown
            | _ -> kind

        run.Give(WorkflowRecord.StepFailed(pending.Path, pending.Leaf.Name, pending.Leaf.Kind, Payload.errorOf kind error, run.Now))

    /// Fails the waiting step named `step` as its retry policy's attempts running out
    /// (`Exhausted`).
    member this.Fail(step: string, error: exn) : unit = this.Fail(step, error, ErrorKind.Exhausted)

    /// Stores a signal, as `Workflows.signal` does. A wait for it takes it, now or when it starts.
    /// Raises `InvalidOperationException` when the instance has ended.
    member _.Signal<'T>(name: string, value: 'T) : unit =
        if ended () then
            invalidOp $"Workflow {run.Name} '{run.Id}' is {run.Status} and takes no signal."

        run.Give(WorkflowRecord.SignalReceived(name, Payload.ofValue value, run.Now, None))

    /// Starts a new run with `input`, as a start with `WorkflowIdReuse.AfterFailure` does: only
    /// when the instance faulted or was cancelled. Otherwise it stores nothing and reports the
    /// instance, as `AlreadyStarted`. The new run replays none of the earlier runs' records.
    member _.StartAgain(input: 'Input) : StartResult =
        match run.Final with
        | Some(WorkflowRecord.Faulted _ | WorkflowRecord.Cancelled _) ->
            run.Give(WorkflowRecord.Started(Payload.ofValue input, run.Now, None, None))
            StartResult.Started
        | _ -> StartResult.AlreadyStarted run.Status

    /// Resumes a faulted instance at the step whose failure faulted it, as `Resume` does for a
    /// registered workflow: the body replays its stored steps and that step waits again for the
    /// test's outcome. Raises `InvalidOperationException` with the reason when the instance cannot
    /// resume: it has not faulted, the body threw its own exception, or it ran steps after the
    /// failure.
    member _.Resume() : unit =
        match run.Final, run.Input with
        | Some(WorkflowRecord.Faulted _), Some input ->
            let history = History()

            for record in run.Journal do
                history.Apply record

            match Resumption.failedStep history run.EncodeResult (fun () -> run.Now) (fun () -> run.Body input) with
            | Ok path -> run.Give(WorkflowRecord.Resumed(path, run.Now))
            | Error reason -> invalidOp $"Workflow {run.Name} '{run.Id}' cannot resume. {reason}"
        | _ -> invalidOp $"Workflow {run.Name} '{run.Id}' is {run.Status}; only a faulted workflow resumes."

    /// Moves the time forward, and fires the timers and signal timeouts that are due by then.
    member _.Advance(by: TimeSpan) : unit =
        if by < TimeSpan.Zero then
            invalidArg (nameof by) "The time moves only forward."

        run.Now <- run.Now + by
        run.Settle()

    /// Cancels the instance: every waiting step fails as `Cancelled`, and the body can run its
    /// cleanup. An instance that has ended is left as it is.
    member _.Cancel() : unit =
        if not (ended ()) then
            let cancelled: StepError =
                { Kind = ErrorKind.Cancelled
                  ExceptionType = typeof<OperationCanceledException>.FullName |> Unchecked.nonNull
                  Message = "The workflow was cancelled." }

            let consequences =
                match run.Machine with
                | Some machine ->
                    [ for KeyValue(path, step) in machine.Pending do
                          if step.ScheduledAt.IsSome then
                              WorkflowRecord.StepFailed(path, step.Leaf.Name, step.Leaf.Kind, cancelled, run.Now) ]
                | None -> [ WorkflowRecord.Cancelled run.Now ]

            run.GiveAll(WorkflowRecord.CancelRequested run.Now :: consequences)

    /// Replays the journal into a new run of the body, as the process does after it stops. Steps
    /// with a stored outcome are not run again; a step that was waiting waits again.
    member _.Restart() : unit = run.Recover()

    /// Restarts the instance after every stored record the test produced, gives it the same
    /// outcomes, and checks that each restart stores the same journal. Raises
    /// `WorkflowReplayException` for the first restart that stores a different record or parks the
    /// instance: a body whose steps, stored values, or result change on replay. A pass does not show
    /// that the body is deterministic: an activity's arguments are not stored, so a GUID or a time
    /// read outside a step that reaches only an activity's call is not seen.
    member _.CheckReplay() : unit =
        let full = List.ofSeq run.Journal |> Array.ofList
        let times = Array.ofSeq run.Times
        let count = full.Length

        let differ (restartAfter: int) (index: int) (found: WorkflowRecord option) (parked: string option) =
            let show (record: WorkflowRecord option) =
                match record with
                | Some record -> $"'{describe record}'"
                | None -> "nothing"

            let expected = if index < count then Some full[index] else None

            let reason =
                match parked with
                | Some reason -> $"The restart parked the instance: {reason}"
                | None -> $"At record {index} it stored {show found}; the test stored {show expected}."

            raise (
                WorkflowReplayException(
                    $"A restart after {restartAfter} of {count} records stored a different journal. {reason} A body that is not deterministic takes another path on replay: read time, GUIDs, and random numbers through steps, and do I/O only in activities.",
                    restartAfter
                )
            )

        // A write the test gave is stored at once, so no restart falls inside it.
        let inside =
            set [ for batch in run.Batches do
                      match batch with
                      | first :: _ -> yield! [ first + 1 .. List.last batch ]
                      | [] -> () ]

        for restartAfter in [ max 1 given .. count ] |> List.filter (inside.Contains >> not) do
            let replay = TestRun(run.Name, run.Id, run.Body, run.EncodeResult, false)
            replay.Load [ for index in 0 .. restartAfter - 1 -> full[index], times[index], run.Inputs.Contains index ]
            replay.Now <- times[min restartAfter (count - 1)]
            replay.Recover()

            // The replay must have stored exactly the test's records up to `upTo`.
            let check (upTo: int) =
                if replay.Parked.IsSome && run.Parked.IsNone then
                    differ restartAfter replay.Journal.Count None replay.Parked

                let produced = replay.Journal.Count

                for index in restartAfter .. min produced upTo - 1 do
                    if replay.Journal[index] <> full[index] then
                        differ restartAfter index (Some replay.Journal[index]) None

                if produced > upTo then
                    differ restartAfter upTo (Some replay.Journal[upTo]) None
                elif produced < upTo then
                    differ restartAfter produced None None

            for batch in run.Batches do
                match batch with
                | first :: _ when first >= restartAfter ->
                    check first
                    replay.Now <- times[first]
                    replay.GiveAll [ for index in batch -> full[index] ]
                | _ -> ()

            check count

/// Runs workflows in memory, for tests. See `WorkflowTest<'Input, 'Result>`.
[<AbstractClass; Sealed>]
type WorkflowTest =

    /// The time a test starts at unless it gives one: 1 January 2026, 00:00 UTC.
    static member DefaultStart = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)

    static member private RunOf(definition: WorkflowDefinition<'Input, 'Result>, live: bool) =
        let body (input: Payload) : Workflow<objnull> =
            WorkflowCore.bind (definition.Body(Payload.toValue<'Input> input)) (fun result -> Return(box result))

        TestRun(definition.Name, "test", body, Payload.encode typeof<'Result>, live)

    /// Starts the workflow with `input` at `now`, and runs the body until it waits. The input is
    /// stored and read back, as `Start` does.
    static member Start(definition: WorkflowDefinition<'Input, 'Result>, input: 'Input, now: DateTime) : WorkflowTest<'Input, 'Result> =
        let run = WorkflowTest.RunOf(definition, true)
        run.Now <- now
        run.Give(WorkflowRecord.Started(Payload.ofValue input, now, None, None))
        WorkflowTest<'Input, 'Result>(run, 0)

    /// Starts the workflow with `input` at `WorkflowTest.DefaultStart`.
    static member Start(definition: WorkflowDefinition<'Input, 'Result>, input: 'Input) : WorkflowTest<'Input, 'Result> =
        WorkflowTest.Start(definition, input, WorkflowTest.DefaultStart)

    /// Starts a C# workflow with `input` at `now`.
    static member Start(workflow: Workflow<'Input, 'Result>, input: 'Input, now: DateTime) : WorkflowTest<'Input, 'Result> =
        WorkflowTest.Start(WorkflowHost.Define workflow, input, now)

    /// Starts a C# workflow with `input` at `WorkflowTest.DefaultStart`.
    static member Start(workflow: Workflow<'Input, 'Result>, input: 'Input) : WorkflowTest<'Input, 'Result> =
        WorkflowTest.Start(WorkflowHost.Define workflow, input, WorkflowTest.DefaultStart)

    /// Recovers an instance from a stored journal, such as one a test of the previous version of
    /// the workflow produced, and runs this code over it, as the process does after a deploy.
    /// `Status` reports `Parked` when the journal does not match the code. The test's time starts
    /// at the last record's time.
    static member Recover(definition: WorkflowDefinition<'Input, 'Result>, journal: WorkflowRecord seq) : WorkflowTest<'Input, 'Result> =
        let records = List.ofSeq journal

        let atOf (record: WorkflowRecord) =
            match record with
            | WorkflowRecord.Started(_, at, _, _)
            | WorkflowRecord.StepScheduled(_, _, _, at, _)
            | WorkflowRecord.StepCompleted(_, _, _, _, at)
            | WorkflowRecord.StepFailed(_, _, _, _, at)
            | WorkflowRecord.SignalReceived(_, _, at, _)
            | WorkflowRecord.CancelRequested at
            | WorkflowRecord.Finished(_, at)
            | WorkflowRecord.Faulted(_, at)
            | WorkflowRecord.Cancelled at
            | WorkflowRecord.Resumed(_, at) -> at

        let run = WorkflowTest.RunOf(definition, true)
        run.Load [ for record in records -> record, atOf record, false ]
        run.Recover()
        WorkflowTest<'Input, 'Result>(run, records.Length)

    /// Recovers a C# workflow's instance from a stored journal. See the definition overload.
    static member Recover(workflow: Workflow<'Input, 'Result>, journal: WorkflowRecord seq) : WorkflowTest<'Input, 'Result> =
        WorkflowTest.Recover(WorkflowHost.Define workflow, journal)
