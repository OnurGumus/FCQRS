namespace FCQRS.Workflows

open System
open System.Collections.Generic

/// What the journal holds for one step path.
type internal StepState =
    | Scheduled of name: string * kind: StepKind * at: DateTime * detail: string option
    | Completed of name: string * kind: StepKind * result: Payload
    | Failed of name: string * kind: StepKind * error: StepError

/// The journal as the machine replays it: the state of every step, and the signals that were
/// stored and not yet consumed by a wait.
type internal History() =
    /// The state of each step, by path.
    member val Steps = Dictionary<string, StepState>()
    /// Stored signals no wait has consumed, oldest first: name, payload, and the trace context
    /// of the caller that sent the signal.
    member val Signals = ResizeArray<struct (string * Payload * string option)>()
    /// Whether a cancel request is stored.
    member val CancelRequested = false with get, set

    /// Whether the journal holds a step at this path or under it.
    member this.Reached(path: string) : bool =
        this.Steps.ContainsKey path
        || this.Steps.Keys |> Seq.exists (fun stored -> stored.StartsWith(path + "/"))

    /// Removes and returns the oldest stored signal with this name: its payload and its sender's
    /// trace context.
    member this.TakeSignal(name: string) : struct (Payload * string option) voption =
        match this.Signals.FindIndex(fun (struct (stored, _, _)) -> stored = name) with
        | -1 -> ValueNone
        | index ->
            let struct (_, payload, sender) = this.Signals[index]
            this.Signals.RemoveAt index
            ValueSome(struct (payload, sender))

    /// Applies a record read from the journal, in journal order.
    member this.Apply(record: WorkflowRecord) =
        match record with
        | WorkflowRecord.StepScheduled(path, name, kind, at, detail) -> this.Steps[path] <- Scheduled(name, kind, at, detail)
        | WorkflowRecord.StepCompleted(path, name, kind, result, _) ->
            this.Steps[path] <- Completed(name, kind, result)

            // A wait consumed the oldest signal with its name, unless its timeout passed first.
            if kind = StepKind.Signal && result <> Payload.timedOut then
                this.TakeSignal name |> ignore
        | WorkflowRecord.StepFailed(path, name, kind, error, _) ->
            this.Steps[path] <- Failed(name, kind, error)

            // A wait that could not read its signal consumed it all the same.
            if kind = StepKind.Signal && error.Kind = ErrorKind.NotRetryable then
                this.TakeSignal name |> ignore
        | WorkflowRecord.SignalReceived(name, payload, _, sender) -> this.Signals.Add(struct (name, payload, sender))
        | WorkflowRecord.CancelRequested _ -> this.CancelRequested <- true
        | _ -> ()

/// One line of execution: the body itself, or a branch of a parallel block. A branch's steps
/// have paths under the block's path and the branch's index.
type internal Fiber(prefix: string, parent: Fiber option) =
    let mutable next = 0
    let mutable cancelled = false

    member _.NextPath() =
        let path = prefix + string next
        next <- next + 1
        path

    /// The path the fiber's next step will take.
    member _.PeekPath() = prefix + string next

    /// The block this branch belongs to, if it is a branch.
    member _.Parent = parent

    member _.Cancel() = cancelled <- true

    /// A branch is cancelled with the block it belongs to.
    member _.IsCancelled: bool =
        cancelled
        || (match parent with
            | Some parent -> parent.IsCancelled
            | None -> false)

/// A step the body is waiting on.
type internal PendingStep =
    { Path: string
      Leaf: Leaf
      Fiber: Fiber
      /// The stored start of the step. `None` until its `StepScheduled` record is stored.
      ScheduledAt: DateTime option
      Detail: string option
      Resume: Result<objnull, exn> -> unit }

/// What the machine asks of the actor.
type internal Emit =
    /// Store this record, then apply it.
    | Store of WorkflowRecord
    /// Start, or after a restart resume, this step.
    | Begin of PendingStep
    /// Stop working on this step: its branch was abandoned.
    | Abandon of path: string

/// Raised when the journal holds a different step at a path than the body asks for.
type WorkflowDivergedException(path: string, expected: string, found: string) =
    inherit Exception($"The workflow body asks for {expected} at step {path}, but the journal holds {found}.")
    /// The step path.
    member _.Path = path

/// Interprets a workflow body against the journal. It holds no actor and performs no I/O: it
/// returns the records to store and the steps to start or abandon.
type internal Machine(history: History, encodeResult: objnull -> Payload, clock: unit -> DateTime) =
    let pending = Dictionary<string, PendingStep>()
    /// Signal waits whose step is stored as scheduled, in the order they began waiting.
    let signalWaits = ResizeArray<string>()
    /// The sender's trace context of the signal each wait consumed, by the wait's path, until the
    /// actor takes it for the wait's span.
    let signalSenders = Dictionary<string, string>()
    /// Races whose winner is decided and being stored.
    let decidedRaces = Dictionary<string, unit -> unit>()
    let queue = Queue<unit -> unit>()
    let emits = ResizeArray<Emit>()
    let root = Fiber("", None)
    let mutable outcome: Result<objnull, exn> option = None

    let check (path: string) (expected: string) (expectedKind: StepKind) (name: string) (kind: StepKind) =
        if name <> expected || kind <> expectedKind then
            raise (WorkflowDivergedException(path, $"{expectedKind} '{expected}'", $"{kind} '{name}'"))

    /// `at + span`, or the latest representable time when that overflows.
    let later (at: DateTime) (span: TimeSpan) : DateTime =
        if span >= DateTime.MaxValue - at then DateTime.MaxValue else at + span

    /// A stored result the step's type can no longer read does not match the code.
    let decode (path: string) (leaf: Leaf) (result: Payload) : objnull =
        try
            leaf.Decode result
        with error ->
            raise (WorkflowDivergedException(path, $"a result '{leaf.Name}' can read", $"a {result.Type} it cannot read ({error.Message})"))

    /// Drops the steps of cancelled branches.
    let sweep () =
        for path in [ for KeyValue(path, step) in pending do if step.Fiber.IsCancelled then path ] do
            pending.Remove path |> ignore
            signalWaits.Remove path |> ignore
            emits.Add(Abandon path)

    /// Gives each waiting signal step the oldest stored signal with its name.
    let matchSignals () =
        for path in List.ofSeq signalWaits do
            match pending.TryGetValue path with
            | true, step ->
                match history.TakeSignal step.Leaf.Name with
                | ValueSome(struct (payload, sender)) ->
                    signalWaits.Remove path |> ignore
                    sender |> Option.iter (fun sender -> signalSenders[path] <- sender)

                    let record =
                        try
                            step.Leaf.Decode payload |> ignore
                            WorkflowRecord.StepCompleted(path, step.Leaf.Name, step.Leaf.Kind, payload, clock ())
                        with error ->
                            // A signal of the wrong type must not be stored as the wait's result:
                            // no replay could read it. The wait fails, and the body can handle it.
                            let unreadable =
                                { Kind = ErrorKind.NotRetryable
                                  ExceptionType = error.GetType().FullName |> Unchecked.nonNull
                                  Message = $"Signal '{step.Leaf.Name}' carried a {payload.Type} this wait cannot read: {error.Message}" }

                            WorkflowRecord.StepFailed(path, step.Leaf.Name, step.Leaf.Kind, unreadable, clock ())

                    emits.Add(Store record)
                | ValueNone -> ()
            | _ -> signalWaits.Remove path |> ignore

    /// Whether the journal shows that the instance went past the fiber's next step: a step is
    /// stored there, or the block the fiber is a branch of was itself passed.
    let rec passed (fiber: Fiber) : bool =
        history.Reached(fiber.PeekPath())
        || (match fiber.Parent with
            | Some parent -> passed parent
            | None -> false)

    let rec run (fiber: Fiber) (workflow: Workflow<objnull>) (onDone: Result<objnull, exn> -> unit) : unit =
        match WorkflowCore.force workflow with
        | Return value -> onDone (Ok value)
        | Fail error -> onDone (Error error)
        | Lazy _ -> invalidOp "force leaves no Lazy"
        | Step(node, k) ->
            let path =
                match node with
                // A version marker sits beside the step sequence, so adding one moves no step.
                | Leaf { Kind = StepKind.Version; Name = changeId } -> $"{fiber.PeekPath()}@{changeId}"
                | _ -> fiber.NextPath()

            let resume (result: Result<objnull, exn>) =
                queue.Enqueue(fun () ->
                    if not fiber.IsCancelled then
                        run fiber (WorkflowCore.protect (fun () -> k result)) onDone)

            match node with
            | Leaf({ Kind = StepKind.Version } as leaf) when not (history.Steps.ContainsKey path) && passed fiber ->
                // The instance passed this point before the marker existed: it runs the old code.
                ignore leaf
                resume (Ok(box 1))
            | Leaf leaf -> runLeaf fiber path leaf resume
            | AllOf(branches, limit) -> runAll fiber path (Array.ofList branches) limit resume
            | RaceOf branches -> runRace fiber path (Array.ofList branches) resume

    and runLeaf (fiber: Fiber) (path: string) (leaf: Leaf) (resume: Result<objnull, exn> -> unit) =
        let wait (scheduledAt: DateTime option) (detail: string option) =
            let step =
                { Path = path
                  Leaf = leaf
                  Fiber = fiber
                  ScheduledAt = scheduledAt
                  Detail = detail
                  Resume = resume }

            pending[path] <- step
            step

        match history.Steps.TryGetValue path with
        | true, Completed(name, kind, result) ->
            check path leaf.Name leaf.Kind name kind
            resume (Ok(decode path leaf result))
        | true, Failed(name, kind, error) ->
            check path leaf.Name leaf.Kind name kind
            resume (Error(WorkflowStepFailedException(path, name, error)))
        | true, Scheduled(name, kind, at, detail) ->
            check path leaf.Name leaf.Kind name kind
            emits.Add(Begin(wait (Some at) detail))

            if leaf.Kind = StepKind.Signal then
                signalWaits.Add path
        | false, _ ->
            let time = clock ()
            wait None None |> ignore

            match leaf.Op with
            | ValueOp produce ->
                emits.Add(Store(WorkflowRecord.StepCompleted(path, leaf.Name, leaf.Kind, leaf.Encode(produce time), time)))
            | TimerOp due ->
                let dueAt =
                    match due with
                    | After duration -> later time duration
                    | At instant -> instant

                emits.Add(Store(WorkflowRecord.StepScheduled(path, leaf.Name, leaf.Kind, time, Some(dueAt.ToString "O"))))
            | SignalOp timeout ->
                let detail = timeout |> Option.map (fun timeout -> (later time timeout).ToString "O")
                emits.Add(Store(WorkflowRecord.StepScheduled(path, leaf.Name, leaf.Kind, time, detail)))
            | ActivityOp _ -> emits.Add(Store(WorkflowRecord.StepScheduled(path, leaf.Name, leaf.Kind, time, None)))

    and runAll (fiber: Fiber) (path: string) (branches: Workflow<objnull>[]) (limit: int option) (resume: Result<objnull, exn> -> unit) =
        let count = branches.Length
        let fibers = Array.init count (fun index -> Fiber($"{path}/{index}/", Some fiber))
        let results: objnull[] = Array.zeroCreate count
        let mutable finished = 0
        let mutable started = 0
        let mutable failed = false

        let rec startNext () =
            if started < count && not failed then
                let index = started
                started <- started + 1

                queue.Enqueue(fun () ->
                    if not fibers[index].IsCancelled then
                        run fibers[index] branches[index] (branchDone index))

        and branchDone (index: int) (result: Result<objnull, exn>) =
            match result with
            | Ok value ->
                results[index] <- value
                finished <- finished + 1

                if finished = count then
                    resume (Ok(box (List.ofArray results)))
                else
                    startNext ()
            | Error error ->
                if not failed then
                    failed <- true

                    for branch in fibers do
                        branch.Cancel()

                    sweep ()
                    resume (Error error)

        if count = 0 then
            resume (Ok(box ([]: objnull list)))
        else
            for _ in 1 .. min count (defaultArg limit count) do
                startNext ()

    and runRace (fiber: Fiber) (path: string) (branches: Workflow<objnull>[]) (resume: Result<objnull, exn> -> unit) =
        let fibers = Array.init branches.Length (fun index -> Fiber($"{path}/{index}/", Some fiber))

        match history.Steps.TryGetValue path with
        | true, Completed(name, kind, result) ->
            check path "race" StepKind.Race name kind
            // The winner is stored: only that branch runs, from its stored steps.
            let winner = Payload.toValue<int> result
            queue.Enqueue(fun () -> run fibers[winner] branches[winner] resume)
        | true, other -> raise (WorkflowDivergedException(path, "Race 'race'", string other))
        | false, _ ->
            let mutable decided = false

            let branchDone (index: int) (result: Result<objnull, exn>) =
                if not decided then
                    decided <- true

                    for other in 0 .. fibers.Length - 1 do
                        if other <> index then
                            fibers[other].Cancel()

                    sweep ()
                    // The body continues once the winner is stored.
                    decidedRaces[path] <- fun () -> resume result
                    emits.Add(Store(WorkflowRecord.StepCompleted(path, "race", StepKind.Race, Payload.ofValue index, clock ())))

            for index in 0 .. branches.Length - 1 do
                queue.Enqueue(fun () ->
                    if not fibers[index].IsCancelled && not decided then
                        run fibers[index] branches[index] (branchDone index))

    let finish (result: Result<objnull, exn>) =
        outcome <- Some result
        let time = clock ()

        match result with
        | Ok value ->
            let stored =
                try
                    WorkflowRecord.Finished(encodeResult value, time)
                with error ->
                    // A result that cannot be stored cannot be given to anyone who asks later.
                    WorkflowRecord.Faulted(Payload.errorOf ErrorKind.BodyFailed error, time)

            emits.Add(Store stored)
        | Error(:? WorkflowStepFailedException as failed) when failed.Error.Kind = ErrorKind.Cancelled && history.CancelRequested ->
            emits.Add(Store(WorkflowRecord.Cancelled time))
        | Error(:? WorkflowStepFailedException as failed) -> emits.Add(Store(WorkflowRecord.Faulted(failed.Error, time)))
        | Error error -> emits.Add(Store(WorkflowRecord.Faulted(Payload.errorOf ErrorKind.BodyFailed error, time)))

    let drain () =
        while queue.Count > 0 do
            queue.Dequeue () ()

        matchSignals ()

        // A branch abandoned in this pass has nothing to begin.
        let result = [ for emit in emits do
                           match emit with
                           | Begin step when step.Fiber.IsCancelled -> ()
                           | other -> other ]

        emits.Clear()
        result

    /// Runs the body from the top. Steps with a stored outcome are fed that outcome, and the body
    /// stops at the steps that have none.
    member _.Run(body: Workflow<objnull>) : Emit list =
        run root body finish
        drain ()

    /// Applies a record that was just stored, and continues the body where that allows.
    member _.Apply(record: WorkflowRecord) : Emit list =
        match record with
        | WorkflowRecord.StepScheduled(path, name, kind, at, detail) ->
            history.Steps[path] <- Scheduled(name, kind, at, detail)

            match pending.TryGetValue path with
            | true, step ->
                let scheduled =
                    { step with
                        ScheduledAt = Some at
                        Detail = detail }

                pending[path] <- scheduled
                emits.Add(Begin scheduled)

                if kind = StepKind.Signal then
                    signalWaits.Add path
            | _ -> ()
        | WorkflowRecord.StepCompleted(path, name, kind, result, _) ->
            history.Steps[path] <- Completed(name, kind, result)

            match pending.TryGetValue path with
            | true, step ->
                pending.Remove path |> ignore
                signalWaits.Remove path |> ignore
                step.Resume(Ok(decode path step.Leaf result))
            | _ ->
                match decidedRaces.TryGetValue path with
                | true, continueBody ->
                    decidedRaces.Remove path |> ignore
                    continueBody ()
                | _ -> ()
        | WorkflowRecord.StepFailed(path, name, kind, error, _) ->
            history.Steps[path] <- Failed(name, kind, error)

            match pending.TryGetValue path with
            | true, step ->
                pending.Remove path |> ignore
                signalWaits.Remove path |> ignore
                step.Resume(Error(WorkflowStepFailedException(path, name, error)))
            | _ -> ()
        | WorkflowRecord.SignalReceived(name, payload, _, sender) -> history.Signals.Add(struct (name, payload, sender))
        | WorkflowRecord.CancelRequested _ -> history.CancelRequested <- true
        | _ -> ()

        drain ()

    /// Removes and returns the trace context of the caller whose signal the wait at `path`
    /// consumed. `None` when the wait timed out, or the sender had no trace.
    member _.TakeSignalSender(path: string) : string option =
        match signalSenders.TryGetValue path with
        | true, sender ->
            signalSenders.Remove path |> ignore
            Some sender
        | _ -> None

    /// The steps the body is waiting on, by path.
    member _.Pending: IReadOnlyDictionary<string, PendingStep> = pending

    /// The body's result, once it has returned or thrown.
    member _.Outcome = outcome
