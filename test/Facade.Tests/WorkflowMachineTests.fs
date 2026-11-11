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

// ---------------------------------------------------------------------------
// The machine. `drive` plays the actor: it stores every record the machine asks for, and
// completes each activity it is asked to begin with the value `complete` gives for its name.
// ---------------------------------------------------------------------------

open System.Collections.Generic
open System.Threading.Tasks

let private at = DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc)

let private boxed (body: Workflow<'T>) : Workflow<objnull> = workflow {
    let! value = body
    return box value
}

type private Run =
    { Journal: ResizeArray<WorkflowRecord>
      Begun: ResizeArray<string>
      Machine: Machine }

let private drive<'T> (prefix: WorkflowRecord list) (complete: string -> Result<Payload, StepError> option) (body: Workflow<'T>) =
    let history = Dictionary<string, StepState>()
    for record in prefix do
        History.apply history record
    let machine = Machine(history, typeof<'T>, fun () -> at)
    let journal = ResizeArray prefix
    let begun = ResizeArray<string>()
    let work = Queue<Emit>(machine.Run(boxed body))
    while work.Count > 0 do
        match work.Dequeue() with
        | Store record ->
            journal.Add record
            for emit in machine.Apply record do
                work.Enqueue emit
        | Begin step ->
            begun.Add step.Path
            let outcome =
                match step.Leaf.Kind with
                | StepKind.Timer -> Some(Ok(Payload.ofValue ()))
                | _ -> complete step.Leaf.Name
            match outcome with
            | Some(Ok result) -> work.Enqueue(Store(WorkflowRecord.StepCompleted(step.Path, step.Leaf.Name, step.Leaf.Kind, result, at)))
            | Some(Error error) -> work.Enqueue(Store(WorkflowRecord.StepFailed(step.Path, step.Leaf.Name, step.Leaf.Kind, error, at)))
            | None -> ()
    { Journal = journal; Begun = begun; Machine = machine }

let private never (_: AttemptContext) : Task<'T> = failwith "the machine does not run activities"

let private order = workflow {
    let! started = now
    let! price = activity "price" (Retry.times 3) never
    do! sleep (TimeSpan.FromHours 1.0)
    let! (stock: int) = activity "stock" (Retry.times 3) never
    return $"{started:HH}:{price}:{stock}"
}

let private orderValues name =
    match name with
    | "price" -> Some(Ok(Payload.ofValue 12.5m))
    | "stock" -> Some(Ok(Payload.ofValue 7))
    | _ -> None

let private finished (run: Run) =
    run.Journal |> Seq.tryPick (function WorkflowRecord.Finished(result, _) -> Some result | _ -> None)

let private liveTest =
    testCase "workflow machine: a body runs step by step, and each step is stored before the next starts" <| fun _ ->
        let run = drive [] orderValues order
        let shape =
            [ for record in run.Journal ->
                match record with
                | WorkflowRecord.StepScheduled(path, name, _, _, _) -> $"scheduled {path} {name}"
                | WorkflowRecord.StepCompleted(path, name, _, _, _) -> $"completed {path} {name}"
                | WorkflowRecord.Finished _ -> "finished"
                | other -> string other ]
        Expect.equal
            shape
            [ "completed 0 now"
              "scheduled 1 price"; "completed 1 price"
              "scheduled 2 sleep"; "completed 2 sleep"
              "scheduled 3 stock"; "completed 3 stock"
              "finished" ]
            "paths count up in the order the body reaches the steps"
        Expect.equal (finished run |> Option.map Payload.toValue<string>) (Some "10:12.5:7") "the body's result is stored"
        Expect.equal (List.ofSeq run.Begun) [ "1"; "2"; "3" ] "each step began once"

let private prefixTest =
    testCase "workflow machine: replay from any prefix of the journal reaches the same result and repeats no stored step" <| fun _ ->
        let full = (drive [] orderValues order).Journal |> List.ofSeq
        for length in 0 .. full.Length - 1 do
            let prefix = List.truncate length full
            let stored =
                set [ for record in prefix do
                        match record with
                        | WorkflowRecord.StepCompleted(path, _, _, _, _) -> path
                        | _ -> () ]
            let run = drive prefix orderValues order
            Expect.equal (List.ofSeq run.Journal) full $"a restart after {length} records writes the same journal"
            Expect.isEmpty (run.Begun |> Seq.filter stored.Contains) $"a restart after {length} records begins no stored step"

let private failureTest =
    testCase "workflow machine: a failed step raises the same exception live and on replay" <| fun _ ->
        let body = workflow {
            try
                let! (price: decimal) = activity "price" Retry.none never
                return $"{price}"
            with :? WorkflowStepFailedException as failed ->
                return $"{failed.StepName} {failed.Error.Kind}"
        }
        let fail _ = Some(Error { Kind = ErrorKind.Exhausted; ExceptionType = "System.TimeoutException"; Message = "slow" })
        let live = drive [] fail body
        Expect.equal (finished live |> Option.map Payload.toValue<string>) (Some "price Exhausted") "the body handles the failure"
        let replayed = drive (List.truncate 2 (List.ofSeq live.Journal)) (fun _ -> None) body
        Expect.equal (finished replayed |> Option.map Payload.toValue<string>) (Some "price Exhausted") "replay raises it again"
        Expect.isEmpty replayed.Begun "replay does not run the failed step"

let private unhandledTest =
    testCase "workflow machine: an exception the body does not handle faults the workflow" <| fun _ ->
        let body = workflow {
            let! (_: DateTime) = now
            failwith "broken"
            return 1
        }
        let run = drive [] (fun _ -> None) body
        match Seq.last run.Journal with
        | WorkflowRecord.Faulted(error, _) ->
            Expect.equal error.Kind ErrorKind.BodyFailed "the fault names the body"
            Expect.equal error.Message "broken" "the fault keeps the message"
        | other -> failtest $"expected a fault, found {other}"

let private divergedTest =
    testCase "workflow machine: a step renamed after it was stored diverges" <| fun _ ->
        let journal = (drive [] orderValues order).Journal |> List.ofSeq
        let renamed = workflow {
            let! (_: DateTime) = now
            let! (price: decimal) = activity "quote" (Retry.times 3) never
            return string price
        }
        Expect.throwsT<WorkflowDivergedException> (fun () -> drive journal orderValues renamed |> ignore) "the journal holds 'price' at step 1"

let private longTest =
    testCase "workflow machine: a journal of 20,000 steps replays without growing the stack" <| fun _ ->
        let body = workflow {
            let mutable total = 0
            for _ in 1 .. 20000 do
                let! (value: int) = activity "next" Retry.none never
                total <- total + value
            // A long loop that reaches no step.
            let mutable spins = 0
            while spins < 1_000_000 do
                spins <- spins + 1
            return total
        }
        let journal = (drive [] (fun _ -> Some(Ok(Payload.ofValue 1))) body).Journal |> List.ofSeq
        let replayed = drive journal (fun _ -> None) body
        Expect.isEmpty replayed.Begun "replay begins nothing"
        Expect.equal (finished replayed |> Option.map Payload.toValue<int>) (Some 20000) "the result is the stored one"

let private storedValueTest =
    testCase "workflow machine: now, newGuid and random return their stored value on replay" <| fun _ ->
        let body = workflow {
            let! id = newGuid
            let! roll = random
            let! (_: int) = activity "wait" Retry.none never
            return id, roll
        }
        let first = drive [] (fun _ -> None) body
        let second = drive (List.ofSeq first.Journal) (fun _ -> Some(Ok(Payload.ofValue 1))) body
        let stored (run: Run) =
            [ for record in run.Journal do
                match record with
                | WorkflowRecord.StepCompleted(_, ("newGuid" | "random"), _, result, _) -> result.Json
                | _ -> () ]
        Expect.equal (finished second |> Option.map Payload.toValue<Guid * float> |> Option.map (fun (id, roll) -> [ $"\"{id}\""; string roll ]) |> Option.map (List.map (fun text -> text.Length > 0)))
            (Some [ true; true ]) "the body finished with both values"
        Expect.equal (stored second) (stored first) "the values were stored once and reused"
        let id, roll = (finished second).Value |> Payload.toValue<Guid * float>
        Expect.equal [ Payload.ofValue(id).Json; Payload.ofValue(roll).Json ] (stored first) "the body saw the stored values"

let tests =
    testList "workflow machine"
        [ payloadTest; recordTest; liveTest; prefixTest; failureTest; unhandledTest; divergedTest; longTest; storedValueTest ]
