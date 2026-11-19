module ReservationTests

// The reservation workflow of samples/reservations, which applies only the newest of the changes
// that arrive while one is applied. WorkflowTest gives each step's outcome; no activity runs.

open Expecto
open FCQRS.Workflows
open Reservation

let private booking =
    { new IBooking with
        member _.Apply(_, _) = failwith "the harness runs no activity" }

let private change version final =
    { Reservation = "r-1"; Version = version; Final = final }

let private applies (flow: WorkflowTest<string, int>) =
    [ for line in flow.Summary do
          if line.StartsWith "scheduled" && line.EndsWith "apply" then
              line ]

let newestTest =
    test "changes that arrive while one is applied leave only the newest to apply" {
        let flow = WorkflowTest.Start(definition booking, "r-1")
        flow.Signal("changed", change 1 false)
        flow.Signal("changed", change 2 false)
        flow.Signal("changed", change 3 false)
        flow.Signal("changed", change 2 false)
        flow.Complete("apply", 1)
        flow.Complete("apply", 3)
        flow.Signal("changed", change 4 true)
        flow.Complete("apply", 4)
        Expect.equal flow.Result 4 "the final version"
        Expect.equal (List.length (applies flow)) 3 "versions 1, 3 and 4; never 2"
        flow.CheckReplay()
    }

let olderTest =
    test "a change at or below the applied version applies nothing" {
        let flow = WorkflowTest.Start(definition booking, "r-1")
        flow.Signal("changed", change 2 false)
        flow.Complete("apply", 2)
        flow.Signal("changed", change 1 false)
        flow.Signal("changed", change 2 false)
        Expect.equal (List.length (applies flow)) 1 "only version 2"
        Expect.isTrue (flow.Status.IsWaitingFor "changed") "it waits for the next change"
        flow.CheckReplay()
    }

let quietTest =
    test "after the quiet limit the workflow ends with the last version it applied" {
        let flow = WorkflowTest.Start(definition booking, "r-1")
        flow.Signal("changed", change 1 false)
        flow.Complete("apply", 1)
        flow.Advance quietLimit
        // The feed compares a refused change with this result: version 2 was never applied.
        Expect.equal flow.Result 1 "the last applied version"
        flow.CheckReplay()
    }

let failedApplyTest =
    test "a failed apply keeps the instance taking changes and applies the newest one later" {
        let flow = WorkflowTest.Start(definition booking, "r-1")
        flow.Signal("changed", change 1 false)
        flow.Fail("apply", exn "the booking system is down")
        Expect.isTrue (flow.Status.IsWaitingFor "changed") "it waits to try again; it has not faulted"
        // A change that arrives while it waits is taken, and is the one it applies next.
        flow.Signal("changed", change 2 false)
        Expect.equal (List.length (applies flow)) 2 "a second apply started"
        flow.Fail("apply", exn "still down")
        // With no newer change, it applies the same change again after retryAfter.
        flow.Advance retryAfter
        Expect.equal (List.length (applies flow)) 3 "a third apply started"
        flow.Complete("apply", 2)
        flow.Signal("changed", change 3 true)
        flow.Complete("apply", 3)
        Expect.equal flow.Result 3 "the final version"
        flow.CheckReplay()
    }

let tests = testList "reservation workflow" [ newestTest; olderTest; quietTest; failedApplyTest ]
