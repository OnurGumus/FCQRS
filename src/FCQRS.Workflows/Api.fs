namespace FCQRS.Workflows

open System
open Akka.Actor
open Akkling.Persistence
open FCQRS.Common
open FCQRS.AkklingHelpers
open FCQRS.Workflows.Protocol

/// A workflow's name and body. The name is stored in the journal's persistence IDs and must
/// not change, and it must differ from every aggregate and saga name.
type WorkflowDefinition<'Input, 'Result> =
    { Name: string
      Body: 'Input -> Workflow<'Result> }

/// Starts and queries the instances of a registered workflow.
type WorkflowHandle<'Input, 'Result> =
    { /// The workflow's name.
      Name: string
      /// Starts the workflow with this ID. Returns once the start is stored. A second start with
      /// the same ID stores nothing and reports the existing instance.
      Start: string -> 'Input -> Async<StartResult>
      /// Where the instance is.
      Status: string -> Async<WorkflowStatus>
      /// Waits up to the given time for the instance to end. The wait is in memory: the workflow
      /// continues when the caller stops waiting. Raises `TimeoutException` when the time passes,
      /// and `InvalidOperationException` when no workflow with this ID was started.
      Result: string -> TimeSpan -> Async<WorkflowOutcome<'Result>>
      /// Stores a signal for the instance: ID, signal name, payload. Returns once it is stored.
      /// Raises `InvalidOperationException` when the workflow was not started or has ended.
      /// `Workflows.signal` takes a typed payload.
      Signal: string -> string -> Payload -> Async<unit>
      /// Cancels the instance. Every pending step fails with a `Cancelled` error, which the body
      /// can catch to run cleanup steps; a body that lets it pass ends as cancelled. Returns once
      /// the request is stored. An instance that has already ended is left as it is. Raises
      /// `InvalidOperationException` when no workflow with this ID was started.
      Cancel: string -> Async<unit> }

/// Defines workflows.
module Workflow =

    /// A workflow named `name` whose instances run `body` on their input.
    let define (name: string) (body: 'Input -> Workflow<'Result>) : WorkflowDefinition<'Input, 'Result> =
        { Name = name; Body = body }

/// Registers workflows with an actor system.
module Workflows =

    let private requestTimeout = TimeSpan.FromSeconds 30.0

    let private checkId (id: string) =
        if String.IsNullOrWhiteSpace id then
            invalidArg (nameof id) "A workflow ID must not be blank."

        if id.Contains '~' then
            invalidArg (nameof id) $"Workflow ID '{id}' contains '~', which FCQRS uses to separate the parts of saga names and topics."

    /// Registers the workflow and returns its handle. Instances that were running when the
    /// process last stopped resume as soon as this is called.
    let register (api: IActor) (definition: WorkflowDefinition<'Input, 'Result>) : WorkflowHandle<'Input, 'Result> =
        JournalTypes.Map<WorkflowRecord> "fcqrs.workflow.record"

        let erased: WorkflowActor.Definition =
            { Name = definition.Name
              EncodeResult = Payload.encode typeof<'Result>
              Body =
                fun input ->
                    WorkflowCore.bind (definition.Body(Payload.toValue<'Input> input)) (fun result -> Return(box result)) }

        // Remembered, so a running instance restarts with the process. That also turns idle
        // passivation off: an instance leaves memory when it ends.
        let factory =
            entityFactoryFor
                api.System
                shardResolver
                definition.Name
                (propsPersist (WorkflowActor.behavior erased api))
                PassivationPolicy.Default
                true

        let ask (id: string) (message: obj) (timeout: TimeSpan) : Async<obj> =
            checkId id

            let envelope: Akkling.Cluster.Sharding.ShardEnvelope =
                { ShardId = DEFAULT_SHARD
                  EntityId = id
                  Message = message }

            async {
                try
                    return! factory.ShardRegion.Ask<obj>(envelope, timeout) |> Async.AwaitTask
                with
                | :? AggregateException as error when (error.InnerException :? AskTimeoutException) ->
                    return raise (TimeoutException $"Workflow {definition.Name} {id} did not answer within {timeout}.")
                | :? AskTimeoutException ->
                    return raise (TimeoutException $"Workflow {definition.Name} {id} did not answer within {timeout}.")
            }

        { Name = definition.Name
          Start =
            fun id input ->
                async {
                    let start =
                        { Input = Payload.ofValue input
                          Traceparent = currentTraceparent ()
                          Parent = None }

                    let! reply = ask id start requestTimeout

                    match reply with
                    | :? StartReply as reply when reply.Created -> return StartResult.Started
                    | :? StartReply as reply -> return StartResult.AlreadyStarted reply.Status
                    | other -> return invalidOp $"Unexpected reply to a workflow start: {other}"
                }
          Status =
            fun id ->
                async {
                    let! reply = ask id GetStatus requestTimeout
                    return reply :?> WorkflowStatus
                }
          Result =
            fun id timeout ->
                async {
                    let! reply = ask id AwaitResult timeout

                    match reply with
                    | :? ResultReply as reply ->
                        match reply with
                        | ResultFinished result -> return WorkflowOutcome.Finished(Payload.toValue<'Result> result)
                        | ResultFaulted error -> return WorkflowOutcome.Faulted error
                        | ResultCancelled -> return WorkflowOutcome.Cancelled
                        | ResultNotStarted -> return invalidOp $"No workflow {definition.Name} with ID '{id}' was started."
                    | other -> return invalidOp $"Unexpected reply to a workflow result request: {other}"
                }
          Signal =
            fun id name payload ->
                async {
                    let signal =
                        { SignalName = name
                          SignalPayload = payload
                          SignalTraceparent = currentTraceparent () }

                    let! reply = ask id signal requestTimeout

                    match reply with
                    | :? SignalReply as reply ->
                        match reply with
                        | SignalAccepted -> return ()
                        | SignalRefused status ->
                            return invalidOp $"Workflow {definition.Name} '{id}' is {status} and takes no signal."
                    | other -> return invalidOp $"Unexpected reply to a workflow signal: {other}"
                }
          Cancel =
            fun id ->
                async {
                    let! reply = ask id CancelWorkflow requestTimeout

                    match reply with
                    | :? CancelReply as reply ->
                        match reply with
                        | CancelAccepted -> return ()
                        | CancelRefused _ -> return invalidOp $"No workflow {definition.Name} with ID '{id}' was started."
                    | other -> return invalidOp $"Unexpected reply to a workflow cancel request: {other}"
                } }

    /// Stores the signal `name` with a typed payload for the instance `id`.
    let signal (handle: WorkflowHandle<'Input, 'Result>) (id: string) (name: string) (payload: 'Signal) : Async<unit> =
        handle.Signal id name (Payload.ofValue payload)
