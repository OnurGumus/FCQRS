module FCQRS.Scheduler


open System
open System.Threading
open Akka.Actor
open Akka.Configuration
open Akka.Event
open Akka.TestKit

/// Proxy that carries a name through cancellation - uses a CancellationTokenSource to handle cancellation
type internal NamedCancelable
    (
        ctsToManage: CancellationTokenSource,
        name: string option,
        taskInitialDelay: TimeSpan,
        taskAction: unit -> unit,
        cancelEventToTrigger: Event<string option * TimeSpan * (unit -> unit)>
    ) =

    // 0 = not signalled, 1 = signalled. Guards the cancelled event: repeated
    // or concurrent cancellation (Cancel, or a CancelAfter timer tripping)
    // must fire it exactly once.
    let mutable cancelSignalled = 0

    let fireCancelEvent () =
        if Interlocked.CompareExchange(&cancelSignalled, 1, 0) = 0 then
            cancelEventToTrigger.Trigger(name, taskInitialDelay, taskAction)

    let cancelCore (cancel: unit -> unit) =
        // CancellationTokenSource.Cancel is thread-safe and idempotent, and
        // fireCancelEvent carries the once-only guard for the event.
        cancel ()
        fireCancelEvent ()

    interface ICancelable with
        member _.IsCancellationRequested = ctsToManage.IsCancellationRequested

        member _.Cancel() =
            cancelCore (fun () -> ctsToManage.Cancel())

        member _.Token = ctsToManage.Token

        member _.CancelAfter(delay: TimeSpan) =
            ctsToManage.CancelAfter(delay)
            // Mirror Cancel(): fire the cancelled event when the delayed cancel
            // trips. fireCancelEvent's guard prevents a double-fire if Cancel()
            // ran (or runs) as well; an already-cancelled token invokes the
            // registration synchronously, which the guard also absorbs.
            ctsToManage.Token.Register(Action(fun () -> fireCancelEvent ())) |> ignore

        member _.CancelAfter(millisecondsDelay: int) =
            ctsToManage.CancelAfter(millisecondsDelay)
            ctsToManage.Token.Register(Action(fun () -> fireCancelEvent ())) |> ignore

        member _.Cancel(throwOnFirstException: bool) =
            cancelCore (fun () -> ctsToManage.Cancel(throwOnFirstException))

/// Scheduler wrapper that exposes enqueue/cancel events with optional names
type ObservingScheduler(config: Config, log: ILoggingAdapter) =
    // Underlying TestScheduler
    let inner = new TestScheduler(config, log)
    // Events carrying (name option, delay, callback)
    let enqueuedEvent = Event<string option * TimeSpan * (unit -> unit)>()
    let cancelledEvent = Event<string option * TimeSpan * (unit -> unit)>()

    /// Fired whenever a ScheduledItem is enqueued
    [<CLIEvent>]
    member _.OnEnqueued: IEvent<string option * TimeSpan * (unit -> unit)> =
        enqueuedEvent.Publish

    /// Fired whenever a ScheduledItem is cancelled
    [<CLIEvent>]
    member _.OnCancelled: IEvent<string option * TimeSpan * (unit -> unit)> =
        cancelledEvent.Publish

    /// Schedule a one-time action with optional name
    member this.ScheduleOnce(name: string option, delay: TimeSpan, action: unit -> unit) : ICancelable =
        let cts = new System.Threading.CancellationTokenSource()

        let wrappedAction =
            fun () ->
                if not cts.IsCancellationRequested then
                    action ()

        (inner :> IActionScheduler).ScheduleOnce(delay, Action(wrappedAction))
        enqueuedEvent.Trigger(name, delay, action)
        new NamedCancelable(cts, name, delay, action, cancelledEvent) :> ICancelable

    /// Fallback ScheduleOnce without name
    member this.ScheduleOnce(delay: TimeSpan, action: unit -> unit) : ICancelable =
        this.ScheduleOnce(None, delay, action)

    /// Schedule a one-time Tell with optional name
    member this.ScheduleTellOnce
        (name: string option, delay: TimeSpan, receiver: ICanTell, message: obj, sender: IActorRef)
        : ICancelable =
        let cts = new System.Threading.CancellationTokenSource()
        let originalTellAction = fun () -> receiver.Tell(message, sender)

        let wrappedTellAction =
            fun () ->
                if not cts.IsCancellationRequested then
                    originalTellAction ()

        (inner :> IActionScheduler).ScheduleOnce(delay, Action(wrappedTellAction))
        enqueuedEvent.Trigger(name, delay, originalTellAction)
        new NamedCancelable(cts, name, delay, originalTellAction, cancelledEvent) :> ICancelable

    /// Fallback ScheduleTellOnce without name
    member this.ScheduleTellOnce(delay: TimeSpan, receiver: ICanTell, message: obj, sender: IActorRef) : ICancelable =
        this.ScheduleTellOnce(None, delay, receiver, message, sender)

    /// Schedule a repeated action with optional name
    member this.ScheduleRepeatedly
        (name: string option, initialDelay: TimeSpan, interval: TimeSpan, action: unit -> unit)
        : ICancelable =
        let cts = new System.Threading.CancellationTokenSource()

        let wrappedAction =
            fun () ->
                if not cts.IsCancellationRequested then
                    action ()

        // Thread the cancelable into the inner schedule: TestScheduler.Advance
        // re-enqueues repeating items whose cancelable is null-or-not-cancelled,
        // so without it a cancelled schedule would be re-enqueued forever.
        let cancelable =
            new NamedCancelable(cts, name, initialDelay, action, cancelledEvent)

        (inner :> IActionScheduler)
            .ScheduleRepeatedly(initialDelay, interval, Action(wrappedAction), cancelable)

        enqueuedEvent.Trigger(name, initialDelay, action)
        cancelable :> ICancelable

    /// Fallback ScheduleRepeatedly without name
    member this.ScheduleRepeatedly(initialDelay: TimeSpan, interval: TimeSpan, action: unit -> unit) : ICancelable =
        this.ScheduleRepeatedly(None, initialDelay, interval, action)

    /// Schedule repeated Tell with optional name
    member this.ScheduleTellRepeatedly
        (
            name: string option,
            initialDelay: TimeSpan,
            interval: TimeSpan,
            receiver: ICanTell,
            message: obj,
            sender: IActorRef
        ) : ICancelable =
        let cts = new System.Threading.CancellationTokenSource()
        let originalTellAction = fun () -> receiver.Tell(message, sender)

        let wrappedTellAction =
            fun () ->
                if not cts.IsCancellationRequested then
                    originalTellAction ()

        // Same cancelable threading as ScheduleRepeatedly: keeps a cancelled
        // recurring Tell from being re-enqueued by TestScheduler.Advance.
        let cancelable =
            new NamedCancelable(cts, name, initialDelay, originalTellAction, cancelledEvent)

        (inner :> IActionScheduler)
            .ScheduleRepeatedly(initialDelay, interval, Action(wrappedTellAction), cancelable)

        enqueuedEvent.Trigger(name, initialDelay, originalTellAction)
        cancelable :> ICancelable

    /// Fallback ScheduleTellRepeatedly without name
    member this.ScheduleTellRepeatedly
        (initialDelay: TimeSpan, interval: TimeSpan, receiver: ICanTell, message: obj, sender: IActorRef)
        : ICancelable =
        this.ScheduleTellRepeatedly(None, initialDelay, interval, receiver, message, sender)

    /// Cancel via wrapper with name
    member this.Cancel(cancelable: ICancelable, name: string option) =
        // A NamedCancelable triggers the cancelled event itself (with its real
        // name, delay and action) inside Cancel(), so triggering again here
        // would fire the event twice for one logical cancel. External
        // cancelables carry no payload; report the passed name once.
        cancelable.Cancel()

        match cancelable with
        | :? NamedCancelable -> ()
        | _ -> cancelledEvent.Trigger(name, TimeSpan.Zero, fun () -> ())

    /// The virtual clock's current time.
    member _.Now = inner.Now

    /// Drive the virtual clock forward. TestScheduler.Advance is NOT thread-safe
    /// (racy clock update + snapshot/execute/remove), and the controller agent
    /// advances on its own thread — serialize all advances through this lock or
    /// a due item can be delivered twice.
    member _.Advance(d: TimeSpan) = lock inner (fun () -> inner.Advance(d))
    member _.AdvanceTo(deadline: DateTimeOffset) = lock inner (fun () -> inner.AdvanceTo(deadline))

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

        member _.ScheduleRepeatedly
            (initialDelay: TimeSpan, interval: TimeSpan, action: Akka.Dispatch.IRunnable, cancelable: ICancelable)
            =
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

        member _.ScheduleRepeatedly
            (initialDelay: TimeSpan, interval: TimeSpan, action: Action, cancelable: ICancelable)
            =
            let userAction = fun () -> action.Invoke()
            enqueuedEvent.Trigger(None, initialDelay, userAction)
            inner.ScheduleRepeatedly(initialDelay, interval, action, cancelable)

    // ITellScheduler - enables sending messages to actors on a schedule
    interface ITellScheduler with
        // Standard overloads
        member this.ScheduleTellOnce(delay: TimeSpan, receiver: ICanTell, message: obj, sender: IActorRef) =
            let _ = this.ScheduleTellOnce(None, delay, receiver, message, sender)
            ()

        member _.ScheduleTellOnce
            (delay: TimeSpan, receiver: ICanTell, message: obj, sender: IActorRef, cancelable: ICancelable)
            =
            let tellAction = fun () -> receiver.Tell(message, sender)
            enqueuedEvent.Trigger(None, delay, tellAction)
            inner.ScheduleTellOnce(delay, receiver, message, sender, cancelable)

        member this.ScheduleTellRepeatedly
            (initialDelay: TimeSpan, interval: TimeSpan, receiver: ICanTell, message: obj, sender: IActorRef)
            =
            let _ =
                this.ScheduleTellRepeatedly(None, initialDelay, interval, receiver, message, sender)

            ()

        member _.ScheduleTellRepeatedly
            (
                initialDelay: TimeSpan,
                interval: TimeSpan,
                receiver: ICanTell,
                message: obj,
                sender: IActorRef,
                cancelable: ICancelable
            ) =
            let tellAction = fun () -> receiver.Tell(message, sender)
            enqueuedEvent.Trigger(None, initialDelay, tellAction)
            inner.ScheduleTellRepeatedly(initialDelay, interval, receiver, message, sender, cancelable)

    // IAdvancedScheduler implementation
    interface IAdvancedScheduler
