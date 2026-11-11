namespace FCQRS.Workflows

open System
open System.Text.Json
open FCQRS.Common
open FCQRS.Serialization

/// A stored value: the journal name of its type, and its JSON.
type Payload = { Type: string; Json: string }

/// What a step does. Stored with every step record, and checked on replay.
[<RequireQualifiedAccess>]
type StepKind =
    | Activity
    | Timer
    | Value
    | Signal
    | Command
    | Child
    | Race
    | Version

/// Why a step or a workflow ended without a value.
[<RequireQualifiedAccess>]
type ErrorKind =
    /// The retry policy's attempts ran out.
    | Exhausted
    /// The retry filter refused to retry the exception.
    | NotRetryable
    /// The step's deadline passed. For an activity, a call may still be running.
    | DeadlineExceeded
    /// A command's deadline passed with no reply. The command may have been applied.
    | OutcomeUnknown
    /// An awaited child workflow faulted.
    | ChildFaulted
    /// The workflow was cancelled while the step was pending.
    | Cancelled
    /// The workflow body threw an exception it did not handle.
    | BodyFailed

/// A stored failure. It holds no stack trace.
type StepError =
    { Kind: ErrorKind
      ExceptionType: string
      Message: string }

/// The records a workflow instance stores. Their shape is a persistent contract.
[<RequireQualifiedAccess>]
type WorkflowRecord =
    | Started of input: Payload * at: DateTime * traceparent: string option * parent: string option
    | StepScheduled of path: string * name: string * kind: StepKind * at: DateTime * detail: string option
    | StepCompleted of path: string * name: string * kind: StepKind * result: Payload * at: DateTime
    | StepFailed of path: string * name: string * kind: StepKind * error: StepError * at: DateTime
    | SignalReceived of name: string * payload: Payload * at: DateTime * traceparent: string option
    | CancelRequested of at: DateTime
    | Finished of result: Payload * at: DateTime
    | Faulted of error: StepError * at: DateTime
    | Cancelled of at: DateTime

    interface ISerializable

/// Raised in a workflow body when a step ended without a value. The body sees this exception
/// both when the step fails and when a stored failure is replayed, never the original exception.
type WorkflowStepFailedException(path: string, name: string, error: StepError) =
    inherit Exception($"Workflow step '{name}' at {path} failed ({error.Kind}): {error.Message}")
    /// The step's path in the workflow.
    member _.Path = path
    /// The step's name.
    member _.StepName = name
    /// The stored failure.
    member _.Error = error

/// Converts step values to and from their stored form.
module Payload =

    [<Literal>]
    let private UnitName = "unit"

    let private nameOf (t: Type) =
        match JournalTypes.TryGetName t with
        | Some name -> name
        | None -> t.FullName |> Unchecked.nonNull

    /// Stores `value` as the type `t`.
    let encode (t: Type) (value: obj | null) : Payload =
        if t = typeof<unit> then
            { Type = UnitName; Json = "null" }
        else
            { Type = nameOf t
              Json = JsonSerializer.Serialize(value, t, Serialization.jsonOptions) }

    /// Reads a stored value as the type `t`.
    let decode (t: Type) (payload: Payload) : obj | null =
        if t = typeof<unit> then
            box ()
        else
            JsonSerializer.Deserialize(payload.Json, t, Serialization.jsonOptions)

    /// Stores a typed value.
    let ofValue<'T> (value: 'T) : Payload = encode typeof<'T> (box value)

    /// Reads a typed value.
    let toValue<'T> (payload: Payload) : 'T = decode typeof<'T> payload |> unbox<'T>

    /// Describes an exception for storage.
    let internal errorOf (kind: ErrorKind) (error: exn) : StepError =
        { Kind = kind
          ExceptionType = error.GetType().FullName |> Unchecked.nonNull
          Message = error.Message }
