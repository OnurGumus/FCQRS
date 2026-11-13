/// The C# workflow API. A C# workflow is an `async` method that returns `WorkflowTask<T>` and
/// awaits steps from its `WorkflowContext`. It stores the same journal records as an F# workflow.
namespace FCQRS.Workflows.CSharp

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Runtime.ExceptionServices
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open FCQRS.Common
open FCQRS.Workflows

/// What the workflow's async builder accepts at an `await`.
type internal IStepAwaiter =
    /// The step to run.
    abstract Node: Workflow<objnull>
    /// Gives the step's outcome to the awaiting method.
    abstract Complete: Result<objnull, exn> -> unit

/// Awaits a step that gives a value. Only a workflow method can await it.
[<Sealed>]
type StepAwaiter<'T> internal (node: Workflow<objnull>) =
    let mutable outcome: Result<objnull, exn> voption = ValueNone

    /// Always false: a step finishes when the workflow instance has stored its result.
    member _.IsCompleted = false

    /// The step's value, or the exception it failed with.
    member _.GetResult() : 'T =
        match outcome with
        | ValueSome(Ok value) -> unbox<'T> value
        | ValueSome(Error error) ->
            ExceptionDispatchInfo.Throw error
            Unchecked.defaultof<'T>
        | ValueNone -> invalidOp "The step has not finished."

    interface INotifyCompletion with
        member _.OnCompleted(_: Action) =
            raise (InvalidOperationException "A workflow step can only be awaited in a method that returns WorkflowTask.")

    interface IStepAwaiter with
        member _.Node = node
        member _.Complete(result: Result<objnull, exn>) = outcome <- ValueSome result

/// Awaits a step that gives no value.
[<Sealed>]
type StepAwaiter internal (node: Workflow<objnull>) =
    let mutable outcome: Result<objnull, exn> voption = ValueNone

    /// Always false: a step finishes when the workflow instance has stored its result.
    member _.IsCompleted = false

    /// Returns when the step succeeded, or throws the exception it failed with.
    member _.GetResult() : unit =
        match outcome with
        | ValueSome(Ok _) -> ()
        | ValueSome(Error error) -> ExceptionDispatchInfo.Throw error
        | ValueNone -> invalidOp "The step has not finished."

    interface INotifyCompletion with
        member _.OnCompleted(_: Action) =
            raise (InvalidOperationException "A workflow step can only be awaited in a method that returns WorkflowTask.")

    interface IStepAwaiter with
        member _.Node = node
        member _.Complete(result: Result<objnull, exn>) = outcome <- ValueSome result

/// A step that gives a `T` when awaited. It describes the step: awaiting it twice runs two steps.
[<Sealed>]
type Step<'T> internal (workflow: Workflow<'T>) =
    member internal _.Workflow = workflow
    /// Lets a workflow method `await` the step.
    member _.GetAwaiter() = StepAwaiter<'T>(WorkflowCore.boxed workflow)

/// A step that gives no value when awaited.
[<Sealed>]
type Step internal (workflow: Workflow<unit>) =
    member internal _.Workflow = workflow
    /// Lets a workflow method `await` the step.
    member _.GetAwaiter() = StepAwaiter(WorkflowCore.boxed workflow)

/// Where a workflow method is.
type internal TaskState<'T> =
    | Running
    | Awaiting of IStepAwaiter
    | Done of 'T
    | Failed of exn

/// The return type of a C# workflow method: `async WorkflowTask<Decision> Run(...)`.
/// Inside such a method, `await` accepts workflow steps only. Awaiting anything else that has
/// not finished, such as `Task.Delay` or an `HttpClient` call, throws, because the workflow
/// instance could not store its result or replay it. Put such calls in an activity.
[<Sealed; AsyncMethodBuilder(typedefof<WorkflowTaskMethodBuilder<_>>)>]
type WorkflowTask<'T> internal () =
    member val internal State: TaskState<'T> = Running with get, set
    member val internal MoveNext: unit -> unit = ignore with get, set

    /// The method as a workflow: each awaited step, then the rest of the method.
    member internal this.AsWorkflow() : Workflow<'T> =
        match this.State with
        | Done value -> Return value
        | Failed error -> Fail error
        | Running -> Fail(InvalidOperationException "The workflow method did not reach an await or its end.")
        | Awaiting awaiter ->
            let advance (result: Result<objnull, exn>) =
                awaiter.Complete result
                this.State <- Running
                this.MoveNext()

            // An awaited method that reached no step is over already. Continue in a loop, so a
            // long run of such awaits does not grow the stack.
            match WorkflowCore.force awaiter.Node with
            | Return value ->
                advance (Ok value)
                this.AsWorkflow()
            | Fail error ->
                advance (Error error)
                this.AsWorkflow()
            | node ->
                WorkflowCore.bind (WorkflowCore.attempt node) (fun result ->
                    advance result
                    this.AsWorkflow())

    /// Lets one workflow method `await` another. The other method's steps join this workflow.
    member this.GetAwaiter() = StepAwaiter<'T>(WorkflowCore.boxed (this.AsWorkflow()))

/// Builds `WorkflowTask<T>` methods. The C# compiler uses it; application code does not.
and [<Sealed>] WorkflowTaskMethodBuilder<'T>() =
    let task = WorkflowTask<'T>()
    // The compiler's state machine is a struct in Release builds. It is boxed once, at the first
    // await, and every later step continues that boxed copy.
    let mutable machine: IAsyncStateMachine | null = null

    let suspend (awaiter: objnull) (current: unit -> IAsyncStateMachine) =
        match awaiter with
        | :? IStepAwaiter as step ->
            let boxed =
                match machine with
                | null ->
                    let boxed = current ()
                    machine <- boxed
                    boxed
                | boxed -> boxed

            task.State <- Awaiting step
            task.MoveNext <- boxed.MoveNext
        | other ->
            let awaited =
                match other with
                | null -> "null"
                | other -> other.GetType().Name

            raise (
                InvalidOperationException
                    $"A workflow method awaited a {awaited}, which is not a workflow step. The workflow could not store its result or replay it. Call it inside an activity."
            )

    /// Called by the compiler.
    static member Create() = WorkflowTaskMethodBuilder<'T>()

    /// The task the method returns.
    member _.Task = task

    /// Called by the compiler.
    member _.Start<'TStateMachine when 'TStateMachine :> IAsyncStateMachine>(stateMachine: byref<'TStateMachine>) =
        stateMachine.MoveNext()

    /// Called by the compiler.
    member _.SetStateMachine(_: IAsyncStateMachine) = ()

    /// Called by the compiler.
    member _.SetResult(result: 'T) = task.State <- Done result

    /// Called by the compiler.
    member _.SetException(error: exn) = task.State <- Failed error

    /// Called by the compiler.
    member _.AwaitOnCompleted<'TAwaiter, 'TStateMachine when 'TAwaiter :> INotifyCompletion and 'TStateMachine :> IAsyncStateMachine>
        (awaiter: byref<'TAwaiter>, stateMachine: byref<'TStateMachine>)
        =
        let current = stateMachine
        suspend (box awaiter) (fun () -> current :> IAsyncStateMachine)

    /// Called by the compiler.
    member _.AwaitUnsafeOnCompleted<'TAwaiter, 'TStateMachine when 'TAwaiter :> ICriticalNotifyCompletion and 'TStateMachine :> IAsyncStateMachine>
        (awaiter: byref<'TAwaiter>, stateMachine: byref<'TStateMachine>)
        =
        let current = stateMachine
        suspend (box awaiter) (fun () -> current :> IAsyncStateMachine)

/// The outcome of a signal wait that has a timeout.
[<Sealed>]
type SignalOutcome<'T> internal (received: bool, value: 'T) =
    /// Whether a signal arrived before the timeout.
    member _.Received = received
    /// The signal's payload. Read it only when `Received` is true.
    member _.Value = value

/// Changes to a retry policy, for C#. `Retry.none`, `Retry.times(n)` and `Retry.forever(deadline)`
/// give the starting policies.
[<Extension; Sealed; AbstractClass>]
type RetryPolicyExtensions =
    /// The wait between attempts.
    [<Extension>]
    static member WithSchedule(policy: RetryPolicy, schedule: RetrySchedule) : RetryPolicy = { policy with Schedule = schedule }

    /// The most attempts to make.
    [<Extension>]
    static member WithMaxAttempts(policy: RetryPolicy, attempts: int) : RetryPolicy = { policy with MaxAttempts = Some attempts }

    /// The time allowed for the whole step, from its stored start.
    [<Extension>]
    static member WithDeadline(policy: RetryPolicy, deadline: TimeSpan) : RetryPolicy = { policy with Deadline = Some deadline }

    /// The time allowed for one attempt.
    [<Extension>]
    static member WithAttemptTimeout(policy: RetryPolicy, timeout: TimeSpan) : RetryPolicy =
        { policy with
            AttemptTimeout = Some timeout }

    /// Which exceptions are worth another attempt.
    [<Extension>]
    static member RetryWhen(policy: RetryPolicy, shouldRetry: Func<exn, bool>) : RetryPolicy =
        { policy with
            ShouldRetry = shouldRetry.Invoke }

/// The steps a C# workflow method can await. Each method describes a step; nothing runs until
/// the step is awaited.
[<Sealed>]
type WorkflowContext internal () =

    /// Calls an external system. The call runs at least once, so pass the context's
    /// `IdempotencyKey` to the external system. `name` is stored and must not change while
    /// instances are running.
    member _.Activity<'T>(name: string, policy: RetryPolicy, run: Func<AttemptContext, Task<'T>>) : Step<'T> =
        Step<'T>(activity name policy run.Invoke)

    /// Waits for `duration`. The due time is stored, so a restart does not postpone it.
    member _.Sleep(duration: TimeSpan) : Step = Step(sleep duration)

    /// Waits until `instant` (UTC).
    member _.Until(instant: DateTime) : Step = Step(until instant)

    /// The current UTC time, stored so that replay returns the same value.
    member _.Now() : Step<DateTime> = Step<DateTime>(now)

    /// A new GUID, stored so that replay returns the same value.
    member _.NewGuid() : Step<Guid> = Step<Guid>(newGuid)

    /// A random number in [0, 1), stored so that replay returns the same value.
    member _.Random() : Step<float> = Step<float>(random)

    /// Waits for a signal named `name`. Stored signals with one name are consumed oldest first.
    member _.WaitSignal<'T>(name: string) : Step<'T> = Step<'T>(waitSignal<'T> name)

    /// Waits for a signal named `name` for at most `timeout`.
    member _.WaitSignal<'T>(name: string, timeout: TimeSpan) : Step<SignalOutcome<'T>> =
        Step<SignalOutcome<'T>>(
            WorkflowCore.bind (waitSignalFor<'T> name timeout) (fun received ->
                match received with
                | Some value -> Return(SignalOutcome<'T>(true, value))
                | None -> Return(SignalOutcome<'T>(false, Unchecked.defaultof<'T>)))
        )

    /// Sends a command to an aggregate and gives the reply's event. The aggregate must answer a
    /// repeated command with the same verdict. With no reply for five minutes the step fails as
    /// `OutcomeUnknown`.
    member _.Send<'TCommand, 'TEvent when 'TEvent: not null>(aggregate: FCQRS.CSharp.AggregateFactory, aggregateId: string, command: 'TCommand) : Step<'TEvent> =
        Step<'TEvent>(sendTo defaultCommandPolicy aggregate.Invoke aggregateId command)

    /// `Send` under a retry policy of its own.
    member _.Send<'TCommand, 'TEvent when 'TEvent: not null>
        (policy: RetryPolicy, aggregate: FCQRS.CSharp.AggregateFactory, aggregateId: string, command: 'TCommand)
        : Step<'TEvent> =
        Step<'TEvent>(sendTo policy aggregate.Invoke aggregateId command)

    /// Starts the registered workflow named `workflow` as a child and gives its handle once the
    /// child's start is stored. A child that is never awaited outlives this workflow.
    member _.StartChild<'TInput, 'TResult>(workflow: string, input: 'TInput) : Step<ChildHandle<'TResult>> =
        Step<ChildHandle<'TResult>>(startChild (Workflow.define workflow (fun (_: 'TInput) -> Fail(InvalidOperationException "A child is run by its own definition."): Workflow<'TResult>)) input)

    /// Waits for a child workflow to end and gives its result.
    member _.AwaitChild<'TResult>(handle: ChildHandle<'TResult>) : Step<'TResult> = Step<'TResult>(awaitChild handle)

    /// Starts the registered workflow named `workflow` as a child and waits for its result.
    member this.Child<'TInput, 'TResult>(workflow: string, input: 'TInput) : Step<'TResult> =
        Step<'TResult>(WorkflowCore.bind (this.StartChild<'TInput, 'TResult>(workflow, input)).Workflow awaitChild)

    /// Turns a workflow method into a step, for `All` and `Race`: `ctx.Run(() => Reserve(ctx, order))`.
    member _.Run<'T>(method: Func<WorkflowTask<'T>>) : Step<'T> =
        Step<'T>(Lazy(fun () -> method.Invoke().AsWorkflow()))

    /// Runs the steps together and gives every result, in step order.
    member _.All<'T>([<ParamArray>] steps: Step<'T>[]) : Step<IReadOnlyList<'T>> =
        Step<IReadOnlyList<'T>>(
            WorkflowCore.bind (all [ for step in steps -> step.Workflow ]) (fun values -> Return(List.toArray values :> IReadOnlyList<'T>))
        )

    /// Runs two steps together and gives both results, which may have different types:
    /// `var (price, customer) = await ctx.Both(priceStep, customerStep);`. Both results are
    /// stored. If one step fails, the other is abandoned and the failure is raised here. This is
    /// `and!` in F#.
    member _.Both<'A, 'B>(first: Step<'A>, second: Step<'B>) : Step<struct ('A * 'B)> =
        Step<struct ('A * 'B)>(
            WorkflowCore.bind (workflow.MergeSources(first.Workflow, second.Workflow)) (fun (a, b) -> Return(struct (a, b)))
        )

    /// Runs the steps together. The first to finish decides the result, and the others are abandoned.
    member _.Race<'T>([<ParamArray>] steps: Step<'T>[]) : Step<'T> =
        Step<'T>(race [ for step in steps -> step.Workflow ])

    /// The version of the workflow's code this instance runs at this point. See `version` in F#.
    member _.Version(changeId: string, latest: int) : Step<int> = Step<int>(version changeId latest)

/// A C# workflow. Derive from it, give it a name, and write `Run` as an `async` method.
/// The name is stored in the journal and must not change.
[<AbstractClass>]
type Workflow<'TInput, 'TResult>(name: string) =
    /// The workflow's name.
    member _.Name = name
    /// The workflow body. It must be deterministic: take time, GUIDs and random numbers from
    /// the context, and do all I/O in activities.
    abstract Run: context: WorkflowContext * input: 'TInput -> WorkflowTask<'TResult>

/// Raised by `ResultAsync` when the workflow's body threw an exception it did not handle.
type WorkflowFaultedException(workflow: string, id: string, error: StepError) =
    inherit Exception($"Workflow {workflow} '{id}' faulted ({error.Kind}): {error.Message}")
    /// The stored failure.
    member _.Error = error

/// Raised by `ResultAsync` when the workflow was cancelled.
type WorkflowCancelledException(workflow: string, id: string) =
    inherit Exception($"Workflow {workflow} '{id}' was cancelled.")

/// Starts and queries the instances of a registered workflow, from C#.
type IWorkflowClient<'TInput, 'TResult> =
    /// The workflow's name.
    abstract Name: string
    /// Starts the workflow with this ID. True when this call started it; false when a workflow
    /// with this ID already existed, in which case nothing was stored.
    abstract StartAsync: id: string * input: 'TInput -> Task<bool>
    /// Where the instance is.
    abstract StatusAsync: id: string -> Task<WorkflowStatus>
    /// Waits up to `timeout` for the workflow to end and gives its result. Throws
    /// `WorkflowFaultedException`, `WorkflowCancelledException`, or `TimeoutException`.
    abstract ResultAsync: id: string * timeout: TimeSpan -> Task<'TResult>
    /// Stores a signal for the instance. Returns once it is stored.
    abstract SignalAsync<'TSignal> : id: string * name: string * payload: 'TSignal -> Task
    /// Cancels the instance. Returns once the request is stored.
    abstract CancelAsync: id: string -> Task

/// Registers C# workflows.
[<Sealed; AbstractClass>]
type WorkflowHost =

    /// The workflow as a definition F# code can register, start as a child, or put in a stream.
    static member Define<'TInput, 'TResult>(workflow: Workflow<'TInput, 'TResult>) : WorkflowDefinition<'TInput, 'TResult> =
        let context = WorkflowContext()
        // Each run of the body gets a state machine of its own.
        Workflow.define workflow.Name (fun input -> Lazy(fun () -> workflow.Run(context, input).AsWorkflow()))

    /// Registers the workflow with the actor system and returns its client. Instances that were
    /// running when the process last stopped resume as soon as this is called.
    static member Register<'TInput, 'TResult>(actor: IActor, workflow: Workflow<'TInput, 'TResult>) : IWorkflowClient<'TInput, 'TResult> =
        let handle = FCQRS.Workflows.Workflows.register actor (WorkflowHost.Define workflow)

        { new IWorkflowClient<'TInput, 'TResult> with
            member _.Name = handle.Name

            member _.StartAsync(id, input) =
                task {
                    let! result = handle.Start id input
                    return result = StartResult.Started
                }

            member _.StatusAsync(id) = handle.Status id |> Async.StartAsTask

            member _.ResultAsync(id, timeout) =
                task {
                    let! outcome = handle.Result id timeout

                    match outcome with
                    | WorkflowOutcome.Finished result -> return result
                    | WorkflowOutcome.Faulted error -> return raise (WorkflowFaultedException(handle.Name, id, error))
                    | WorkflowOutcome.Cancelled -> return raise (WorkflowCancelledException(handle.Name, id))
                }

            member _.SignalAsync<'TSignal>(id, name, payload: 'TSignal) =
                handle.Signal id name (Payload.ofValue payload) |> Async.StartAsTask :> Task

            member _.CancelAsync(id) = handle.Cancel id |> Async.StartAsTask :> Task }

/// The client of one workflow class. Inject it when two workflows have the same input and result
/// types, where `IWorkflowClient<TInput, TResult>` alone would not say which one is meant.
[<Sealed>]
type WorkflowClient<'TWorkflow, 'TInput, 'TResult when 'TWorkflow :> Workflow<'TInput, 'TResult>>
    internal (client: IWorkflowClient<'TInput, 'TResult>) =
    /// The workflow's client.
    member _.Client = client

/// Registers every workflow added with `AddWorkflow` when the host starts, so instances that
/// were running when the process last stopped resume without waiting for a request.
type internal WorkflowStarter(services: IServiceProvider, registrations: IEnumerable<WorkflowRegistration>) =
    interface IHostedService with
        member _.StartAsync(_) =
            for registration in registrations do
                registration.Resolve services

            Task.CompletedTask

        member _.StopAsync(_) = Task.CompletedTask

/// One workflow added to the service collection.
and internal WorkflowRegistration(resolve: IServiceProvider -> unit) =
    member _.Resolve(services: IServiceProvider) = resolve services

/// Adds workflows to a host.
[<Extension; Sealed; AbstractClass>]
type WorkflowServiceExtensions =

    /// Registers the workflow `TWorkflow` and its `IWorkflowClient<TInput, TResult>`. The workflow
    /// is created from the container, so its constructor can take the services its activities
    /// call. Call it after `AddFcqrs`, which provides the actor system.
    [<Extension>]
    static member AddWorkflow<'TWorkflow, 'TInput, 'TResult when 'TWorkflow :> Workflow<'TInput, 'TResult>>
        (services: IServiceCollection)
        : IServiceCollection =
        // One registration per workflow class, so two workflows with the same input and result
        // types are both registered at startup.
        services.AddSingleton<WorkflowClient<'TWorkflow, 'TInput, 'TResult>>(fun (provider: IServiceProvider) ->
            let workflow = ActivatorUtilities.CreateInstance<'TWorkflow> provider
            WorkflowClient<'TWorkflow, 'TInput, 'TResult>(WorkflowHost.Register(provider.GetRequiredService<IActor>(), workflow)))
        |> ignore

        // With several workflows of these types, this resolves the one added last.
        services.AddSingleton<IWorkflowClient<'TInput, 'TResult>>(fun (provider: IServiceProvider) ->
            provider.GetRequiredService<WorkflowClient<'TWorkflow, 'TInput, 'TResult>>().Client)
        |> ignore

        services.AddSingleton<WorkflowRegistration>(
            WorkflowRegistration(fun provider -> provider.GetRequiredService<WorkflowClient<'TWorkflow, 'TInput, 'TResult>>() |> ignore)
        )
        |> ignore

        // The host adds one starter however many workflows are registered.
        services.AddHostedService<WorkflowStarter>() |> ignore
        services
