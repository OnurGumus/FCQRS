namespace FCQRS.Workflows

open System
open System.Threading
open System.Threading.Tasks
open FCQRS.Common

/// How an activity is retried.
type RetryPolicy =
    { /// The wait between attempts.
      Schedule: RetrySchedule
      /// The most attempts to make. `None` sets no limit, and then `Deadline` is required.
      MaxAttempts: int option
      /// The time allowed for the whole step, measured from the stored start of the step.
      /// A restart does not extend it.
      Deadline: TimeSpan option
      /// The time allowed for one attempt.
      AttemptTimeout: TimeSpan option
      /// Whether an exception is worth another attempt. `false` fails the step at once.
      ShouldRetry: exn -> bool }

/// Common retry policies.
module Retry =

    let private backoff = Backoff(TimeSpan.FromSeconds 1.0, 2.0, TimeSpan.FromMinutes 1.0)

    /// One attempt.
    let none: RetryPolicy =
        { Schedule = backoff
          MaxAttempts = Some 1
          Deadline = None
          AttemptTimeout = None
          ShouldRetry = fun _ -> true }

    /// At most `attempts` attempts, with backoff from one second to one minute.
    let times (attempts: int) : RetryPolicy = { none with MaxAttempts = Some attempts }

    /// Retries with backoff until `deadline` has passed since the step was first reached.
    let forever (deadline: TimeSpan) : RetryPolicy =
        { none with
            MaxAttempts = None
            Deadline = Some deadline }

/// What an activity receives for one attempt.
type AttemptContext =
    { /// The same for every attempt of this step, across restarts: workflow name, workflow ID,
      /// and step path. Pass it to the external system so a repeated call is recognised.
      IdempotencyKey: string
      /// 1 for the first attempt since the process last started this step.
      Attempt: int
      /// Cancelled when the attempt times out, the step's deadline passes, or the workflow stops.
      Cancellation: CancellationToken }

[<AutoOpen>]
module internal Steps =

    type TimerDue =
        | After of TimeSpan
        | At of DateTime

    type LeafOp =
        | ActivityOp of policy: RetryPolicy * run: (AttemptContext -> Task<objnull>)
        | TimerOp of TimerDue
        /// Produces a value from the current time. Stored when produced.
        | ValueOp of (DateTime -> objnull)

    type Leaf =
        { Name: string
          Kind: StepKind
          ResultType: Type
          Op: LeafOp }

/// A workflow that produces a `'T`. It is a description: a workflow instance interprets it,
/// stores each step's result, and on recovery feeds the stored results back in.
[<NoEquality; NoComparison>]
type Workflow<'T> =
    internal
    | Return of 'T
    | Fail of exn
    | Lazy of (unit -> Workflow<'T>)
    | Step of Leaf * (Result<objnull, exn> -> Workflow<'T>)

module internal WorkflowCore =

    let protect (f: unit -> Workflow<'T>) : Workflow<'T> =
        try
            f ()
        with error ->
            Fail error

    let rec force (workflow: Workflow<'T>) : Workflow<'T> =
        match workflow with
        | Lazy f -> force (protect f)
        | other -> other

    let rec bind (workflow: Workflow<'A>) (next: 'A -> Workflow<'B>) : Workflow<'B> =
        match workflow with
        | Return value -> protect (fun () -> next value)
        | Fail error -> Fail error
        | Lazy f -> Lazy(fun () -> bind (protect f) next)
        | Step(leaf, k) -> Step(leaf, fun result -> bind (protect (fun () -> k result)) next)

    let rec tryWith (workflow: Workflow<'T>) (handler: exn -> Workflow<'T>) : Workflow<'T> =
        match workflow with
        | Return value -> Return value
        | Fail error -> protect (fun () -> handler error)
        | Lazy f -> Lazy(fun () -> tryWith (protect f) handler)
        | Step(leaf, k) -> Step(leaf, fun result -> tryWith (protect (fun () -> k result)) handler)

    let rec tryFinally (workflow: Workflow<'T>) (compensation: unit -> unit) : Workflow<'T> =
        let finish (result: Workflow<'T>) =
            try
                compensation ()
                result
            with error ->
                Fail error

        match workflow with
        | Return value -> finish (Return value)
        | Fail error -> finish (Fail error)
        | Lazy f -> Lazy(fun () -> tryFinally (protect f) compensation)
        | Step(leaf, k) -> Step(leaf, fun result -> tryFinally (protect (fun () -> k result)) compensation)

    /// Runs `body` while `guard` holds. Iterations that reach no step run in a loop, so a long
    /// loop of plain code does not grow the stack.
    let rec whileLoop (guard: unit -> bool) (body: Workflow<unit>) : Workflow<unit> =
        Lazy(fun () ->
            let mutable result: Workflow<unit> voption = ValueNone

            while result.IsNone do
                if guard () then
                    match force body with
                    | Return() -> ()
                    | other -> result <- ValueSome(bind other (fun () -> whileLoop guard body))
                else
                    result <- ValueSome(Return())

            result.Value)

    let step<'T> (name: string) (kind: StepKind) (op: LeafOp) : Workflow<'T> =
        Step(
            { Name = name
              Kind = kind
              ResultType = typeof<'T>
              Op = op },
            fun result ->
                match result with
                | Ok value -> Return(unbox<'T> value)
                | Error error -> Fail error
        )

/// Builds a workflow. Every `let!` and `do!` is a step the workflow instance controls.
type WorkflowBuilder() =
    member _.Return(value: 'T) : Workflow<'T> = Return value
    member _.ReturnFrom(workflow: Workflow<'T>) : Workflow<'T> = workflow
    member _.Zero() : Workflow<unit> = Return()
    member _.Bind(workflow: Workflow<'A>, next: 'A -> Workflow<'B>) : Workflow<'B> = WorkflowCore.bind workflow next
    member _.Delay(f: unit -> Workflow<'T>) : Workflow<'T> = Lazy f

    member _.Combine(first: Workflow<unit>, second: Workflow<'T>) : Workflow<'T> =
        WorkflowCore.bind first (fun () -> second)

    member _.TryWith(body: Workflow<'T>, handler: exn -> Workflow<'T>) : Workflow<'T> =
        WorkflowCore.tryWith body handler

    member _.TryFinally(body: Workflow<'T>, compensation: unit -> unit) : Workflow<'T> =
        WorkflowCore.tryFinally body compensation

    member _.While(guard: unit -> bool, body: Workflow<unit>) : Workflow<unit> = WorkflowCore.whileLoop guard body

    member _.Using(resource: 'R :> IDisposable, body: 'R -> Workflow<'T>) : Workflow<'T> =
        WorkflowCore.tryFinally (WorkflowCore.protect (fun () -> body resource)) (fun () ->
            match box resource with
            | null -> ()
            | _ -> resource.Dispose())

    member this.For(items: seq<'A>, body: 'A -> Workflow<unit>) : Workflow<unit> =
        Lazy(fun () ->
            let enumerator = items.GetEnumerator()

            WorkflowCore.tryFinally
                (WorkflowCore.whileLoop enumerator.MoveNext (Lazy(fun () -> body enumerator.Current)))
                enumerator.Dispose)

/// The workflow builder and the steps a workflow can take.
[<AutoOpen>]
module WorkflowSteps =

    /// Builds a workflow: `workflow { let! x = activity ... }`.
    let workflow = WorkflowBuilder()

    /// Calls an external system. The call runs at least once: a crash after the call and before
    /// its result is stored repeats it, so pass `IdempotencyKey` to the external system.
    /// `name` is stored and must not change while instances are running.
    let activity (name: string) (policy: RetryPolicy) (run: AttemptContext -> Task<'T>) : Workflow<'T> =
        if policy.MaxAttempts.IsNone && policy.Deadline.IsNone then
            invalidArg (nameof policy) $"Activity '{name}' has no attempt limit, so it needs a deadline."

        let boxed (context: AttemptContext) : Task<objnull> =
            task {
                let! value = run context
                return box value
            }

        WorkflowCore.step<'T> name StepKind.Activity (ActivityOp(policy, boxed))

    /// `activity` for a function that returns an `Async`.
    let activityAsync (name: string) (policy: RetryPolicy) (run: AttemptContext -> Async<'T>) : Workflow<'T> =
        activity name policy (fun context -> Async.StartAsTask(run context, cancellationToken = context.Cancellation))

    /// Waits for `duration`. The due time is stored when the step is first reached, so a restart
    /// does not postpone it.
    let sleep (duration: TimeSpan) : Workflow<unit> =
        WorkflowCore.step<unit> "sleep" StepKind.Timer (TimerOp(After duration))

    /// Waits until `instant` (UTC).
    let until (instant: DateTime) : Workflow<unit> =
        WorkflowCore.step<unit> "until" StepKind.Timer (TimerOp(At(instant.ToUniversalTime())))

    /// The current UTC time, stored so that replay returns the same value.
    let now: Workflow<DateTime> =
        WorkflowCore.step<DateTime> "now" StepKind.Value (ValueOp(fun time -> box time))

    /// A new GUID, stored so that replay returns the same value.
    let newGuid: Workflow<Guid> =
        WorkflowCore.step<Guid> "newGuid" StepKind.Value (ValueOp(fun _ -> box (Guid.NewGuid())))

    /// A random number in [0, 1), stored so that replay returns the same value.
    let random: Workflow<float> =
        WorkflowCore.step<float> "random" StepKind.Value (ValueOp(fun _ -> box (Random.Shared.NextDouble())))
