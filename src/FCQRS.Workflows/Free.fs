namespace FCQRS.Workflows

open FCQRS.Model.FreeMonad

/// Runs a program written with `FCQRS.Model.FreeMonad` as a workflow. With both namespaces
/// open, `Free.bind` and `Free.toWorkflow` both resolve.
[<RequireQualifiedAccess>]
module Free =

    /// A step's result, boxed: the answer that an instruction's continuation takes.
    let answer (step: Workflow<'T>) : Workflow<objnull> = WorkflowCore.boxed step

    /// The workflow that runs `program`: `step` gives the workflow step that carries out each
    /// instruction, such as `Free.answer (activity "charge" policy run)`, and its result is the
    /// instruction's answer. Each step stores its result as any workflow step does, so a restart
    /// replays the stored answers through the program and continues at the instruction it had
    /// reached. The program's own code runs again on every replay and must be deterministic,
    /// like a workflow body.
    ///
    /// The step names that `step` gives are persistent contracts, and so is the order in which
    /// the program performs its instructions: an instance whose journal holds another name or
    /// order is parked. A step whose result has another type than the program expects faults
    /// the instance.
    let rec toWorkflow (step: 'Instruction -> Workflow<objnull>) (program: Free<'Instruction, 'Result>) : Workflow<'Result> =
        match program with
        | Pure result -> workflow { return result }
        | Impure(instruction, next) ->
            workflow {
                let! answer = step instruction
                // `Unchecked.nonNull` only changes the static type: FCQRS.Model 6.1.0 declares
                // the continuation over `obj`, and a null answer is `()` or `None`.
                return! toWorkflow step (next (Unchecked.nonNull answer))
            }
