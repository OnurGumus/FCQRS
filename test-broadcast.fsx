// Standalone test of the BroadcastHub + Queue mechanism
// to isolate and measure the latency

#r "nuget: Akka.Streams, 1.5.31"
#r "nuget: Akkling.Streams, 0.17.0"

open System
open Akka.Actor
open Akka.Streams
open Akka.Streams.Dsl
open Akkling.Streams

type DataEvent = {
    Id: int
    Timestamp: DateTime
}

let system = ActorSystem.Create("TestSystem")
let materializer = ActorMaterializer.Create(system)

printfn "Setting up BroadcastHub + Queue pipeline..."

// Create the same pipeline as FCQRS Query.fs
let subQueue = Source.queue OverflowStrategy.Fail 1
let subSink = Sink.broadcastHub 1
let runnableGraph = subQueue |> Source.toMat subSink Keep.both
let queue, subRunnable = runnableGraph |> Graph.run materializer

printfn "Pipeline created. Starting producer thread...\n"

// Producer: Simulate the read journal publishing events
let producerTask = async {
    for i in 1..5 do
        let sw = System.Diagnostics.Stopwatch.StartNew()
        let event = { Id = i; Timestamp = DateTime.UtcNow }

        printfn "[PRODUCER-%d] About to OfferAsync at 0ms" i
        let result = queue.OfferAsync(event).Result
        printfn "[PRODUCER-%d] OfferAsync completed (result=%A) at %dms\n" i result sw.ElapsedMilliseconds

        System.Threading.Thread.Sleep(100) // Small delay between events
}

// Consumer 1: Subscribe with filter (like the CID subscription)
let consumer1Task = async {
    let sw = System.Diagnostics.Stopwatch.StartNew()
    printfn "[CONSUMER1] Starting subscription at 0ms"

    let filter = fun (event: DataEvent) ->
        printfn "[CONSUMER1] Filter called for event %d at %dms" event.Id sw.ElapsedMilliseconds
        event.Id = 3 // Only interested in event 3

    let sink = Sink.forEach (fun (event: DataEvent) ->
        printfn "[CONSUMER1] Received event %d in callback at %dms\n" event.Id sw.ElapsedMilliseconds
    )

    let subscription =
        subRunnable
        |> Source.viaMat KillSwitch.single Keep.right
        |> Source.filter filter
        |> Source.take 1
        |> Source.toMat sink Keep.both
        |> Graph.run materializer

    let ks, completionTask = subscription
    // Just wait without awaiting the task result
    System.Threading.Thread.Sleep(2000)
    printfn "[CONSUMER1] Subscription completed at %dms\n" sw.ElapsedMilliseconds
}

// Consumer 2: Subscribe to all events (like the logging subscription)
let consumer2Task = async {
    let sw = System.Diagnostics.Stopwatch.StartNew()
    printfn "[CONSUMER2] Starting subscription at 0ms\n"

    let sink = Sink.forEach (fun (event: DataEvent) ->
        printfn "[CONSUMER2] Received event %d at %dms" event.Id sw.ElapsedMilliseconds
    )

    let subscription =
        subRunnable
        |> Source.viaMat KillSwitch.single Keep.right
        |> Source.take 5
        |> Source.toMat sink Keep.both
        |> Graph.run materializer

    let ks, completionTask = subscription
    // Just wait without awaiting the task result
    System.Threading.Thread.Sleep(2000)
    printfn "[CONSUMER2] Subscription completed at %dms\n" sw.ElapsedMilliseconds
}

// Start consumers first (they need to subscribe before events arrive)
System.Threading.Thread.Sleep(500)

// Start all tasks
printfn "=== STARTING TEST ===\n"
Async.Start consumer1Task
Async.Start consumer2Task
System.Threading.Thread.Sleep(200) // Give consumers time to subscribe
Async.Start producerTask

// Wait for everything to complete
System.Threading.Thread.Sleep(3000)

printfn "\n=== TEST COMPLETE ==="
system.Terminate() |> Async.AwaitTask |> Async.RunSynchronously
