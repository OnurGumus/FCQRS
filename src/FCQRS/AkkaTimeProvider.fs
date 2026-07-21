// Define a message type to trigger the callback
module FCQRS.AkkaTimeProvider

open Akka.Actor
open Akka.Event
open Akkling
open System

open System.Threading
open System.Threading.Tasks
open System.Diagnostics

[<AutoOpen>]
module Internal = 

    type internal ExecuteCallback = ExecuteCallback

    // BCL parity: TimeProvider.System accepts due times and periods from
    // Timeout.InfiniteTimeSpan (-1 ms, "never fire") up to uint.MaxValue - 1 ms
    // (~49.7 days). Anything outside that range is an argument error, not a
    // value to clamp (the old clamp fired a 30-day timer ~5 days early).
    let private maxSupported = TimeSpan.FromMilliseconds(float UInt32.MaxValue - 1.0)

    let private validateTime (argName: string) (value: TimeSpan) =
        if value < Timeout.InfiniteTimeSpan || value > maxSupported then
            raise (
                ArgumentOutOfRangeException(
                    argName,
                    value,
                    "Must be Timeout.InfiniteTimeSpan (-1 ms) or a value between 0 and 4294967294 ms."))

    // Actor that executes the timer callback
    type internal CallbackActor(callback: TimerCallback, state: obj | null, log: ILoggingAdapter) =
        inherit UntypedActor()

        override x.OnReceive(message: obj) =
            match message with
            | :? ExecuteCallback ->
                try
                    callback.Invoke(state)
                with ex ->
                    // Route through the actor system log (Console.Error goes
                    // nowhere when stdout/stderr logging is off).
                    log.Error(ex, "Error during timer execution")
            | _ -> ()

    // Timer implementation using Akka.NET's scheduler
    type internal AkkaTimer(callback: TimerCallback, state: obj | null, dueTime: TimeSpan, period: TimeSpan, actorSystem: ActorSystem) =
        let scheduler = actorSystem.Scheduler
        let sync = obj ()
        let mutable isDisposed = false
        let mutable timerHandle: ICancelable | null = null

        // Validate before anything (including the callback actor) is created:
        // CreateTimer/Change throw on out-of-range input per the BCL contract.
        do validateTime "dueTime" dueTime
        do validateTime "period" period

        // -1 ms (Timeout.InfiniteTimeSpan) disables repetition; period 0 fires once.
        let mutable currentPeriod = period

        // Create the actor that will execute the callback
        let callbackActorRef = actorSystem.ActorOf(Props.Create(fun () -> CallbackActor(callback, state, actorSystem.Log)))

        // Schedule the timer. A negative due time (Timeout.InfiniteTimeSpan)
        // means "never fire" per BCL timer semantics, so nothing is scheduled.
        // TimeSpans are passed straight to the scheduler: the Akka scheduler
        // APIs take TimeSpan, so there is no ms truncation and no ~24.86-day
        // Int32 ceiling, and a recurring period cannot drift through clamping.
        let schedule (due: TimeSpan) =
            if due >= TimeSpan.Zero then
                timerHandle <-
                    if currentPeriod <= TimeSpan.Zero then
                        // Schedule a one-time execution
                        scheduler.ScheduleTellOnceCancelable(
                            due,
                            callbackActorRef,
                            ExecuteCallback,
                            ActorRefs.NoSender)
                    else
                        // Schedule repeated executions
                        scheduler.ScheduleTellRepeatedlyCancelable(
                            due,
                            currentPeriod,
                            callbackActorRef,
                            ExecuteCallback,
                            ActorRefs.NoSender)

        do lock sync (fun () -> schedule dueTime)

        interface ITimer with
            member _.Dispose() =
                lock sync (fun () ->
                    isDisposed <- true

                    if timerHandle <> null then
                        (timerHandle |> Unchecked.nonNull).Cancel()
                        timerHandle <- null
                    // Stop the actor
                    callbackActorRef.Tell(PoisonPill.Instance))

            member _.Change(newDueTime: TimeSpan, newPeriod: TimeSpan) : bool =
                validateTime "newDueTime" newDueTime
                validateTime "newPeriod" newPeriod

                lock sync (fun () ->
                    if isDisposed then
                        false
                    else
                        currentPeriod <- newPeriod

                        if timerHandle <> null then
                            (timerHandle |> Unchecked.nonNull).Cancel()
                            timerHandle <- null

                        schedule newDueTime
                        true)

            /// <summary>
            /// Cancels the timer and completes once the callback actor has
            /// terminated. Because the PoisonPill is queued behind any callback
            /// already delivered to the actor, awaiting this guarantees all
            /// in-flight callbacks have completed, as the BCL contract requires.
            /// </summary>
            member this.DisposeAsync(): ValueTask = 
                (this :> ITimer).Dispose()
                ValueTask(task = WatchAsyncSupport.WatchAsync(callbackActorRef))
                
/// <summary>
/// Custom TimeProvider that uses Akka.NET's scheduler. This allows you to use akka testing schedulers for entirety of the system.
/// Once you play with the akka testing schedulers and use this provider, you will still be able to control the time of the entire system.
/// </summary>
/// <remarks>
/// CreateTimer follows the BCL contract: due times and periods must be
/// Timeout.InfiniteTimeSpan (-1 ms, "never fire") or between 0 and
/// 4,294,967,294 ms (~49.7 days); anything else throws
/// ArgumentOutOfRangeException instead of being silently clamped.
/// </remarks>
type internal AkkaTimeProvider(actorSystem: ActorSystem) =
    inherit TimeProvider()

    let scheduler = actorSystem.Scheduler

    // Override GetUtcNow to use Akka.NET's scheduler's NowUtc
    override _.GetUtcNow() : DateTimeOffset =
        scheduler.Now

    // GetTimestamp must stay coherent with the clock that Task.Delay and
    // CreateTimer follow. On the ObservingScheduler (test scheduler) timers
    // run on the VIRTUAL clock, but scheduler.MonotonicClock is a REAL
    // stopwatch (Akka's TestScheduler never advances it), so derive the
    // timestamp from the virtual Now there. On a real scheduler both Now and
    // MonotonicClock track real time, so keep the monotonic clock.
    override _.GetTimestamp() : int64 =
        match scheduler with
        | :? FCQRS.Scheduler.ObservingScheduler as obs -> obs.Now.UtcTicks
        | _ -> scheduler.MonotonicClock.Ticks

    // TimestampFrequency must match GetTimestamp's units: both sources above
    // yield TimeSpan ticks (10^7/s), NOT Stopwatch.Frequency (10^9 on modern
    // Linux/macOS) — a mismatch makes GetElapsedTime ~100x too small.
    override _.TimestampFrequency : int64 =
        TimeSpan.TicksPerSecond

    // Implement CreateTimer to use AkkaTimer
    override _.CreateTimer(callback: TimerCallback, state: obj, dueTime: TimeSpan, period: TimeSpan) : ITimer =
        let timer = new AkkaTimer(callback , state, dueTime, period, actorSystem) 
        timer :> ITimer
