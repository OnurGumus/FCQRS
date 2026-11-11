namespace FCQRS.Workflows

open FCQRS.Common

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
