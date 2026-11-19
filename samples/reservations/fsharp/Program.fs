open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Actor
open FCQRS.FSharp
open FCQRS.Workflows
open Reservation

// Each run starts with an empty journal, so its output is the same.
let database = Path.Combine(AppContext.BaseDirectory, "reservations.db")
for file in [ database; database + "-wal"; database + "-shm" ] do
    File.Delete file

let logging = LoggerFactory.Create(fun _ -> ())
let configuration = ConfigurationBuilder().Build()
let connection = Fcqrs.connect DBType.Sqlite $"Data Source={database}"
let api = Fcqrs.actor configuration logging (Some connection) "reservations"

// A stand-in for the booking system. It holds the first change until the
// program releases it, so that later changes arrive while it is applied.
type Booking() =
    let started = TaskCompletionSource()
    let release = TaskCompletionSource()
    let applied = ConcurrentQueue<int>()
    member _.Started = started.Task
    member _.Release() = release.TrySetResult() |> ignore
    member _.Applied = applied
    interface IBooking with
        member _.Apply(_, change) =
            task {
                started.TrySetResult() |> ignore
                do! release.Task
                applied.Enqueue change.Version
                printfn "applied %s version %d" change.Reservation change.Version
                return change.Version
            }

let booking = Booking()
let reservations = Workflows.register api (definition booking)

// docs:feed
// What a projection's handler does with each reservation event. A handler
// that raises stops the projection, so feed gives a value for a change that
// the workflow refused.
let feed (change: ReservationChanged) =
    async {
        let id = change.Reservation
        // A start for an instance that exists stores nothing.
        let! _ = reservations.Start id id
        try
            do! Workflows.signal reservations id "changed" change
            return Taken
        with :? InvalidOperationException ->
            // The workflow has ended, after a final change or after the
            // quiet limit. Its result is the last version it applied.
            let! outcome = reservations.Result id (TimeSpan.FromSeconds 30.0)
            match outcome with
            | WorkflowOutcome.Finished applied when change.Version <= applied ->
                return Repeated
            | _ -> return Late
    }
// docs:end

let change version final =
    { Reservation = "r-1"; Version = version; Final = final }

let send version final =
    feed (change version final) |> Async.RunSynchronously

// Version 1 starts the workflow. Versions 2 and 3, and version 2 once more,
// arrive while version 1 is applied.
send 1 false |> ignore
booking.Started.Wait()
for version in [ 2; 3; 2 ] do
    send version false |> ignore
booking.Release()

// Version 4, the final one, arrives after version 3 is applied.
while not (Seq.contains 3 booking.Applied) do
    Thread.Sleep 50
send 4 true |> ignore

match reservations.Result "r-1" (TimeSpan.FromSeconds 30.0)
      |> Async.RunSynchronously with
| WorkflowOutcome.Finished version -> printfn "r-1 ended at version %d" version
| other -> printfn "r-1 ended otherwise: %A" other

// After the workflow ended, the projection hands version 2 over again, and
// a version 5 arrives that nothing will apply.
printfn "version 2 again: %A" (send 2 false)
printfn "version 5: %A" (send 5 false)

api.Stop().Wait()
