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
      Abandoned: ResizeArray<string>
      /// The most steps that were begun and unfinished at one time.
      mutable Widest: int
      Machine: Machine }

/// `complete` gives a begun step's outcome, or None to leave it waiting. `choose` picks which
/// waiting step finishes next, so a test decides the order in which parallel steps complete.
let private driveWith<'T>
    (choose: PendingStep list -> PendingStep)
    (prefix: WorkflowRecord list)
    (complete: PendingStep -> Result<Payload, StepError> option)
    (body: Workflow<'T>)
    =
    let history = History()
    for record in prefix do
        history.Apply record
    let machine = Machine(history, Payload.encode typeof<'T>, fun () -> at)
    let run = { Journal = ResizeArray prefix; Begun = ResizeArray(); Abandoned = ResizeArray(); Widest = 0; Machine = machine }
    let work = Queue<Emit>(machine.Run(boxed body))
    let waiting = ResizeArray<PendingStep>()
    let mutable busy = true
    while busy do
        while work.Count > 0 do
            match work.Dequeue() with
            | Store record ->
                run.Journal.Add record
                for emit in machine.Apply record do
                    work.Enqueue emit
            | Begin step ->
                run.Begun.Add step.Path
                waiting.Add step
                run.Widest <- max run.Widest waiting.Count
            | Abandon path ->
                run.Abandoned.Add path
                waiting.RemoveAll(fun step -> step.Path = path) |> ignore
        let ready = waiting |> Seq.filter (fun step -> (complete step).IsSome) |> List.ofSeq
        if ready.IsEmpty then
            busy <- false
        else
            let step = choose ready
            waiting.Remove step |> ignore
            match complete step with
            | Some(Ok result) -> work.Enqueue(Store(WorkflowRecord.StepCompleted(step.Path, step.Leaf.Name, step.Leaf.Kind, result, at)))
            | Some(Error error) -> work.Enqueue(Store(WorkflowRecord.StepFailed(step.Path, step.Leaf.Name, step.Leaf.Kind, error, at)))
            | None -> ()
    run

let private byName (complete: string -> Result<Payload, StepError> option) (step: PendingStep) =
    match step.Leaf.Kind with
    | StepKind.Timer -> Some(Ok(Payload.ofValue ()))
    | StepKind.Signal -> None
    | _ -> complete step.Leaf.Name

let private drive<'T> (prefix: WorkflowRecord list) (complete: string -> Result<Payload, StepError> option) (body: Workflow<'T>) =
    driveWith List.head prefix (byName complete) body

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

// ---------------------------------------------------------------------------
// Parallel branches, races and signals.
// ---------------------------------------------------------------------------

let private storedPaths (records: WorkflowRecord list) =
    set [ for record in records do
            match record with
            | WorkflowRecord.StepCompleted(path, _, _, _, _)
            | WorkflowRecord.StepFailed(path, _, _, _, _) -> path
            | _ -> () ]

/// Every prefix of `journal` replays to `expected` and begins no step the prefix had stored.
let private replaysFromEveryPrefix<'T when 'T: equality> (journal: WorkflowRecord list) (complete: string -> Result<Payload, StepError> option) (body: Workflow<'T>) (expected: 'T) =
    for length in 0 .. journal.Length do
        let prefix = List.truncate length journal
        let run = drive prefix complete body
        Expect.equal (finished run |> Option.map Payload.toValue<'T>) (Some expected) $"a restart after {length} records reaches the same result"
        Expect.isEmpty (run.Begun |> Seq.filter (storedPaths prefix).Contains) $"a restart after {length} records begins no stored step"

let rec private permutations (items: 'A list) : 'A list list =
    match items with
    | [] -> [ [] ]
    | _ -> [ for item in items do
                 for rest in permutations (List.except [ item ] items) -> item :: rest ]

let private fetch (name: string) : Workflow<int> = workflow {
    let! (first: int) = activity name (Retry.times 3) never
    let! (second: int) = activity (name + "-again") (Retry.times 3) never
    return first + second
}

let private three = workflow {
    let! values = all [ fetch "a"; fetch "b"; fetch "c" ]
    let! (last: int) = activity "sum" (Retry.times 3) never
    return values, last
}

let private threeValues (name: string) =
    match name with
    | "a" -> Some(Ok(Payload.ofValue 1))
    | "b" -> Some(Ok(Payload.ofValue 10))
    | "c" -> Some(Ok(Payload.ofValue 100))
    | "a-again" | "b-again" | "c-again" -> Some(Ok(Payload.ofValue 0))
    | "sum" -> Some(Ok(Payload.ofValue 111))
    | _ -> None

let private allTest =
    testCase "workflow machine: all gives results in branch order whatever order the branches finish in, and replays from any prefix" <| fun _ ->
        for order in permutations [ "0/0/"; "0/1/"; "0/2/" ] do
            // Finish the waiting step of the branch that comes first in `order`.
            let choose (ready: PendingStep list) =
                order |> List.pick (fun branch -> ready |> List.tryFind (fun step -> step.Path.StartsWith branch) |> Option.orElse (if branch = List.last order then Some(List.head ready) else None))
            let live = driveWith choose [] (byName threeValues) three
            Expect.equal (finished live |> Option.map Payload.toValue<int list * int>) (Some([ 1; 10; 100 ], 111)) $"order {order}"
            Expect.equal (set live.Begun) (set [ "0/0/0"; "0/0/1"; "0/1/0"; "0/1/1"; "0/2/0"; "0/2/1"; "1" ]) "branch steps have paths under the block and branch"
            replaysFromEveryPrefix (List.ofSeq live.Journal) threeValues three ([ 1; 10; 100 ], 111)

let private allFailureTest =
    testCase "workflow machine: a failing branch fails all once and abandons the other branches" <| fun _ ->
        let body = workflow {
            try
                let! (_: int list) = all [ fetch "a"; fetch "b"; fetch "c" ]
                return "no failure"
            with :? WorkflowStepFailedException as failed ->
                let! (after: int) = activity "after" Retry.none never
                return $"{failed.StepName} {after}"
        }
        let complete name =
            match name with
            | "b" -> Some(Error { Kind = ErrorKind.Exhausted; ExceptionType = "System.Exception"; Message = "down" })
            | "after" -> Some(Ok(Payload.ofValue 5))
            | _ -> None
        let live = drive [] complete body
        Expect.equal (finished live |> Option.map Payload.toValue<string>) (Some "b 5") "the body handles the branch failure and continues"
        Expect.equal (set live.Abandoned) (set [ "0/0/0"; "0/2/0" ]) "the other branches' steps are abandoned"
        let journal = List.ofSeq live.Journal
        for length in 0 .. journal.Length do
            let run = drive (List.truncate length journal) complete body
            Expect.equal (finished run |> Option.map Payload.toValue<string>) (Some "b 5") $"a restart after {length} records reaches the same result"
        // Once the failure is stored, a restart begins nothing in the abandoned branches.
        let afterFailure = journal |> List.findIndex (function WorkflowRecord.StepFailed _ -> true | _ -> false)
        let replayed = drive (List.truncate (afterFailure + 1) journal) complete body
        Expect.isEmpty (replayed.Begun |> Seq.filter (fun path -> path.StartsWith "0/")) "abandoned branches are not begun again"

let private raceBody = workflow {
    let! winner = race [ fetch "slow"; fetch "fast" ]
    let! (last: int) = activity "after" Retry.none never
    return winner + last
}

let private raceTest =
    testCase "workflow machine: a race stores its winner, abandons the rest, and replay runs only the winner" <| fun _ ->
        let complete name =
            match name with
            | "fast" -> Some(Ok(Payload.ofValue 7))
            | "fast-again" -> Some(Ok(Payload.ofValue 1))
            | "after" -> Some(Ok(Payload.ofValue 100))
            | _ -> None
        let live = drive [] complete raceBody
        Expect.equal (finished live |> Option.map Payload.toValue<int>) (Some 108) "the fast branch decided the result"
        let winner =
            live.Journal |> Seq.pick (function WorkflowRecord.StepCompleted("0", "race", StepKind.Race, result, _) -> Some(Payload.toValue<int> result) | _ -> None)
        Expect.equal winner 1 "the winner's index is stored at the race's path"
        Expect.contains live.Abandoned "0/0/0" "the slow branch's step is abandoned"
        let journal = List.ofSeq live.Journal
        // After the winner is stored, even a slow branch that could now finish does not run.
        let everything name = match complete name with Some outcome -> Some outcome | None -> Some(Ok(Payload.ofValue 1000))
        let decidedAt = journal |> List.findIndex (function WorkflowRecord.StepCompleted(_, "race", _, _, _) -> true | _ -> false)
        for length in decidedAt + 1 .. journal.Length do
            let run = drive (List.truncate length journal) everything raceBody
            Expect.equal (finished run |> Option.map Payload.toValue<int>) (Some 108) $"a restart after {length} records keeps the stored winner"
            Expect.isEmpty (run.Begun |> Seq.filter (fun path -> path.StartsWith "0/0/")) "the losing branch is not begun"
        // Before the winner is stored, a restart decides again.
        for length in 0 .. decidedAt do
            let run = drive (List.truncate length journal) complete raceBody
            Expect.equal (finished run |> Option.map Payload.toValue<int>) (Some 108) $"a restart after {length} records decides the race again"

let private limitTest =
    testCase "workflow machine: allAtMost keeps to its limit and replays from any prefix" <| fun _ ->
        let body = workflow {
            let! doubled = allAtMost 2 [ 1; 2; 3; 4; 5 ] (fun item -> workflow {
                let! (value: int) = activity $"double-{item}" (Retry.times 3) never
                return value })
            return doubled
        }
        let complete (name: string) = Some(Ok(Payload.ofValue (2 * int (name.Substring 7))))
        // The newest begun step finishes first, so branches finish out of order.
        let live = driveWith List.last [] (byName complete) body
        Expect.equal (finished live |> Option.map Payload.toValue<int list>) (Some [ 2; 4; 6; 8; 10 ]) "results are in item order"
        Expect.equal live.Widest 2 "no more than two branches were running"
        replaysFromEveryPrefix (List.ofSeq live.Journal) complete body [ 2; 4; 6; 8; 10 ]
        for length in 0 .. live.Journal.Count do
            let run = driveWith List.last (List.truncate length (List.ofSeq live.Journal)) (byName complete) body
            Expect.isLessThanOrEqual run.Widest 2 $"a restart after {length} records keeps to the limit"

let private andTest =
    testCase "workflow machine: and! runs two workflows together" <| fun _ ->
        let body = workflow {
            let! (left: int) = activity "left" Retry.none never
            and! (right: string) = activity "right" Retry.none never
            return $"{left}{right}"
        }
        let complete name =
            match name with
            | "left" -> Some(Ok(Payload.ofValue 4))
            | "right" -> Some(Ok(Payload.ofValue "2"))
            | _ -> None
        let live = drive [] complete body
        Expect.equal (finished live |> Option.map Payload.toValue<string>) (Some "42") "both results arrive"
        Expect.equal (List.ofSeq live.Begun) [ "0/0/0"; "0/1/0" ] "both began before either finished"

let private andThreeTest =
    testCase "workflow machine: three and! branches run together" <| fun _ ->
        let body = workflow {
            let! (left: int) = activity "left" Retry.none never
            and! (middle: string) = activity "middle" Retry.none never
            and! (right: bool) = activity "right" Retry.none never
            return $"{left}{middle}{right}"
        }
        let complete name =
            match name with
            | "left" -> Some(Ok(Payload.ofValue 4))
            | "middle" -> Some(Ok(Payload.ofValue "2"))
            | "right" -> Some(Ok(Payload.ofValue true))
            | _ -> None
        let live = drive [] complete body
        Expect.equal (finished live |> Option.map Payload.toValue<string>) (Some "42True") "every result arrives in order"
        Expect.equal (Seq.length live.Begun) 3 "all three began before any finished"
        // Replay the stored steps without the stored result: no step runs again.
        let steps = live.Journal |> Seq.filter (function WorkflowRecord.Finished _ -> false | _ -> true) |> List.ofSeq
        let replayed = drive steps (fun _ -> None) body
        Expect.equal (finished replayed |> Option.map Payload.toValue<string>) (Some "42True") "replay gives the stored results"
        Expect.isEmpty replayed.Begun "replay begins no step"

let private recursionTest =
    testCase "workflow machine: deep recursion keeps a small stack, live and on replay" <| fun _ ->
        let depth = 50_000
        // Work after the recursive call: each level leaves a pending let! behind.
        let rec nested n = workflow {
            if n = 0 then
                return 0
            else
                let! (x: int) = activity "tick" Retry.none never
                let! rest = nested (n - 1)
                return x + rest
        }
        // The same, inside a try/finally at every level.
        let rec guarded n = workflow {
            if n = 0 then
                return 0
            else
                try
                    let! (x: int) = activity "tick" Retry.none never
                    let! rest = guarded (n - 1)
                    return x + rest
                finally
                    ()
        }
        let rec tail n total = workflow {
            if n = 0 then
                return total
            else
                let! (x: int) = activity "tick" Retry.none never
                return! tail (n - 1) (total + x)
        }
        let ticks name = if name = "tick" then Some(Ok(Payload.ofValue 1)) else None
        let results = ResizeArray()
        // A 256 KB stack: a body whose stack grew with its depth would overflow here.
        let thread =
            System.Threading.Thread(
                (fun () ->
                    for name, body in [ "nested", nested depth; "guarded", guarded depth; "tail", tail depth 0 ] do
                        let live = drive [] ticks body
                        let steps =
                            live.Journal |> Seq.filter (function WorkflowRecord.Finished _ -> false | _ -> true) |> List.ofSeq
                        let replayed = drive steps (fun _ -> None) body
                        results.Add((name, finished live |> Option.map Payload.toValue<int>, finished replayed |> Option.map Payload.toValue<int>, replayed.Begun.Count))),
                256 * 1024)
        thread.Start()
        thread.Join()
        for name, live, replayed, begun in results do
            Expect.equal live (Some depth) $"{name}: every level returns"
            Expect.equal replayed (Some depth) $"{name}: replay gives the same result"
            Expect.equal begun 0 $"{name}: replay begins no step"
        Expect.equal results.Count 3 "every body ran"

let private signal (name: string) (value: 'T) = WorkflowRecord.SignalReceived(name, Payload.ofValue value, at, None)

let private signalTest =
    testCase "workflow machine: stored signals are consumed oldest first, and a timed-out wait consumes none" <| fun _ ->
        let body = workflow {
            let! (first: string) = waitSignal "answer"
            let! (second: string option) = waitSignalFor "answer" (TimeSpan.FromHours 1.0)
            let! (third: string option) = waitSignalFor "answer" (TimeSpan.FromHours 1.0)
            return first, second, third
        }
        // Two signals arrive before the body starts.
        let early = drive [ signal "answer" "one"; signal "other" 5; signal "answer" "two" ] (fun _ -> None) body
        let waits =
            [ for record in early.Journal do
                match record with
                | WorkflowRecord.StepCompleted(path, "answer", StepKind.Signal, result, _) -> path, Payload.toValue<string> result
                | _ -> () ]
        Expect.equal waits [ "0", "one"; "1", "two" ] "the waits consume the signals in arrival order"
        Expect.isNone (finished early) "the third wait has no signal"
        let journal = List.ofSeq early.Journal
        // The third wait times out; then a late signal arrives and stays stored.
        let timedOut = journal @ [ WorkflowRecord.StepCompleted("2", "answer", StepKind.Signal, Payload.timedOut, at) ]
        let done' = drive timedOut (fun _ -> None) body
        Expect.equal (finished done' |> Option.map Payload.toValue<string * string option * string option>) (Some("one", Some "two", None)) "the timed-out wait gives None"
        // Replay from every prefix assigns the same signals to the same waits.
        for length in 0 .. timedOut.Length do
            let run = drive (List.truncate length timedOut) (fun _ -> None) body
            let again =
                [ for record in run.Journal do
                    match record with
                    | WorkflowRecord.StepCompleted(path, "answer", StepKind.Signal, result, _) when result <> Payload.timedOut -> path, Payload.toValue<string> result
                    | _ -> () ]
            Expect.isTrue (List.forall (fun pair -> List.contains pair waits) again) $"a restart after {length} records keeps each wait's signal: {again}"

let private liveSignalTest =
    testCase "workflow machine: a signal that arrives during a wait completes it" <| fun _ ->
        let body = workflow {
            let! (answer: int) = waitSignal "answer"
            return answer * 2
        }
        let waitingRun = drive [] (fun _ -> None) body
        Expect.isNone (finished waitingRun) "the body waits"
        let arrived = drive (List.ofSeq waitingRun.Journal @ [ signal "answer" 21 ]) (fun _ -> None) body
        Expect.equal (finished arrived |> Option.map Payload.toValue<int>) (Some 42) "the stored signal completes the wait"

let private unreadableTest =
    testCase "workflow machine: a stored result the step's type cannot read diverges, and an unstorable result faults" <| fun _ ->
        let journal =
            [ WorkflowRecord.StepCompleted("0", "now", StepKind.Value, Payload.ofValue at, at)
              WorkflowRecord.StepScheduled("1", "price", StepKind.Activity, at, None)
              WorkflowRecord.StepCompleted("1", "price", StepKind.Activity, Payload.ofValue "twelve", at) ]
        let typed = workflow {
            let! (_: DateTime) = now
            let! (price: decimal) = activity "price" (Retry.times 3) never
            return price
        }
        Expect.throwsT<WorkflowDivergedException> (fun () -> drive journal orderValues typed |> ignore) "'price' is read as a decimal"
        let unstorable = workflow {
            let! (_: DateTime) = now
            // The serializer refuses a System.Type.
            return typeof<int>
        }
        match Seq.last (drive [] (fun _ -> None) unstorable).Journal with
        | WorkflowRecord.Faulted(error, _) -> Expect.equal error.Kind ErrorKind.BodyFailed "a result that cannot be stored faults the workflow"
        | other -> failtest $"expected a fault, found {other}"

let private farTimerTest =
    testCase "workflow machine: a timer further away than time can represent is due at the end of time" <| fun _ ->
        let body = workflow {
            do! sleep TimeSpan.MaxValue
            return 1
        }
        let journal = (driveWith List.head [] (fun _ -> None) body).Journal
        match Seq.head journal with
        | WorkflowRecord.StepScheduled(_, "sleep", StepKind.Timer, _, Some due) ->
            Expect.equal (DateTime.Parse(due, null, Globalization.DateTimeStyles.RoundtripKind)) DateTime.MaxValue "the due time is capped"
        | other -> failtest $"expected a scheduled timer, found {other}"

let private oldCode = workflow {
    let! (price: decimal) = activity "price" (Retry.times 3) never
    let! (stock: int) = activity "stock" (Retry.times 3) never
    return $"{price}:{stock}"
}

/// The same workflow after a fraud check was added between its two steps.
let private newCode = workflow {
    let! (price: decimal) = activity "price" (Retry.times 3) never
    let! change = version "add-fraud-check" 2
    let! checkedBy = workflow {
        if change >= 2 then
            let! (verdict: string) = activity "fraud" (Retry.times 3) never
            return verdict
        else
            return "unchecked"
    }
    let! (stock: int) = activity "stock" (Retry.times 3) never
    return $"{price}:{stock}:{checkedBy}"
}

let private versionValues name =
    match name with
    | "price" -> Some(Ok(Payload.ofValue 12.5m))
    | "stock" -> Some(Ok(Payload.ofValue 7))
    | "fraud" -> Some(Ok(Payload.ofValue "clear"))
    | _ -> None

let private versionTest =
    testCase "workflow machine: a version step lets running instances keep their old steps and new instances take new ones" <| fun _ ->
        let fresh = drive [] versionValues newCode
        Expect.equal (finished fresh |> Option.map Payload.toValue<string>) (Some "12.5:7:clear") "a new instance takes the new branch"
        Expect.contains
            (List.ofSeq fresh.Journal |> List.choose (function WorkflowRecord.StepCompleted(path, name, StepKind.Version, result, _) -> Some(path, name, Payload.toValue<int> result) | _ -> None))
            ("1@add-fraud-check", "add-fraud-check", 2)
            "its version is stored beside the step sequence"
        replaysFromEveryPrefix (List.ofSeq fresh.Journal) versionValues newCode "12.5:7:clear"

        let old = (drive [] versionValues oldCode).Journal |> List.ofSeq
        // An instance that had begun its second step under the old code keeps the old steps.
        for length in 3 .. old.Length - 1 do
            let upgraded = drive (List.truncate length old) versionValues newCode
            Expect.equal (finished upgraded |> Option.map Payload.toValue<string>) (Some "12.5:7:unchecked") $"an instance {length} records into the old code stays on it"
            Expect.isFalse (upgraded.Journal |> Seq.exists (function WorkflowRecord.StepCompleted(_, _, StepKind.Version, _, _) -> true | _ -> false)) "and stores no version"
        // An instance that had not reached the point takes the new branch.
        let early = drive (List.truncate 2 old) versionValues newCode
        Expect.equal (finished early |> Option.map Payload.toValue<string>) (Some "12.5:7:clear") "an instance that had not passed the point takes the new steps"

let private versionLoopTest =
    testCase "workflow machine: a version step in a loop stores a value for each iteration" <| fun _ ->
        let body = workflow {
            let mutable total = 0
            for _ in 1 .. 3 do
                let! change = version "loop-change" 2
                let! (value: int) = activity "next" Retry.none never
                total <- total + value * change
            return total
        }
        let run = drive [] (fun _ -> Some(Ok(Payload.ofValue 1))) body
        Expect.equal (finished run |> Option.map Payload.toValue<int>) (Some 6) "each iteration saw version 2"
        let markers = run.Journal |> Seq.choose (function WorkflowRecord.StepCompleted(path, _, StepKind.Version, _, _) -> Some path | _ -> None) |> List.ofSeq
        Expect.equal markers [ "0@loop-change"; "1@loop-change"; "2@loop-change" ] "one marker per iteration"
        replaysFromEveryPrefix (List.ofSeq run.Journal) (fun _ -> Some(Ok(Payload.ofValue 1))) body 6

let private wrongSignalTest =
    testCase "workflow machine: a signal the wait cannot read fails that wait and is consumed" <| fun _ ->
        let body = workflow {
            let! first = workflow {
                try
                    let! (answer: string) = waitSignal "answer"
                    return answer
                with :? WorkflowStepFailedException as failed ->
                    return string failed.Error.Kind
            }
            let! (second: string) = waitSignal "answer"
            return first, second
        }
        // The first signal carries a number where the wait reads a string.
        let run = drive [ signal "answer" 42; signal "answer" "two" ] (fun _ -> None) body
        Expect.equal (finished run |> Option.map Payload.toValue<string * string>) (Some("NotRetryable", "two")) "the wait fails, and the next wait gets the next signal"
        // From any point after both signals were stored, replay gives each wait the same outcome.
        let journal = List.ofSeq run.Journal
        for length in 2 .. journal.Length do
            let replayed = drive (List.truncate length journal) (fun _ -> None) body
            Expect.equal (finished replayed |> Option.map Payload.toValue<string * string>) (Some("NotRetryable", "two")) $"a restart after {length} records"

let private versionInBranchTest =
    testCase "workflow machine: a version step at the end of a branch gives 1 to an instance that passed the block" <| fun _ ->
        let branch (guarded: bool) (name: string) = workflow {
            let! (value: int) = activity name (Retry.times 3) never
            if guarded then
                let! change = version "check-branch" 2
                if change >= 2 then
                    let! (extra: int) = activity "extra" (Retry.times 3) never
                    return value + extra
                else
                    return value
            else
                return value
        }
        let code (guarded: bool) = workflow {
            let! values = all [ branch guarded "a"; branch false "b" ]
            let! (last: int) = activity "sum" (Retry.times 3) never
            return List.sum values + last
        }
        let values name =
            match name with
            | "a" -> Some(Ok(Payload.ofValue 1))
            | "b" -> Some(Ok(Payload.ofValue 2))
            | "sum" -> Some(Ok(Payload.ofValue 10))
            | "extra" -> Some(Ok(Payload.ofValue 100))
            | _ -> None
        let old = (drive [] values (code false)).Journal |> List.ofSeq
        // The old instance finished both branches and began the step after the block.
        let passedBlock = old |> List.findIndex (function WorkflowRecord.StepScheduled("1", _, _, _, _) -> true | _ -> false)
        let upgraded = drive (List.truncate (passedBlock + 1) old) values (code true)
        Expect.equal (finished upgraded |> Option.map Payload.toValue<int>) (Some 13) "the instance keeps the old branch"
        Expect.isFalse (upgraded.Begun |> Seq.exists (fun path -> path = "0/0/1")) "and does not run the new step"
        Expect.equal (finished (drive [] values (code true)) |> Option.map Payload.toValue<int>) (Some 113) "a new instance runs it"

let tests =
    testList "workflow machine"
        [ payloadTest; recordTest; liveTest; prefixTest; failureTest; unhandledTest; divergedTest; longTest; storedValueTest
          allTest; allFailureTest; raceTest; limitTest; andTest; andThreeTest; recursionTest; signalTest; liveSignalTest; unreadableTest; farTimerTest; versionTest; versionLoopTest; wrongSignalTest; versionInBranchTest ]
