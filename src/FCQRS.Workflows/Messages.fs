namespace FCQRS.Workflows

open FCQRS.Common

/// The names to register with a telemetry pipeline, for example OpenTelemetry's
/// `tracing.AddSource(WorkflowTelemetry.ActivitySourceName)` and
/// `metrics.AddMeter(WorkflowTelemetry.MeterName)`.
/// A workflow logs through the `ILoggerFactory` given to the actor system: its progress in the
/// `Telemetry.MessageFlowCategory` category at Information, and failed steps, faulted workflows,
/// and parked instances in the `FCQRS.Workflows` category.
type WorkflowTelemetry =
    /// The source of workflow spans. It is also in `Telemetry.AllActivitySources`.
    static member ActivitySourceName: string = Telemetry.WorkflowActivitySourceName
    /// The meter of the workflow instruments:
    /// `fcqrs.workflow.started`, `.finished`, `.faulted`, `.cancelled` (counters),
    /// `fcqrs.workflow.running`, `.parked` (instances in memory in that state),
    /// `fcqrs.workflow.step.duration` (milliseconds from a step's stored start to its end),
    /// `fcqrs.workflow.activity.retries` (attempts that failed and were retried),
    /// `fcqrs.workflow.recovery.duration` (milliseconds) and `fcqrs.workflow.recovery.records`.
    static member MeterName: string = "FCQRS.Workflow"

/// Where a workflow instance is.
[<RequireQualifiedAccess>]
type WorkflowStatus =
    /// No workflow with this ID has been started.
    | NotStarted
    /// The body is waiting on these steps, given as "path name".
    | Running of pending: string list
    /// The journal no longer matches the workflow's code. The instance runs nothing until
    /// matching code is deployed.
    | Parked of reason: string
    /// The body returned.
    | Completed
    /// The body threw an exception it did not handle.
    | Failed of StepError
    /// The workflow was cancelled.
    | Cancelled

    interface ISerializable

    /// Whether the body is waiting on a step with this name, such as a signal wait, whose step is
    /// named after its signal. `false` for an instance that is not running.
    member this.IsWaitingFor(step: string) : bool =
        match this with
        | Running pending ->
            // Each entry is "path name". A path holds no space, so the name follows the first one.
            pending
            |> List.exists (fun entry ->
                let space = entry.IndexOf ' '
                space >= 0 && entry.Substring(space + 1) = step)
        | _ -> false

/// What a start does when an instance with its ID has ended. A running or parked instance is
/// never replaced: the start stores nothing and reports it.
[<RequireQualifiedAccess>]
type WorkflowIdReuse =
    /// An ended instance is final: the start stores nothing. The default.
    | Reject
    /// An instance that faulted or was cancelled starts a new run under its ID, with the new input.
    /// A finished instance is final. Run 2 and later give their steps idempotency keys, command
    /// IDs, and child IDs under `<ID>#<run>`, so an external system does not take the new run's
    /// calls for repeats of the old run's.
    | AfterFailure

    interface ISerializable

/// The answer to a start request.
[<RequireQualifiedAccess>]
type StartResult =
    /// This request started the workflow, or a new run of it.
    | Started
    /// A workflow with this ID exists and the start's reuse policy keeps it. Nothing was stored.
    | AlreadyStarted of WorkflowStatus

/// How a workflow ended.
[<RequireQualifiedAccess>]
type WorkflowOutcome<'Result> =
    /// The body returned this value.
    | Finished of 'Result
    /// The body threw an exception it did not handle.
    | Faulted of StepError
    /// The workflow was cancelled.
    | Cancelled

/// The messages a workflow instance exchanges. Those that can cross nodes are serializable;
/// an attempt's outcome holds a value or an exception and never leaves its node.
module internal Protocol =

    type StartWorkflow =
        { Input: Payload
          /// The trace the instance joins.
          Traceparent: string option
          /// With no `Traceparent`, the span that the first span of the instance's own trace
          /// links to: the step of a parent that started it as a detached child.
          Link: string option
          Parent: string option
          /// What the start does when the instance has ended.
          Reuse: WorkflowIdReuse }

        interface ISerializable

    type StartReply =
        { Created: bool
          Status: WorkflowStatus }

        interface ISerializable

    type GetStatus =
        | GetStatus

        interface ISerializable

    /// Asks for the instance's result, given once it ends. The asker waits for the reply at most
    /// `wait`, and the instance drops the request after that.
    type AwaitResult =
        | AwaitResult of wait: System.TimeSpan

        interface ISerializable

    type ResultReply =
        | ResultFinished of Payload
        | ResultFaulted of StepError
        | ResultCancelled
        | ResultNotStarted

        interface ISerializable

    type SendSignal =
        { SignalName: string
          SignalPayload: Payload
          SignalTraceparent: string option }

        interface ISerializable

    type SignalReply =
        | SignalAccepted
        | SignalRefused of WorkflowStatus

        interface ISerializable

    type CancelWorkflow =
        | CancelWorkflow

        interface ISerializable

    type CancelReply =
        | CancelAccepted
        | CancelRefused of WorkflowStatus

        interface ISerializable

    type ResumeWorkflow =
        | ResumeWorkflow

        interface ISerializable

    type ResumeReply =
        | ResumeAccepted
        | ResumeRefused of reason: string

        interface ISerializable

    /// Asks an instance what it holds in memory. Tests use it.
    type Inspect =
        | Inspect

        interface ISerializable

    type Inspection =
        { /// Result requests that wait for the instance to end.
          Waiters: int
          /// Timers armed for the instance's steps.
          Timers: int }

        interface ISerializable

    /// Makes the shard recover an instance. The instance ignores it.
    type Wake =
        | Wake

        interface ISerializable

    type TimerFired =
        { TimerPath: string
          TimerEpoch: int64 }

        interface ISerializable

    type RetryDue =
        { RetryPath: string
          NextAttempt: int
          RetryEpoch: int64 }

        interface ISerializable

    type DeadlinePassed =
        { DeadlinePath: string
          DeadlineEpoch: int64 }

        interface ISerializable

    /// An instance that is not running got no request for its idle time.
    type IdleElapsed =
        { IdleEpoch: int64 }

        interface ISerializable

    type AttemptDone =
        { AttemptPath: string
          Attempt: int
          AttemptEpoch: int64
          Outcome: Result<objnull, exn> }

        interface Akka.Actor.INoSerializationVerificationNeeded
