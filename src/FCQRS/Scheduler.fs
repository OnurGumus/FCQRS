module FCQRS.Scheduler


open System
open System.Threading
open Akka.Actor
open Akka.Configuration
open Akka.Event
open Akka.TestKit

/// Proxy that carries a name through cancellation - uses a CancellationTokenSource to handle cancellation
type NamedCancelable(ctsToManage: CancellationTokenSource, name: string option, taskInitialDelay: TimeSpan, taskAction: unit -> unit, cancelEventToTrigger: Event<string option * TimeSpan * (unit -> unit)>) =
    
    interface ICancelable with
        member _.IsCancellationRequested = ctsToManage.IsCancellationRequested
        
        member _.Cancel() =
            if not ctsToManage.IsCancellationRequested then
                ctsToManage.Cancel()
                cancelEventToTrigger.Trigger(name, taskInitialDelay, taskAction)
                
        member _.Token = ctsToManage.Token
        
        member _.CancelAfter(delay: TimeSpan) =
            ctsToManage.CancelAfter(delay)
            // Note: The cancelledEvent will be triggered if ctsToManage.Cancel() is eventually called.
            // Direct event trigger upon CancelAfter completion would require further logic if desired.
            
        member _.CancelAfter(millisecondsDelay: int) =
            ctsToManage.CancelAfter(millisecondsDelay)
            
        member _.Cancel(throwOnFirstException: bool) =
            if not ctsToManage.IsCancellationRequested then
                ctsToManage.Cancel(throwOnFirstException)
                cancelEventToTrigger.Trigger(name, taskInitialDelay, taskAction)

/// Scheduler wrapper that exposes enqueue/cancel events with optional names
type ObservingScheduler(config: Config, log: ILoggingAdapter) =
    // Underlying TestScheduler
    let inner = new TestScheduler(config, log)
    // Events carrying (name option, delay, callback)
    let enqueuedEvent = Event<string option * TimeSpan * (unit -> unit)>()
    let cancelledEvent = Event<string option * TimeSpan * (unit -> unit)>()

    /// Fired whenever a ScheduledItem is enqueued
    [<CLIEvent>]
    member _.OnEnqueued : IEvent<string option * TimeSpan * (unit -> unit)> = enqueuedEvent.Publish

    /// Fired whenever a ScheduledItem is cancelled
    [<CLIEvent>]
    member _.OnCancelled : IEvent<string option * TimeSpan * (unit -> unit)> = cancelledEvent.Publish

    /// Schedule a one-time action with optional name
    member this.ScheduleOnce(name: string option, delay: TimeSpan, action: unit -> unit) : ICancelable =
        let cts = new System.Threading.CancellationTokenSource()
        let wrappedAction = fun () -> 
            if not cts.IsCancellationRequested then action()
        (inner :> IActionScheduler).ScheduleOnce(delay, Action(wrappedAction))
        enqueuedEvent.Trigger(name, delay, action)
        new NamedCancelable(cts, name, delay, action, cancelledEvent) :> ICancelable

    /// Fallback ScheduleOnce without name
    member this.ScheduleOnce(delay: TimeSpan, action: unit -> unit) : ICancelable =
        this.ScheduleOnce(None, delay, action)

    /// Schedule a one-time Tell with optional name
    member this.ScheduleTellOnce(name: string option, delay: TimeSpan, receiver: ICanTell, message: obj, sender: IActorRef) : ICancelable =
        let cts = new System.Threading.CancellationTokenSource()
        let originalTellAction = fun () -> receiver.Tell(message, sender)
        let wrappedTellAction = fun () ->
            if not cts.IsCancellationRequested then
                originalTellAction()
        
        (inner :> IActionScheduler).ScheduleOnce(delay, Action(wrappedTellAction))
        enqueuedEvent.Trigger(name, delay, originalTellAction)
        new NamedCancelable(cts, name, delay, originalTellAction, cancelledEvent) :> ICancelable

    /// Fallback ScheduleTellOnce without name
    member this.ScheduleTellOnce(delay: TimeSpan, receiver: ICanTell, message: obj, sender: IActorRef) : ICancelable =
        this.ScheduleTellOnce(None, delay, receiver, message, sender)

    /// Schedule a repeated action with optional name
    member this.ScheduleRepeatedly(name: string option, initialDelay: TimeSpan, interval: TimeSpan, action: unit -> unit) : ICancelable =
        let cts = new System.Threading.CancellationTokenSource()
        let wrappedAction = fun () -> 
            if not cts.IsCancellationRequested then action()
        (inner :> IActionScheduler).ScheduleRepeatedly(initialDelay, interval, Action(wrappedAction))
        enqueuedEvent.Trigger(name, initialDelay, action)
        new NamedCancelable(cts, name, initialDelay, action, cancelledEvent) :> ICancelable

    /// Fallback ScheduleRepeatedly without name
    member this.ScheduleRepeatedly(initialDelay: TimeSpan, interval: TimeSpan, action: unit -> unit) : ICancelable =
        this.ScheduleRepeatedly(None, initialDelay, interval, action)

    /// Schedule repeated Tell with optional name
    member this.ScheduleTellRepeatedly(name: string option, initialDelay: TimeSpan, interval: TimeSpan, receiver: ICanTell, message: obj, sender: IActorRef) : ICancelable =
        let cts = new System.Threading.CancellationTokenSource()
        let originalTellAction = fun () -> receiver.Tell(message, sender)
        let wrappedTellAction = fun () ->
            if not cts.IsCancellationRequested then
                originalTellAction()
        
        (inner :> IActionScheduler).ScheduleRepeatedly(initialDelay, interval, Action(wrappedTellAction))
        enqueuedEvent.Trigger(name, initialDelay, originalTellAction)
        new NamedCancelable(cts, name, initialDelay, originalTellAction, cancelledEvent) :> ICancelable
        
    /// Fallback ScheduleTellRepeatedly without name
    member this.ScheduleTellRepeatedly(initialDelay: TimeSpan, interval: TimeSpan, receiver: ICanTell, message: obj, sender: IActorRef) : ICancelable =
        this.ScheduleTellRepeatedly(None, initialDelay, interval, receiver, message, sender)

    /// Cancel via wrapper with name
    member this.Cancel(cancelable: ICancelable, name: string option) =
        // This method seems to imply associating a name with an externally provided ICancelable for cancellation.
        // However, our NamedCancelable already handles its name.
        // If the provided cancelable is one of our NamedCancelable, its name is already known.
        // If it's an external ICancelable, we don't have a direct way to associate this 'name' with its cancellation event.
        // For now, just call Cancel() and trigger a generic event if this method is used.
        cancelable.Cancel() 
        cancelledEvent.Trigger(name, TimeSpan.Zero, fun () -> ()) // Generic cancellation event

    /// Drive the virtual clock forward
    member _.Advance(d: TimeSpan) = inner.Advance(d)
    member _.AdvanceTo(deadline: DateTimeOffset) = inner.AdvanceTo(deadline)

    // Essential interface implementations to replace TestScheduler
    
    // ITimeProvider - provides clock access
    interface ITimeProvider with
        member _.Now = inner.Now
        member _.MonotonicClock = inner.MonotonicClock
        member _.HighResMonotonicClock = inner.HighResMonotonicClock
    
    // IScheduler implementation
    interface IScheduler with
        // Property that returns the advanced scheduler interface
        member this.Advanced = this :> IAdvancedScheduler
    
    // IRunnableScheduler implementation (since we now know it's needed)
    interface IRunnableScheduler with
        // Standard overloads
        member this.ScheduleOnce(delay: TimeSpan, action: Akka.Dispatch.IRunnable) = 
            let runnableAction = fun () -> action.Run()
            let _ = this.ScheduleOnce(None, delay, runnableAction) // Calls ObservingScheduler's own method
            () // Interface method returns void
            
        member _.ScheduleOnce(delay: TimeSpan, action: Akka.Dispatch.IRunnable, cancelable: ICancelable) = 
            let runnableAction = fun () -> action.Run()
            enqueuedEvent.Trigger(None, delay, runnableAction)
            inner.ScheduleOnce(delay, action, cancelable) // Delegate to inner with provided cancelable
            
        member this.ScheduleRepeatedly(initialDelay: TimeSpan, interval: TimeSpan, action: Akka.Dispatch.IRunnable) =
            let runnableAction = fun () -> action.Run()
            let _ = this.ScheduleRepeatedly(None, initialDelay, interval, runnableAction)
            ()
            
        member _.ScheduleRepeatedly(initialDelay: TimeSpan, interval: TimeSpan, action: Akka.Dispatch.IRunnable, cancelable: ICancelable) =
            let runnableAction = fun () -> action.Run()
            enqueuedEvent.Trigger(None, initialDelay, runnableAction)
            inner.ScheduleRepeatedly(initialDelay, interval, action, cancelable)
            
    // IActionScheduler - enables scheduling lambdas/actions
    interface IActionScheduler with
        // Standard overloads
        member this.ScheduleOnce(delay: TimeSpan, action: Action) = 
            let userAction = fun () -> action.Invoke()
            let _ = this.ScheduleOnce(None, delay, userAction)
            ()
            
        member _.ScheduleOnce(delay: TimeSpan, action: Action, cancelable: ICancelable) = 
            let userAction = fun () -> action.Invoke()
            enqueuedEvent.Trigger(None, delay, userAction)
            inner.ScheduleOnce(delay, action, cancelable)
            
        member this.ScheduleRepeatedly(initialDelay: TimeSpan, interval: TimeSpan, action: Action) =
            let userAction = fun () -> action.Invoke()
            let _ = this.ScheduleRepeatedly(None, initialDelay, interval, userAction)
            ()
            
        member _.ScheduleRepeatedly(initialDelay: TimeSpan, interval: TimeSpan, action: Action, cancelable: ICancelable) =
            let userAction = fun () -> action.Invoke()
            enqueuedEvent.Trigger(None, initialDelay, userAction)
            inner.ScheduleRepeatedly(initialDelay, interval, action, cancelable)
    
    // ITellScheduler - enables sending messages to actors on a schedule
    interface ITellScheduler with
        // Standard overloads
        member this.ScheduleTellOnce(delay: TimeSpan, receiver: ICanTell, message: obj, sender: IActorRef) =
            let _ = this.ScheduleTellOnce(None, delay, receiver, message, sender)
            ()
            
        member _.ScheduleTellOnce(delay: TimeSpan, receiver: ICanTell, message: obj, sender: IActorRef, cancelable: ICancelable) =
            let tellAction = fun () -> receiver.Tell(message, sender)
            enqueuedEvent.Trigger(None, delay, tellAction)
            inner.ScheduleTellOnce(delay, receiver, message, sender, cancelable)
            
        member this.ScheduleTellRepeatedly(initialDelay: TimeSpan, interval: TimeSpan, receiver: ICanTell, message: obj, sender: IActorRef) =
            let _ = this.ScheduleTellRepeatedly(None, initialDelay, interval, receiver, message, sender)
            ()
            
        member _.ScheduleTellRepeatedly(initialDelay: TimeSpan, interval: TimeSpan, receiver: ICanTell, message: obj, sender: IActorRef, cancelable: ICancelable) =
            let tellAction = fun () -> receiver.Tell(message, sender)
            enqueuedEvent.Trigger(None, initialDelay, tellAction)
            inner.ScheduleTellRepeatedly(initialDelay, interval, receiver, message, sender, cancelable)
    
    // IAdvancedScheduler implementation
    interface IAdvancedScheduler
