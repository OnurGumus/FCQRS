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

        let toMs (t: TimeSpan) =
            if t.TotalMilliseconds > double Int32.MaxValue then Int32.MaxValue
            else int t.TotalMilliseconds

        let mutable periodMs =
            if period < TimeSpan.Zero then Timeout.Infinite
            else toMs period

        // Create the actor that will execute the callback
        let callbackActorRef = actorSystem.ActorOf(Props.Create(fun () -> CallbackActor(callback, state, actorSystem.Log)))

        // Schedule the timer. A negative due time (Timeout.InfiniteTimeSpan)
        // means "never fire" per BCL timer semantics, so nothing is scheduled.
        let schedule (due: TimeSpan) =
            if due >= TimeSpan.Zero then
                let delayMs = toMs due

                timerHandle <-
                    if periodMs = Timeout.Infinite || periodMs = 0 then
                        // Schedule a one-time execution
                        scheduler.ScheduleTellOnceCancelable(
                            delayMs,
                            callbackActorRef,
                            ExecuteCallback,
                            ActorRefs.NoSender)
                    else
                        // Schedule repeated executions
                        scheduler.ScheduleTellRepeatedlyCancelable(
                            delayMs,
                            periodMs,
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
                lock sync (fun () ->
                    if isDisposed then
                        false
                    else
                        periodMs <-
                            if newPeriod < TimeSpan.Zero then Timeout.Infinite
                            else toMs newPeriod

                        if timerHandle <> null then
                            (timerHandle |> Unchecked.nonNull).Cancel()
                            timerHandle <- null

                        schedule newDueTime
                        true)

            member this.DisposeAsync(): ValueTask = 
                (this :> ITimer).Dispose()
                ValueTask.CompletedTask
                
// Custom TimeProvider that uses Akka.NET's scheduler. This allows you to use akka testing schedulers for entirety of the system
// Once you play with the akka testing schedulers and use this provider, you will still be able to control the time of the entire system.
type internal AkkaTimeProvider(actorSystem: ActorSystem) =
    inherit TimeProvider()

    let scheduler = actorSystem.Scheduler

    // Override GetUtcNow to use Akka.NET's scheduler's NowUtc
    override _.GetUtcNow() : DateTimeOffset =
        scheduler.Now

    // Override GetTimestamp to use Akka.NET's monotonic clock
    override _.GetTimestamp() : int64 =
        scheduler.MonotonicClock.Ticks

    // TimestampFrequency must match GetTimestamp's units: MonotonicClock.Ticks
    // are TimeSpan ticks (10^7/s), NOT Stopwatch.Frequency (10^9 on modern
    // Linux/macOS) — a mismatch makes GetElapsedTime ~100x too small.
    override _.TimestampFrequency : int64 =
        TimeSpan.TicksPerSecond

    // Implement CreateTimer to use AkkaTimer
    override _.CreateTimer(callback: TimerCallback, state: obj, dueTime: TimeSpan, period: TimeSpan) : ITimer =
        let timer = new AkkaTimer(callback , state, dueTime, period, actorSystem) 
        timer :> ITimer
