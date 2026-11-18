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
      /// the same ID stores nothing and reports the existing instance, also when it has ended.
      Start: string -> 'Input -> Async<StartResult>
      /// Starts the workflow with this ID under a reuse policy. With `WorkflowIdReuse.AfterFailure`,
      /// an instance that faulted or was cancelled starts a new run with this input; a running,
      /// parked, or finished one stores nothing and is reported. `Start` is `StartWith Reject`.
      StartWith: WorkflowIdReuse -> string -> 'Input -> Async<StartResult>
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
      Cancel: string -> Async<unit>
      /// Resumes a faulted instance at the step whose failure faulted it: the body replays its
      /// stored steps and runs that step again, with the same idempotency key, and continues. Steps
      /// that completed before the failure do not run again. Returns once the resume is stored.
      /// Raises `InvalidOperationException` when the instance has not faulted, when the body threw
      /// its own exception, or when the body ran steps after the failure, such as compensation.
      Resume: string -> Async<unit> }

/// Defines workflows.
module Workflow =

    /// A workflow named `name` whose instances run `body` on their input.
    let define (name: string) (body: 'Input -> Workflow<'Result>) : WorkflowDefinition<'Input, 'Result> =
        { Name = name; Body = body }

/// Registers workflows with an actor system.
module Workflows =

    let private requestTimeout = TimeSpan.FromSeconds 30.0

    let private idleTimeKey = "akka.fcqrs.workflow-idle-timeout"

    /// How long an instance that is not running stays in memory after a request reached it. Five
    /// seconds unless the configuration says otherwise.
    let private idleTimeOf (config: Akka.Configuration.Config) : TimeSpan =
        if config.HasPath idleTimeKey then
            // A bare number is seconds, as for `akka.fcqrs.command-timeout`.
            match config.GetString idleTimeKey |> Int32.TryParse with
            | true, seconds -> TimeSpan.FromSeconds(float seconds)
            | _ -> config.GetTimeSpan(idleTimeKey, Nullable(TimeSpan.FromSeconds 5.0))
        else
            TimeSpan.FromSeconds 5.0

    let private checkId (id: string) =
        if String.IsNullOrWhiteSpace id then
            invalidArg (nameof id) "A workflow ID must not be blank."

        if id.Contains '~' then
            invalidArg (nameof id) $"Workflow ID '{id}' contains '~', which FCQRS uses to separate the parts of saga names and topics."

        if id.Contains '#' then
            invalidArg (nameof id) $"Workflow ID '{id}' contains '#', which FCQRS uses to name a workflow's later runs."

    /// Registers the workflow and returns its handle. Instances that were running when the
    /// process last stopped resume as soon as this is called.
    let register (api: IActor) (definition: WorkflowDefinition<'Input, 'Result>) : WorkflowHandle<'Input, 'Result> =
        let erased: WorkflowActor.Definition =
            { Name = definition.Name
              IdleTime = idleTimeOf api.System.Settings.Config
              EncodeResult = Payload.encode typeof<'Result>
              Body =
                fun input ->
                    WorkflowCore.bind (definition.Body(Payload.toValue<'Input> input)) (fun result -> Return(box result)) }

        // Remembered, so a running instance restarts with the process. That also turns Akka's idle
        // passivation off: an instance asks to leave memory itself, when it ends.
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

        let startWith (reuse: WorkflowIdReuse) (id: string) (input: 'Input) =
            async {
                let start =
                    { Input = Payload.ofValue input
                      Traceparent = currentTraceparent ()
                      Link = None
                      Parent = None
                      Reuse = reuse }

                let! reply = ask id start requestTimeout

                match reply with
                | :? StartReply as reply when reply.Created -> return StartResult.Started
                | :? StartReply as reply -> return StartResult.AlreadyStarted reply.Status
                | other -> return invalidOp $"Unexpected reply to a workflow start: {other}"
            }

        { Name = definition.Name
          Start = startWith WorkflowIdReuse.Reject
          StartWith = startWith
          Status =
            fun id ->
                async {
                    let! reply = ask id GetStatus requestTimeout
                    return reply :?> WorkflowStatus
                }
          Result =
            fun id timeout ->
                async {
                    let! reply = ask id (AwaitResult timeout) timeout

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
                }
          Resume =
            fun id ->
                async {
                    let! reply = ask id ResumeWorkflow requestTimeout

                    match reply with
                    | :? ResumeReply as reply ->
                        match reply with
                        | ResumeAccepted -> return ()
                        | ResumeRefused reason -> return invalidOp reason
                    | other -> return invalidOp $"Unexpected reply to a workflow resume request: {other}"
                } }

    /// Stores the signal `name` with a typed payload for the instance `id`.
    let signal (handle: WorkflowHandle<'Input, 'Result>) (id: string) (name: string) (payload: 'Signal) : Async<unit> =
        handle.Signal id name (Payload.ofValue payload)
