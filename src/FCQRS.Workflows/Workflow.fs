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

/// A child workflow that was started. Give it to `awaitChild` to wait for its result.
type ChildHandle<'Result> =
    { /// The child workflow's name.
      Workflow: string
      /// The child instance's ID: the parent's ID, a colon, and the path of the step that started it.
      Id: string }

/// Raised inside an attempt when a child workflow ended without a result.
type internal ChildEndedException(message: string) =
    inherit Exception(message)

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

    /// What one attempt of an activity or a command receives.
    type AttemptEnv =
        { Api: IActor
          Context: AttemptContext
          WorkflowName: string
          WorkflowId: string
          Path: string }

    type LeafOp =
        /// An external call or an aggregate command, attempted under a retry policy.
        | ActivityOp of policy: RetryPolicy * run: (AttemptEnv -> Task<objnull>)
        | TimerOp of TimerDue
        /// Produces a value from the current time. Stored when produced.
        | ValueOp of (DateTime -> objnull)
        /// Waits for the named signal, at most for the given time.
        | SignalOp of timeout: TimeSpan option

    type Leaf =
        { Name: string
          Kind: StepKind
          Encode: objnull -> Payload
          Decode: Payload -> objnull
          Op: LeafOp
          /// Runs when the workflow is cancelled while this step is pending.
          OnCancel: (IActor -> unit) option }

/// A workflow that produces a `'T`. It is a description: a workflow instance interprets it,
/// stores each step's result, and on recovery feeds the stored results back in.
[<NoEquality; NoComparison>]
type Workflow<'T> =
    internal
    | Return of 'T
    | Fail of exn
    | Lazy of (unit -> Workflow<'T>)
    | Step of Node * (Result<objnull, exn> -> Workflow<'T>)

/// What a step waits on.
and [<NoEquality; NoComparison>] internal Node =
    | Leaf of Leaf
    /// Every branch finishes; at most `limit` run at once. The result is an `objnull list`.
    | AllOf of branches: Workflow<objnull> list * limit: int option
    /// The first branch to finish decides the result.
    | RaceOf of branches: Workflow<objnull> list

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

    let unboxed<'T> (result: Result<objnull, exn>) : Workflow<'T> =
        match result with
        | Ok value -> Return(unbox<'T> value)
        | Error error -> Fail error

    let step<'T> (name: string) (kind: StepKind) (op: LeafOp) : Workflow<'T> =
        Step(
            Leaf
                { Name = name
                  Kind = kind
                  Encode = Payload.encode typeof<'T>
                  Decode = Payload.decode typeof<'T>
                  Op = op
                  OnCancel = None },
            unboxed<'T>
        )

    let boxed (workflow: Workflow<'T>) : Workflow<objnull> = bind workflow (fun value -> Return(box value))

    let allOf (limit: int option) (branches: Workflow<'T> list) : Workflow<'T list> =
        Step(
            AllOf(branches |> List.map boxed, limit),
            fun result ->
                match result with
                | Ok values -> Return(unbox<objnull list> values |> List.map unbox<'T>)
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

    /// `let! a = first and! b = second` runs both together.
    member _.MergeSources(first: Workflow<'A>, second: Workflow<'B>) : Workflow<'A * 'B> =
        WorkflowCore.bind (WorkflowCore.allOf None [ WorkflowCore.boxed first; WorkflowCore.boxed second ]) (fun values ->
            match values with
            | [ a; b ] -> Return(unbox<'A> a, unbox<'B> b)
            | _ -> Fail(InvalidOperationException "Two branches give two results."))

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

        let boxed (attempt: AttemptEnv) : Task<objnull> =
            task {
                let! value = run attempt.Context
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

    /// Runs the branches together and gives every result, in branch order. If a branch fails,
    /// the others are abandoned and the failure is raised here.
    /// Every branch result is stored in this workflow's journal, so use a child workflow per
    /// item for a fan-out over thousands of items.
    let all (branches: Workflow<'T> list) : Workflow<'T list> = WorkflowCore.allOf None branches

    /// Runs `body` for every item, at most `limit` at a time, and gives the results in item order.
    let allAtMost (limit: int) (items: 'A list) (body: 'A -> Workflow<'T>) : Workflow<'T list> =
        if limit < 1 then
            invalidArg (nameof limit) "At least one branch must be allowed to run."

        WorkflowCore.allOf (Some limit) (items |> List.map (fun item -> Lazy(fun () -> body item)))

    /// Runs the branches together. The first to finish decides the result, with its value or
    /// its exception, and the others are abandoned. The winner is stored, so replay selects
    /// the same branch.
    let race (branches: Workflow<'T> list) : Workflow<'T> =
        if List.isEmpty branches then
            invalidArg (nameof branches) "A race needs at least one branch."

        Step(RaceOf(branches |> List.map WorkflowCore.boxed), WorkflowCore.unboxed<'T>)

    /// Waits for a signal named `name` and gives its payload. A signal that arrived before the
    /// workflow reached this step was stored and is consumed here; signals with one name are
    /// consumed oldest first.
    let waitSignal<'T> (name: string) : Workflow<'T> =
        WorkflowCore.step<'T> name StepKind.Signal (SignalOp None)

    /// Waits for a signal named `name` for at most `timeout`, measured from the stored start of
    /// the step. Gives `None` when the time passes first; a signal that arrives later stays
    /// stored for the next wait.
    let waitSignalFor<'T> (name: string) (timeout: TimeSpan) : Workflow<'T option> =
        Step(
            Leaf
                { Name = name
                  Kind = StepKind.Signal
                  Encode = Payload.encode typeof<'T>
                  Decode =
                    fun payload ->
                        if payload = Payload.timedOut then
                            box (None: 'T option)
                        else
                            box (Some(Payload.toValue<'T> payload))
                  Op = SignalOp(Some timeout)
                  OnCancel = None },
            WorkflowCore.unboxed<'T option>
        )

    let private commandPolicy: RetryPolicy =
        { Schedule = Backoff(TimeSpan.FromSeconds 1.0, 2.0, TimeSpan.FromMinutes 1.0)
          MaxAttempts = None
          Deadline = Some(TimeSpan.FromMinutes 5.0)
          AttemptTimeout = None
          ShouldRetry = fun error -> error :? TimeoutException }

    let private correlationOf (key: string) : string =
        let hash = Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes key)
        Guid(ReadOnlySpan(hash, 0, 16)).ToString()

    /// Sends a command to an aggregate under `policy` and gives the reply's event.
    /// The workflow subscribes to the aggregate's replies before it sends. Every resend uses the
    /// same correlation ID, and after a restart the command is sent again unless its reply was
    /// stored, so the aggregate must answer a repeated command with the same verdict.
    /// When the policy runs out, the step fails as `OutcomeUnknown`: the command may have been
    /// applied.
    let sendWith
        (policy: RetryPolicy)
        (aggregate: FCQRS.FSharp.AggregateHandle<'Command, 'Event>)
        (aggregateId: string)
        (command: 'Command)
        : Workflow<'Event> =
        let run (attempt: AttemptEnv) : Task<objnull> =
            let reply =
                attempt.Api.CreateCommandSubscription
                    aggregate.Factory
                    (FCQRS.FSharp.Fcqrs.cid (correlationOf attempt.Context.IdempotencyKey))
                    (FCQRS.FSharp.Fcqrs.aggregateId aggregateId)
                    command
                    (fun (_: 'Event) -> true)
                    None

            task {
                let! event = Async.StartAsTask(reply, cancellationToken = attempt.Context.Cancellation)
                return box event.EventDetails
            }

        WorkflowCore.step<'Event> "send" StepKind.Command (ActivityOp(policy, run))

    /// `sendWith` with a policy that resends on a timeout, with backoff, for five minutes.
    let send (aggregate: FCQRS.FSharp.AggregateHandle<'Command, 'Event>) (aggregateId: string) (command: 'Command) : Workflow<'Event> =
        sendWith commandPolicy aggregate aggregateId command

    /// A value that records which version of the workflow's code an instance runs, for changing
    /// a workflow while instances are running:
    ///
    ///     let! v = version "add-fraud-check" 2
    ///     if v >= 2 then ... new steps ... else ... the steps as they were ...
    ///
    /// A new instance stores `latest` and gets it. An instance that had already passed this
    /// point when the change was deployed gets 1 and stores nothing. The step takes no place
    /// in the step sequence, so adding it does not move the steps after it.
    let version (changeId: string) (latest: int) : Workflow<int> =
        WorkflowCore.step<int> changeId StepKind.Version (ValueOp(fun _ -> box latest))
