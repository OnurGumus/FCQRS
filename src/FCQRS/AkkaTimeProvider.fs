// Define a message type to trigger the callback
module FCQRS.AkkaTimeProvider

open Akka.Actor
open Akkling
open System

open System.Threading
open System.Threading.Tasks
open System.Diagnostics

[<AutoOpen>]
module Internal = 

    type internal ExecuteCallback = ExecuteCallback

    // Actor that executes the timer callback
    type internal CallbackActor(callback: TimerCallback, state: obj) =
        inherit UntypedActor()

        override x.OnReceive(message: obj) =
            match message with
            | :? ExecuteCallback ->
                try
                    callback.Invoke(state)
                with ex ->
                    // Log or handle exceptions as needed
                    Console.Error.WriteLine($"Error during timer execution: {ex.Message}")
            | _ -> ()

    // Timer implementation using Akka.NET's scheduler
    type internal AkkaTimer(callback: TimerCallback, state: obj, dueTime: TimeSpan, period: TimeSpan, actorSystem: ActorSystem) =
        let scheduler = actorSystem.Scheduler
        let mutable isDisposed = false
        let mutable timerHandle: ICancelable  = null

        // Convert dueTime and period to milliseconds
        let initialDelayMs = 
            if dueTime < TimeSpan.Zero then 0
            elif dueTime.TotalMilliseconds > double Int32.MaxValue then Int32.MaxValue
            else int dueTime.TotalMilliseconds

        let periodMs = 
            if period < TimeSpan.Zero then Timeout.Infinite
            elif period.TotalMilliseconds > double Int32.MaxValue then Int32.MaxValue
            else int period.TotalMilliseconds

        // Create the actor that will execute the callback
        let callbackActorRef = actorSystem.ActorOf(Props.Create(fun () -> CallbackActor(callback, state)))

        // Start the timer with the initial due time and period
        let rec start() =
            if not isDisposed then
                timerHandle <- 
                    if periodMs = Timeout.Infinite || periodMs = 0 then
                        // Schedule a one-time execution
                        scheduler.ScheduleTellOnceCancelable(
                            initialDelayMs,
                            callbackActorRef,
                            ExecuteCallback,
                            ActorRefs.NoSender)
                    else
                        // Schedule repeated executions
                        scheduler.ScheduleTellRepeatedlyCancelable(
                            initialDelayMs,
                            periodMs,
                            callbackActorRef,
                            ExecuteCallback,
                            ActorRefs.NoSender)

        do start()

        interface ITimer with
            member _.Dispose() =
                isDisposed <- true
                if timerHandle <> null then
                    (timerHandle |> Unchecked.nonNull).Cancel()
                // Stop the actor
                callbackActorRef.Tell(PoisonPill.Instance)

            member _.Change(newDueTime: TimeSpan, newPeriod: TimeSpan) : bool =
                let initialDelayMs = 
                    if newDueTime < TimeSpan.Zero then 0
                    elif newDueTime.TotalMilliseconds > double Int32.MaxValue then Int32.MaxValue
                    else int newDueTime.TotalMilliseconds

                let periodMs = 
                    if newPeriod < TimeSpan.Zero then Timeout.Infinite
                    elif newPeriod.TotalMilliseconds > double Int32.MaxValue then Int32.MaxValue
                    else int newPeriod.TotalMilliseconds

                if isDisposed then false
                else
                    if timerHandle <> null then
                        (timerHandle |> Unchecked.nonNull).Cancel()
                    timerHandle <- 
                        if periodMs = Timeout.Infinite || periodMs = 0 then
                            // Schedule a one-time execution
                            scheduler.ScheduleTellOnceCancelable(
                                initialDelayMs,
                                callbackActorRef,
                                ExecuteCallback,
                                ActorRefs.NoSender)
                        else
                            // Schedule repeated executions
                            scheduler.ScheduleTellRepeatedlyCancelable(
                                initialDelayMs,
                                periodMs,
                                callbackActorRef,
                                ExecuteCallback,
                                ActorRefs.NoSender)
                    true

            member this.DisposeAsync(): ValueTask = 
                (this :> ITimer).Dispose()
                ValueTask.CompletedTask
                
// Custom TimeProvider that uses Akka.NET's scheduler. This allows you to use akka testing schedulers for entirety of the system
// Once you play with the akka testing schedulers and use this provider, you will still be able to control the time of the entire system.
type AkkaTimeProvider(actorSystem: ActorSystem) =
    inherit TimeProvider()

    let scheduler = actorSystem.Scheduler

    // Override GetUtcNow to use Akka.NET's scheduler's NowUtc
    override _.GetUtcNow() : DateTimeOffset =
        scheduler.Now

    // Override GetTimestamp to use Akka.NET's monotonic clock
    override _.GetTimestamp() : int64 =
        scheduler.MonotonicClock.Ticks

    // Override TimestampFrequency to match Stopwatch frequency
    override _.TimestampFrequency : int64 =
        Stopwatch.Frequency

    // Implement CreateTimer to use AkkaTimer
    override _.CreateTimer(callback: TimerCallback, state: obj, dueTime: TimeSpan, period: TimeSpan) : ITimer =
        let timer = new AkkaTimer(callback , state, dueTime, period, actorSystem) 
        timer :> ITimer
