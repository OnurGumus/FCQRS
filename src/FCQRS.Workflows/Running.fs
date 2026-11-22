namespace FCQRS.Workflows

open System
open System.Collections.Concurrent
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open FCQRS.Common

/// An attempt of a step that this process is running: an activity's call, a command waiting for
/// its reply, or a child start. It stays listed until its call returns, also when it ignores its
/// cancelled token after the attempt timed out, its branch was abandoned, or its workflow stopped.
type RunningAttempt =
    { Workflow: string
      /// The instance's ID; from its second run on, `<ID>#<run>`.
      Instance: string
      /// The step's path in the body.
      Path: string
      /// The step's name.
      Step: string
      Kind: StepKind
      /// 1 for the first attempt since the process last started this step.
      Attempt: int
      /// When the attempt started, in real time.
      Started: DateTimeOffset
      /// Whether the attempt's token was cancelled. A cancelled attempt that stays listed is one
      /// whose call does not observe its token.
      CancellationRequested: bool }

/// The attempts an actor system is running, and the wait for them when it shuts down.
module internal RunningAttempts =

    type private Entry =
        { Workflow: string
          Instance: string
          Path: string
          Step: string
          Kind: StepKind
          Attempt: int
          Started: DateTimeOffset
          Token: CancellationToken
          Call: Task }

    let private snapshotOf (entry: Entry) : RunningAttempt =
        { Workflow = entry.Workflow
          Instance = entry.Instance
          Path = entry.Path
          Step = entry.Step
          Kind = entry.Kind
          Attempt = entry.Attempt
          Started = entry.Started
          CancellationRequested = entry.Token.IsCancellationRequested }

    let private drainKey = "akka.fcqrs.workflow-drain-timeout"

    /// How long a shutdown waits for running attempts to return. Three seconds unless the
    /// configuration says otherwise; a bare number is seconds.
    let drainTimeOf (config: Akka.Configuration.Config) : TimeSpan =
        if config.HasPath drainKey then
            match config.GetString drainKey |> Int32.TryParse with
            | true, seconds -> TimeSpan.FromSeconds(float seconds)
            | _ -> config.GetTimeSpan(drainKey, Nullable(TimeSpan.FromSeconds 3.0))
        else
            TimeSpan.FromSeconds 3.0

    type Registry(api: IActor) =
        let entries = ConcurrentDictionary<Guid, Entry>()
        let logger = api.LoggerFactory.CreateLogger "FCQRS.Workflows.Shutdown"

        /// What is running now, oldest first.
        member _.Snapshot() : RunningAttempt list =
            entries.Values |> Seq.map snapshotOf |> Seq.sortBy _.Started |> List.ofSeq

        /// Lists `call` until it ends.
        member _.Track(workflow: string, instance: string, path: string, step: string, kind: StepKind, attempt: int, token: CancellationToken, call: Task) =
            let id = Guid.NewGuid()

            entries[id] <-
                { Workflow = workflow
                  Instance = instance
                  Path = path
                  Step = step
                  Kind = kind
                  Attempt = attempt
                  Started = DateTimeOffset.UtcNow
                  Token = token
                  Call = call }

            // Runs at once when the call has already ended.
            call.ContinueWith((fun (_: Task) -> entries.TryRemove id |> ignore), TaskContinuationOptions.ExecuteSynchronously)
            |> ignore

        /// Waits up to `timeout` for the running attempts to return, and logs the ones that do
        /// not. By then their workflows have stopped, which cancelled their tokens.
        member this.Drain(timeout: TimeSpan) : Task =
            task {
                let calls = entries.Values |> Seq.map _.Call |> Array.ofSeq

                if calls.Length > 0 then
                    let all = Task.WhenAll calls
                    let! first = Task.WhenAny(all, Task.Delay timeout)

                    if not (obj.ReferenceEquals(first, all)) then
                        let now = DateTimeOffset.UtcNow

                        for attempt in this.Snapshot() do
                            logger.LogWarning(
                                "Shutdown did not wait for {Workflow} '{Instance}' step {Path} {Step}, attempt {Attempt}, running for {Age}; its call ignores its cancelled token.",
                                attempt.Workflow,
                                attempt.Instance,
                                attempt.Path,
                                attempt.Step,
                                attempt.Attempt,
                                now - attempt.Started
                            )
            }

    let private registries = ConditionalWeakTable<Akka.Actor.ActorSystem, Registry>()

    /// The actor system's registry. The first call adds the shutdown task that drains it, in the
    /// last phase before the actor system terminates: sharding has stopped the workflows by then.
    let forSystem (api: IActor) : Registry =
        registries.GetValue(
            api.System,
            fun system ->
                let registry = Registry(api)
                let timeout = drainTimeOf system.Settings.Config

                try
                    Akka.Actor.CoordinatedShutdown
                        .Get(system)
                        .AddTask(
                            Akka.Actor.CoordinatedShutdown.PhaseBeforeActorSystemTerminate,
                            "fcqrs-workflow-drain",
                            fun () -> registry.Drain(timeout).ContinueWith(fun (_: Task) -> Akka.Done.Instance)
                        )
                with :? InvalidOperationException ->
                    // Shutdown has started: nothing waits for attempts that start now.
                    ()

                registry
        )
