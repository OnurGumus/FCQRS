module WorkflowMachineTests

// The workflow journal records and the replay machine, tested without an actor system.

open System
open Expecto
open FCQRS.Serialization
open FCQRS.Workflows

type Quote = { Symbol: string; Price: decimal }

type Verdict =
    | Approved of limit: decimal
    | Declined of reason: string

let private roundTrip<'T when 'T: equality> (value: 'T) =
    Expect.equal (Payload.toValue<'T> (Payload.ofValue value)) value $"{typeof<'T>.Name} survives storage"

let private payloadTest =
    testCase "workflow journal: step values survive storage" <| fun _ ->
        roundTrip 42
        roundTrip "text"
        roundTrip ()
        roundTrip { Symbol = "ABC"; Price = 12.5m }
        roundTrip (Approved 100m)
        roundTrip (Declined "risk")
        roundTrip (DateTime(2026, 11, 11, 10, 30, 0, DateTimeKind.Utc))
        roundTrip (Some 3)
        roundTrip (None: int option)
        roundTrip [ 1; 2; 3 ]

let private recordTest =
    testCase "workflow journal: every record survives the journal serializer" <| fun _ ->
        let at = DateTime(2026, 11, 11, 10, 30, 0, DateTimeKind.Utc)
        let payload = Payload.ofValue { Symbol = "ABC"; Price = 1m }
        let error = { Kind = ErrorKind.Exhausted; ExceptionType = "System.TimeoutException"; Message = "slow" }
        let records =
            [ WorkflowRecord.Started(payload, at, Some "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01", None)
              WorkflowRecord.StepScheduled("3/1/0", "score", StepKind.Activity, at, Some "detail")
              WorkflowRecord.StepCompleted("0", "score", StepKind.Activity, payload, at)
              WorkflowRecord.StepFailed("1", "ask", StepKind.Activity, error, at)
              WorkflowRecord.SignalReceived("answer", payload, at, None)
              WorkflowRecord.CancelRequested at
              WorkflowRecord.Finished(payload, at)
              WorkflowRecord.Faulted(error, at)
              WorkflowRecord.Cancelled at ]
        for record in records do
            let copy = Serialization.decodeFromBytes<WorkflowRecord> (Serialization.encodeToBytes record)
            Expect.equal copy record "the record is read back unchanged"

let tests = testList "workflow machine" [ payloadTest; recordTest ]
