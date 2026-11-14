namespace FCQRS.Workflows

open System
open Akka
open Akka.Streams.Dsl
open FCQRS.Workflows.CSharp

/// How one element's workflow ended, as `WorkflowFlow.Run` emits it to C#.
[<Sealed>]
type WorkflowEnded<'Result>(id: string, outcome: WorkflowOutcome<'Result>) =
    /// The workflow ID computed from the element.
    member _.Id = id

    /// True when the body returned a value.
    member _.IsFinished =
        match outcome with
        | WorkflowOutcome.Finished _ -> true
        | _ -> false

    /// The body's return value when `IsFinished` is true, and the type's default otherwise.
    member _.Result: 'Result =
        match outcome with
        | WorkflowOutcome.Finished result -> result
        | _ -> Unchecked.defaultof<'Result>

    /// True when the body threw an exception it did not handle.
    member _.IsFaulted =
        match outcome with
        | WorkflowOutcome.Faulted _ -> true
        | _ -> false

    /// The stored failure when `IsFaulted` is true, and null otherwise.
    member _.Error: StepError | null =
        match outcome with
        | WorkflowOutcome.Faulted error -> error
        | _ -> null

    /// True when the workflow was cancelled.
    member _.IsCancelled =
        match outcome with
        | WorkflowOutcome.Cancelled -> true
        | _ -> false

/// Akka.Streams stages that start workflows. `run` and `start` take an F# handle; `Run` and
/// `Start` take a C# client.
///
/// The stream is not durable. If the process stops, the workflows resume and the stream does
/// not, so the source must replay from an offset of its own, for example by committing only
/// after an element's outcome is emitted. Compute the workflow ID from the element: a replayed
/// element then reaches the instance it started before and starts no second one.
[<AbstractClass; Sealed>]
type WorkflowFlow =

    /// Starts one workflow per element and emits the ID with the start's answer. An element is
    /// emitted once its start is stored, and at most `parallelism` starts are in flight, so a
    /// slow journal slows the source down. Output keeps the input's order.
    static member start
        (handle: WorkflowHandle<'Input, 'Result>)
        (idOf: 'Input -> string)
        (parallelism: int)
        : Flow<'Input, struct (string * StartResult), NotUsed> =
        Flow
            .Create<'Input>()
            .SelectAsync(
                parallelism,
                fun input ->
                    task {
                        let id = idOf input
                        let! result = handle.Start id input
                        return struct (id, result)
                    }
            )

    /// Starts one workflow per element and emits the ID with the workflow's outcome, in the
    /// input's order. A faulted or cancelled workflow is an element like a finished one. At most
    /// `parallelism` elements are between their start and their outcome, so that many workflows
    /// run at once. An element whose workflow does not end within `timeout` fails the stream with
    /// a `TimeoutException`; the workflow itself carries on.
    static member run
        (handle: WorkflowHandle<'Input, 'Result>)
        (idOf: 'Input -> string)
        (parallelism: int)
        (timeout: TimeSpan)
        : Flow<'Input, struct (string * WorkflowOutcome<'Result>), NotUsed> =
        Flow
            .Create<'Input>()
            .SelectAsync(
                parallelism,
                fun input ->
                    task {
                        let id = idOf input
                        let! _ = handle.Start id input
                        let! outcome = handle.Result id timeout
                        return struct (id, outcome)
                    }
            )

    /// Starts one workflow per element and emits the ID with true when this element started the
    /// workflow, and false when a workflow with this ID already existed. An element is emitted
    /// once its start is stored, and at most `parallelism` starts are in flight, so a slow journal
    /// slows the source down. Output keeps the input's order.
    static member Start
        (client: IWorkflowClient<'Input, 'Result>, idOf: Func<'Input, string>, parallelism: int)
        : Flow<'Input, struct (string * bool), NotUsed> =
        Flow
            .Create<'Input>()
            .SelectAsync(
                parallelism,
                fun input ->
                    task {
                        let id = idOf.Invoke input
                        let! started = client.StartAsync(id, input)
                        return struct (id, started)
                    }
            )

    /// Starts one workflow per element and emits how it ended, in the input's order. A faulted
    /// or cancelled workflow is an element like a finished one, not a failure of the stream. At
    /// most `parallelism` elements are between their start and their outcome, so that many
    /// workflows run at once. An element whose workflow does not end within `timeout` fails the
    /// stream with a `TimeoutException`; the workflow itself carries on.
    static member Run
        (client: IWorkflowClient<'Input, 'Result>, idOf: Func<'Input, string>, parallelism: int, timeout: TimeSpan)
        : Flow<'Input, WorkflowEnded<'Result>, NotUsed> =
        Flow
            .Create<'Input>()
            .SelectAsync(
                parallelism,
                fun input ->
                    task {
                        let id = idOf.Invoke input
                        let! _ = client.StartAsync(id, input)
                        let! outcome = client.OutcomeAsync(id, timeout)
                        return WorkflowEnded(id, outcome)
                    }
            )
