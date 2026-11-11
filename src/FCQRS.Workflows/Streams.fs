namespace FCQRS.Workflows

open System
open Akka
open Akka.Streams.Dsl

/// Akka.Streams stages that start workflows.
///
/// The stream is not durable. If the process stops, the workflows resume and the stream does
/// not, so the source must replay from an offset of its own, for example by committing only
/// after an element's outcome is emitted. Compute the workflow ID from the element: a replayed
/// element then reaches the instance it started before and starts no second one.
module WorkflowFlow =

    /// Starts one workflow per element and emits the ID with the start's answer. An element is
    /// emitted once its start is stored, and at most `parallelism` starts are in flight, so a
    /// slow journal slows the source down. Output keeps the input's order.
    let start
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
    /// input's order. At most `parallelism` elements are between their start and their outcome,
    /// so that many workflows run at once. An element whose workflow does not end within
    /// `timeout` fails the stream with a `TimeoutException`; the workflow itself carries on.
    let run
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
