namespace FCQRS.Workflows

open System
open System.Collections.Generic

/// What the journal holds for one step path.
type internal StepState =
    | Scheduled of name: string * kind: StepKind * at: DateTime * detail: string option
    | Completed of name: string * kind: StepKind * result: Payload
    | Failed of name: string * kind: StepKind * error: StepError

/// A step the body is waiting on.
type internal PendingStep =
    { Path: string
      Leaf: Leaf
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

/// Raised when the journal holds a different step at a path than the body asks for.
type WorkflowDivergedException(path: string, expected: string, found: string) =
    inherit Exception($"The workflow body asks for {expected} at step {path}, but the journal holds {found}.")
    /// The step path.
    member _.Path = path

/// Interprets a workflow body against the journal. It holds no actor and performs no I/O: it
/// returns the records to store and the steps to start.
type internal Machine(history: IDictionary<string, StepState>, resultType: Type, clock: unit -> DateTime) =
    let pending = Dictionary<string, PendingStep>()
    let queue = Queue<unit -> unit>()
    let emits = ResizeArray<Emit>()
    let mutable outcome: Result<objnull, exn> option = None
    let mutable counter = 0

    let nextPath () =
        let path = string counter
        counter <- counter + 1
        path

    let check (path: string) (leaf: Leaf) (name: string) (kind: StepKind) =
        if name <> leaf.Name || kind <> leaf.Kind then
            raise (WorkflowDivergedException(path, $"{leaf.Kind} '{leaf.Name}'", $"{kind} '{name}'"))

    let rec run (workflow: Workflow<objnull>) (onDone: Result<objnull, exn> -> unit) : unit =
        match WorkflowCore.force workflow with
        | Return value -> onDone (Ok value)
        | Fail error -> onDone (Error error)
        | Lazy _ -> invalidOp "force leaves no Lazy"
        | Step(leaf, k) ->
            let path = nextPath ()

            let resume (result: Result<objnull, exn>) =
                queue.Enqueue(fun () -> run (WorkflowCore.protect (fun () -> k result)) onDone)

            let wait (scheduledAt: DateTime option) (detail: string option) =
                let step =
                    { Path = path
                      Leaf = leaf
                      ScheduledAt = scheduledAt
                      Detail = detail
                      Resume = resume }

                pending[path] <- step
                step

            match history.TryGetValue path with
            | true, Completed(name, kind, result) ->
                check path leaf name kind
                resume (Ok(Payload.decode leaf.ResultType result))
            | true, Failed(name, kind, error) ->
                check path leaf name kind
                resume (Error(WorkflowStepFailedException(path, name, error)))
            | true, Scheduled(name, kind, at, detail) ->
                check path leaf name kind
                emits.Add(Begin(wait (Some at) detail))
            | false, _ ->
                let time = clock ()

                match leaf.Op with
                | ValueOp produce ->
                    wait None None |> ignore
                    let result = Payload.encode leaf.ResultType (produce time)
                    emits.Add(Store(WorkflowRecord.StepCompleted(path, leaf.Name, leaf.Kind, result, time)))
                | TimerOp due ->
                    let dueAt =
                        match due with
                        | After duration -> time + duration
                        | At instant -> instant

                    wait None None |> ignore
                    emits.Add(Store(WorkflowRecord.StepScheduled(path, leaf.Name, leaf.Kind, time, Some(dueAt.ToString "O"))))
                | ActivityOp _ ->
                    wait None None |> ignore
                    emits.Add(Store(WorkflowRecord.StepScheduled(path, leaf.Name, leaf.Kind, time, None)))

    let finish (result: Result<objnull, exn>) =
        outcome <- Some result
        let time = clock ()

        match result with
        | Ok value -> emits.Add(Store(WorkflowRecord.Finished(Payload.encode resultType value, time)))
        | Error(:? WorkflowStepFailedException as failed) -> emits.Add(Store(WorkflowRecord.Faulted(failed.Error, time)))
        | Error error -> emits.Add(Store(WorkflowRecord.Faulted(Payload.errorOf ErrorKind.BodyFailed error, time)))

    let drain () =
        while queue.Count > 0 do
            queue.Dequeue () ()

        let result = List.ofSeq emits
        emits.Clear()
        result

    /// Runs the body from the top. Steps with a stored outcome are fed that outcome, and the body
    /// stops at the steps that have none.
    member _.Run(body: Workflow<objnull>) : Emit list =
        run body finish
        drain ()

    /// Applies a record that was just stored, and continues the body where that allows.
    member _.Apply(record: WorkflowRecord) : Emit list =
        match record with
        | WorkflowRecord.StepScheduled(path, name, kind, at, detail) ->
            history[path] <- Scheduled(name, kind, at, detail)

            match pending.TryGetValue path with
            | true, step ->
                let scheduled =
                    { step with
                        ScheduledAt = Some at
                        Detail = detail }

                pending[path] <- scheduled
                emits.Add(Begin scheduled)
            | _ -> ()
        | WorkflowRecord.StepCompleted(path, name, kind, result, _) ->
            history[path] <- Completed(name, kind, result)

            match pending.TryGetValue path with
            | true, step ->
                pending.Remove path |> ignore
                step.Resume(Ok(Payload.decode step.Leaf.ResultType result))
            | _ -> ()
        | WorkflowRecord.StepFailed(path, name, kind, error, _) ->
            history[path] <- Failed(name, kind, error)

            match pending.TryGetValue path with
            | true, step ->
                pending.Remove path |> ignore
                step.Resume(Error(WorkflowStepFailedException(path, name, error)))
            | _ -> ()
        | _ -> ()

        drain ()

    /// The steps the body is waiting on, by path.
    member _.Pending: IReadOnlyDictionary<string, PendingStep> = pending

    /// The body's result, once it has returned or thrown.
    member _.Outcome = outcome

/// Reads journal records into the table the machine replays from.
module internal History =

    let apply (history: IDictionary<string, StepState>) (record: WorkflowRecord) =
        match record with
        | WorkflowRecord.StepScheduled(path, name, kind, at, detail) -> history[path] <- Scheduled(name, kind, at, detail)
        | WorkflowRecord.StepCompleted(path, name, kind, result, _) -> history[path] <- Completed(name, kind, result)
        | WorkflowRecord.StepFailed(path, name, kind, error, _) -> history[path] <- Failed(name, kind, error)
        | _ -> ()
