namespace FCQRS.Workflows

open FCQRS.Common

/// The names to register with a telemetry pipeline, for example OpenTelemetry's
/// `tracing.AddSource(WorkflowTelemetry.ActivitySourceName)` and
/// `metrics.AddMeter(WorkflowTelemetry.MeterName)`.
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

/// The answer to a start request.
[<RequireQualifiedAccess>]
type StartResult =
    /// This request started the workflow.
    | Started
    /// A workflow with this ID already exists. Nothing was stored.
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
          Traceparent: string option
          Parent: string option }

        interface ISerializable

    type StartReply =
        { Created: bool
          Status: WorkflowStatus }

        interface ISerializable

    type GetStatus =
        | GetStatus

        interface ISerializable

    type AwaitResult =
        | AwaitResult

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

    type AttemptDone =
        { AttemptPath: string
          Attempt: int
          AttemptEpoch: int64
          Outcome: Result<objnull, exn> }

        interface Akka.Actor.INoSerializationVerificationNeeded
