module ProjectionSubscriptionTests

open System
open System.Collections.Concurrent
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.Extensions.Logging.Abstractions
open FCQRS.FSharp
open FCQRS.Model.Data
open FCQRS.Query

type private Notification(cid: CID, number: int) =
    member _.Number = number
    interface IMessageWithCID with
        member _.CID = cid

// Exercise the production notification hub directly: journal latency would hide
// a race between subscription return and the first published notification.
let private createHub capacity =
    let hubType =
        match typeof<ISubscribe<IMessageWithCID>>.Assembly.GetType("FCQRS.Query+Internal+NotificationHub`1", true) with
        | null -> failwith "The production NotificationHub type was not found."
        | definition -> definition.MakeGenericType [| typeof<IMessageWithCID> |]
    let hub =
        match Activator.CreateInstance(hubType, [| box capacity; box NullLogger.Instance; box (TimeSpan.FromSeconds 5.0) |]) with
        | null -> failwith "The production NotificationHub could not be constructed."
        | instance -> instance
    let flags = BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic
    let requiredMethod name =
        match hubType.GetMethod(name, flags) with
        | null -> failwithf "The production NotificationHub.%s method was not found." name
        | method -> method
    let publishMethod = requiredMethod "Publish"
    let stopMethod = requiredMethod "Stop"
    let publish (event: IMessageWithCID) = publishMethod.Invoke(hub, [| box event |]) |> ignore
    let stop () = stopMethod.Invoke(hub, [||]) |> ignore
    hub :?> ISubscribe<IMessageWithCID>, publish, stop

let private completed (task: Task) =
    let winner = Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds 5.0)).GetAwaiter().GetResult()
    Expect.isTrue (obj.ReferenceEquals(winner, task)) "the subscription completed within the test bound"

let private received (task: Task) =
    completed task
    task.GetAwaiter().GetResult()

let private readyBeforeReturn =
    testCase "projection subscriptions: registration precedes immediate publication"
    <| fun _ ->
        let subscriptions, publish, stop = createHub 1024
        try
            use _standing = subscriptions.Subscribe(ignore)
            // No delay, polling, retry, or second publication can repair a missed
            // registration. Each notification must reach its just-created waiter.
            for number in 1..500 do
                let cid = Fcqrs.newCid ()
                let mutable callbacks = 0
                use awaiting = subscriptions.Subscribe(cid, 1, callback = (fun _ -> callbacks <- callbacks + 1))
                publish (Notification(cid, number))
                received awaiting.Task
                Expect.equal callbacks 1 "the notification was delivered exactly once"
        finally
            stop ()

let private boundedIsolation =
    testCase "projection subscriptions: blocked callbacks keep only their own newest queued events"
    <| fun _ ->
        let subscriptions, publish, stop = createHub 2
        use entered = new ManualResetEventSlim(false)
        use release = new ManualResetEventSlim(false)
        use fastReceived = new SemaphoreSlim(0)
        let cid = Fcqrs.newCid ()
        let slowEvents = ConcurrentQueue<int>()
        let fastEvents = ConcurrentQueue<int>()
        try
            use slow =
                subscriptions.Subscribe(cid, 3, callback = (fun evt ->
                    let number = (evt :?> Notification).Number
                    slowEvents.Enqueue number
                    if number = 0 then
                        entered.Set()
                        if not (release.Wait(TimeSpan.FromSeconds 5.0)) then
                            failwith "The test did not release the blocked callback."))
            use fast =
                subscriptions.Subscribe(cid, 13, callback = (fun evt ->
                    fastEvents.Enqueue((evt :?> Notification).Number)
                    fastReceived.Release() |> ignore))
            publish (Notification(cid, 0))
            Expect.isTrue (entered.Wait(TimeSpan.FromSeconds 5.0)) "the slow callback is blocked"
            Expect.isTrue (fastReceived.Wait(TimeSpan.FromSeconds 5.0)) "the fast subscriber received the first event"
            for number in 1..12 do
                publish (Notification(cid, number))
                Expect.isTrue (fastReceived.Wait(TimeSpan.FromSeconds 5.0)) "a blocked subscriber cannot block another subscriber"
            release.Set()
            received slow.Task
            received fast.Task
            Expect.sequenceEqual (slowEvents.ToArray()) [ 0; 11; 12 ] "the bounded queue retained its two newest events in order"
            Expect.sequenceEqual (fastEvents.ToArray()) [ 0..12 ] "the fast subscriber received every event in order"
        finally
            release.Set()
            stop ()

let private incompleteCancellation =
    testCase "projection subscriptions: disposal, cancellation and shutdown cannot satisfy an incomplete count"
    <| fun _ ->
        for ending in [ "dispose"; "cancel"; "shutdown" ] do
            let subscriptions, publish, stop = createHub 8
            use tokenSource = new CancellationTokenSource()
            use first = new ManualResetEventSlim(false)
            let cid = Fcqrs.newCid ()
            try
                use awaiting = subscriptions.Subscribe(cid, 2, callback = (fun _ -> first.Set()), cancellationToken = tokenSource.Token)
                publish (Notification(cid, 1))
                Expect.isTrue (first.Wait(TimeSpan.FromSeconds 5.0)) "one matching event was delivered"
                match ending with
                | "dispose" -> awaiting.Dispose()
                | "cancel" -> tokenSource.Cancel()
                | _ -> stop ()
                completed awaiting.Task
                Expect.isTrue awaiting.Task.IsCanceled (ending + " canceled the incomplete two-event wait")
            finally
                stop ()

let private failedCallbacks =
    testCase "projection subscriptions: filter and callback failures fault only their own subscription"
    <| fun _ ->
        let subscriptions, publish, stop = createHub 8
        try
            for failingFilter in [ true; false ] do
                let cid = Fcqrs.newCid ()
                let filter (_: IMessageWithCID) =
                    if failingFilter then raise (InvalidOperationException("filter failed"))
                    true
                let callback (_: IMessageWithCID) = raise (InvalidOperationException("callback failed"))
                use broken = subscriptions.Subscribe(cid, filter, 1, callback = callback)
                use healthy = subscriptions.Subscribe(cid, 1)
                publish (Notification(cid, 1))
                completed broken.Task
                Expect.isTrue broken.Task.IsFaulted "the failure was exposed to the caller"
                match broken.Task.Exception with
                | null -> failtest "The faulted subscription did not expose its exception."
                | failure ->
                    match failure.InnerException with
                    | :? InvalidOperationException -> ()
                    | _ -> failtest "The subscription did not preserve the original failure."
                received healthy.Task
        finally
            stop ()

let private alreadyCanceled =
    testCase "projection subscriptions: an already canceled token cannot start callbacks"
    <| fun _ ->
        let subscriptions, publish, stop = createHub 8
        use tokenSource = new CancellationTokenSource()
        tokenSource.Cancel()
        let mutable callbacks = 0
        try
            use awaiting = subscriptions.Subscribe((fun _ -> true), 1, callback = (fun _ -> Interlocked.Increment(&callbacks) |> ignore), cancellationToken = tokenSource.Token)
            publish (Notification(Fcqrs.newCid (), 1))
            completed awaiting.Task
            Expect.isTrue awaiting.Task.IsCanceled "cancellation before registration cancels the awaiter"
            Expect.equal callbacks 0 "no callback ran for the canceled subscription"
        finally
            stop ()

let tests =
    testList "projection notification coordination"
        [ readyBeforeReturn; boundedIsolation; incompleteCancellation; failedCallbacks; alreadyCanceled ]
