module FCQRS.SchedulerController

open System
open System.Threading
open Akka.Actor // For IScheduler
open Akka.TestKit // For ObservingScheduler
open FCQRS.Scheduler
// --- Agent Message and State Types ---
type internal ControllerMessage =
    | InitializeScheduler of obsScheduler: ObservingScheduler
    | WatchForTask of taskName: string // For manual pause and signal
    | RegisterAutoAdvanceOnAppearance of taskName: string // For automatic advance on appearance
    | TaskEnqueued of nameOpt: string option * delay: TimeSpan * enqueuedAt: DateTimeOffset
    | SignalToAdvanceCapturedTask // For manual pause and signal
    | InternalAdvancerTick
    | Stop // Optional: For gracefully stopping the agent

type internal ControllerState = {
    Scheduler: ObservingScheduler option
    TaskToWatchFor: string option // Related to manual pause/signal
    CapturedTaskDetails: (string * TimeSpan * DateTimeOffset) option // Related to manual pause/signal (name, delay, enqueuedAt)
    TasksAwaitingAutoAdvance: Set<string> // Tasks to auto-advance upon appearance
    IsNormalAdvancementPaused: bool // True if manual pause is active
    NormalAdvancementAmount: TimeSpan
}

let private initialAgentState = {
    Scheduler = None
    TaskToWatchFor = None
    CapturedTaskDetails = None
    TasksAwaitingAutoAdvance = Set.empty
    IsNormalAdvancementPaused = false
    NormalAdvancementAmount = TimeSpan.FromSeconds(1.0)
}

// --- Agent Definition ---
let private createAgent (systemScheduler: IScheduler) =
    MailboxProcessor.Start(fun inbox ->
        let rec agentLoop (state: ControllerState) =
            async {
                let! msg = inbox.Receive()
                match msg with
                | InitializeScheduler obsScheduler ->
                    // Register the OnEnqueued hook to send messages to this agent.
                    // Capture the enqueue time so advances land on the task's DUE
                    // time, not on enqueue-delay-from-now (which overshoots once
                    // the virtual clock has moved since enqueue).
                    obsScheduler.OnEnqueued.Add(fun (nameOpt, delay, _) ->
                        inbox.Post(TaskEnqueued(nameOpt, delay, obsScheduler.Now))
                    )
                    // Start the internal ticker
                    inbox.Post(InternalAdvancerTick)
                    return! agentLoop { state with Scheduler = Some obsScheduler }

                | WatchForTask taskName -> // For manual pause and signal
                    return! agentLoop {
                        state with
                            TaskToWatchFor = Some taskName
                            CapturedTaskDetails = None // Clear previous capture
                            IsNormalAdvancementPaused = false // Ensure advancer runs until this specific task is caught
                    }

                | RegisterAutoAdvanceOnAppearance taskName ->
                    return! agentLoop { state with TasksAwaitingAutoAdvance = state.TasksAwaitingAutoAdvance |> Set.add taskName }

                | TaskEnqueued (nameOpt, delay, enqueuedAt) ->
                    match nameOpt with
                    | Some actualName ->
                        // Priority 1: Check for Auto-Advance tasks (StartsWith match)
                        let autoAdvanceMatch =
                            state.TasksAwaitingAutoAdvance
                            |> Seq.tryFind (fun expectedPrefix -> actualName.StartsWith(expectedPrefix))

                        match autoAdvanceMatch with
                        | Some matchedPrefix ->
                            let updatedAutoAdvanceSet = state.TasksAwaitingAutoAdvance |> Set.remove matchedPrefix
                            let nextState = { state with TasksAwaitingAutoAdvance = updatedAutoAdvanceSet }
                            let _ = // Bind the result of the match expression
                                match state.Scheduler with
                                | Some obsSch ->
                                    // Advance to the task's DUE time: the clock may
                                    // already have moved since enqueue, and advancing
                                    // by the full delay would overshoot and fire
                                    // unrelated tasks early.
                                    let remaining = enqueuedAt + delay - obsSch.Now

                                    if remaining > TimeSpan.Zero then
                                        obsSch.Advance(remaining)
                                | None ->  ()
                            return! agentLoop nextState

                        | None -> // Not handled by auto-advance, check manual watch (StartsWith match)
                            match state.TaskToWatchFor with
                            | Some watchPrefix when actualName.StartsWith(watchPrefix) && state.CapturedTaskDetails.IsNone ->
                                if delay > TimeSpan.Zero then
                                    let nextStateAfterManualCapture = {
                                        state with
                                            CapturedTaskDetails = Some (actualName, delay, enqueuedAt)
                                            TaskToWatchFor = None
                                            IsNormalAdvancementPaused = true
                                    }
                                    return! agentLoop nextStateAfterManualCapture
                                else
                                    return! agentLoop state
                            | _ ->
                                return! agentLoop state

                    | None ->
                        return! agentLoop state

                | SignalToAdvanceCapturedTask -> // For manual pause and signal
                    match state.CapturedTaskDetails, state.Scheduler with
                    | Some (name, capturedDelay, enqueuedAt), Some obsSch ->
                        // Same due-time rule as auto-advance: do not overshoot.
                        let remaining = enqueuedAt + capturedDelay - obsSch.Now

                        if remaining > TimeSpan.Zero then
                            obsSch.Advance(remaining)

                        return! agentLoop {
                            state with
                                CapturedTaskDetails = None
                                IsNormalAdvancementPaused = false // Resume normal advancement
                        }
                    | _, None ->
                        return! agentLoop state
                    | None, _ ->
                        return! agentLoop state

                | InternalAdvancerTick ->
                    // Post the next tick from a detached timer instead of sleeping
                    // inside the loop: a sleeping agent is deaf to WatchForTask /
                    // RegisterAutoAdvanceOnAppearance for up to a second.
                    let tickAfter (ms: int) =
                        Async.Start(async {
                            do! Async.Sleep ms
                            inbox.Post(InternalAdvancerTick)
                        })

                    match state.Scheduler, state.IsNormalAdvancementPaused with
                    | Some obsSch, false ->
                        obsSch.Advance(state.NormalAdvancementAmount)
                        tickAfter 1000 // Real-time pacing
                        return! agentLoop state
                    | Some _, true -> // Manually Paused
                        tickAfter 1000
                        return! agentLoop state
                    | None, _ -> // Scheduler not yet initialized
                        tickAfter 100 // Wait a bit before retrying to initialize ticker
                        return! agentLoop state
                
                | Stop ->
                    return () // Terminate the agent loop
            }
        agentLoop initialAgentState
    )

// --- Public API --- 
// Store the agent instance once started
let mutable private agentInstance: MailboxProcessor<ControllerMessage> option = None

/// <summary>
/// Initializes and starts the SchedulerController agent.
/// This should be called once during application startup.
/// </summary>
let start (systemScheduler: IScheduler) =
    match systemScheduler with
    | :? ObservingScheduler as obsSch ->
        // Replace any previous agent instead of leaking it (it would keep
        // ticking and still hold the old scheduler's OnEnqueued hook).
        match agentInstance with
        | Some old -> old.Post(Stop)
        | None -> ()

        let agent = createAgent obsSch
        agent.Post(InitializeScheduler obsSch) // Send initial scheduler
        agentInstance <- Some agent
    | _ -> ()

/// <summary>
/// Instructs the controller to watch for the next occurrence of a specific task name.
/// </summary>
let watchForAndPauseOnNext(taskName: string) =
    match agentInstance with
    | Some agent -> agent.Post(WatchForTask taskName)
    | None -> ()

/// <summary>
/// Signals the controller to advance for a previously captured task and resume normal advancement.
/// </summary>
let signalAndAdvanceForCapturedTask() =
    match agentInstance with
    | Some agent -> agent.Post(SignalToAdvanceCapturedTask)
    | None ->  ()

/// <summary>
/// Registers a task for automatic advancement upon its appearance.
/// </summary>
let registerAutoAdvanceOnAppearance (taskName: string) =
    match agentInstance with
    | Some agent -> agent.Post(RegisterAutoAdvanceOnAppearance taskName)
    | None ->  ()

/// <summary>
/// (Optional) Stops the scheduler controller agent.
/// </summary>
let stop() =
    match agentInstance with
    | Some agent ->
        agent.Post(Stop)
        agentInstance <- None // don't leave a dead agent addressable
    | None -> ()