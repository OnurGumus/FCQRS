namespace FCQRS.Workflows

open System
open System.Threading.Tasks
open Akka.Actor
open Akka.Cluster.Sharding
open FCQRS.Common
open FCQRS.Workflows.Protocol

/// Steps that start other workflows.
[<AutoOpen>]
module ChildSteps =

    let private requestTimeout = TimeSpan.FromSeconds 30.0
    /// How long one request for a child's result waits before the parent asks again.
    let private resultTimeout = TimeSpan.FromMinutes 1.0

    /// How many attempts wait for the child's workflow to be registered. A parent's running
    /// instances resume as soon as the parent is registered, which can be before its children are.
    let private registrationAttempts = 3

    let private regionOf (api: IActor) (workflow: string) (attempt: int) : IActorRef =
        try
            ClusterSharding.Get(api.System).ShardRegion workflow
        with error ->
            let message = $"Workflow '{workflow}' is not registered with this actor system, so it cannot run as a child."

            if attempt <= registrationAttempts then
                raise (TimeoutException(message, error))
            else
                raise (InvalidOperationException(message, error))

    let private envelope (id: string) (message: obj) : Akkling.Cluster.Sharding.ShardEnvelope =
        { ShardId = DEFAULT_SHARD
          EntityId = id
          Message = message }

    let private startPolicy: RetryPolicy =
        { Schedule = Backoff(TimeSpan.FromSeconds 1.0, 2.0, TimeSpan.FromMinutes 1.0)
          MaxAttempts = None
          Deadline = Some(TimeSpan.FromMinutes 5.0)
          AttemptTimeout = None
          ShouldRetry = fun error -> not (error :? InvalidOperationException) }

    /// Asks again for as long as the child runs. Only the child's own end stops it.
    let private awaitPolicy: RetryPolicy =
        { Schedule = FixedInterval(TimeSpan.FromSeconds 1.0)
          MaxAttempts = None
          Deadline = None
          AttemptTimeout = None
          ShouldRetry = fun error -> not (error :? ChildEndedException || error :? InvalidOperationException) }

    /// Starts another workflow and gives its handle once the child's start is stored.
    /// The child's ID is this workflow's ID, a colon, and this step's path, so a repeated start
    /// after a crash reaches the same child and does not start a second one.
    /// A child that is never awaited runs on by itself and outlives this workflow.
    /// The child's workflow must be registered with `Workflows.register` in the same process.
    let startChild (definition: WorkflowDefinition<'Input, 'Result>) (input: 'Input) : Workflow<ChildHandle<'Result>> =
        let run (attempt: AttemptEnv) : Task<objnull> =
            task {
                let childId = $"{attempt.WorkflowId}:{attempt.Path}"

                let start =
                    { Input = Payload.ofValue input
                      Traceparent = currentTraceparent ()
                      Parent = Some $"{attempt.WorkflowName}/{attempt.WorkflowId}" }

                let! reply = (regionOf attempt.Api definition.Name attempt.Context.Attempt).Ask<obj>(envelope childId start, requestTimeout, attempt.Context.Cancellation)

                match reply with
                | :? StartReply ->
                    let handle: ChildHandle<'Result> =
                        { Workflow = definition.Name
                          Id = childId }

                    return box handle
                | other -> return invalidOp $"Unexpected reply to a child workflow start: {other}"
            }

        WorkflowCore.step<ChildHandle<'Result>> definition.Name StepKind.Child (ActivityOp(startPolicy, run))

    /// Waits for a child workflow to end and gives its result. A child that faulted or was
    /// cancelled fails this step with `ChildFaulted`. If this workflow is cancelled while it
    /// waits, the child is cancelled too.
    let awaitChild (handle: ChildHandle<'Result>) : Workflow<'Result> =
        let run (attempt: AttemptEnv) : Task<objnull> =
            task {
                let! reply =
                    (regionOf attempt.Api handle.Workflow attempt.Context.Attempt).Ask<obj>(envelope handle.Id AwaitResult, resultTimeout, attempt.Context.Cancellation)

                match reply with
                | :? ResultReply as reply ->
                    match reply with
                    | ResultFinished result -> return box (Payload.toValue<'Result> result)
                    | ResultFaulted error ->
                        return raise (ChildEndedException $"Child workflow {handle.Workflow} '{handle.Id}' faulted ({error.Kind}): {error.Message}")
                    | ResultCancelled -> return raise (ChildEndedException $"Child workflow {handle.Workflow} '{handle.Id}' was cancelled.")
                    | ResultNotStarted -> return raise (ChildEndedException $"Child workflow {handle.Workflow} '{handle.Id}' was never started.")
                | other -> return invalidOp $"Unexpected reply to a child workflow result request: {other}"
            }

        Step(
            Leaf
                { Name = handle.Workflow
                  Kind = StepKind.Child
                  Encode = Payload.encode typeof<'Result>
                  Decode = Payload.decode typeof<'Result>
                  Op = ActivityOp(awaitPolicy, run)
                  OnCancel = Some(fun api -> (regionOf api handle.Workflow Int32.MaxValue).Tell(envelope handle.Id CancelWorkflow, ActorRefs.NoSender)) },
            WorkflowCore.unboxed<'Result>
        )

    /// Starts a child workflow and waits for its result.
    let child (definition: WorkflowDefinition<'Input, 'Result>) (input: 'Input) : Workflow<'Result> =
        WorkflowCore.bind (startChild definition input) awaitChild
