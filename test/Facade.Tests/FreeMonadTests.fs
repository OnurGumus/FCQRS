module FreeMonadTests

// The free monad of FCQRS.Model: what its builders accept, that binds nested to any depth stay
// cheap, and that a program runs as a workflow whose steps store the interpreter's answers.

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Expecto
open FCQRS.Model.FreeMonad
open FCQRS.Workflows

type Instruction =
    | Log of string
    | Find of int
    | Price of string

/// Answers every instruction and records the order it saw them in.
let private interpreter () =
    let seen = List<Instruction>()

    let handle (instruction: Instruction) : objnull =
        seen.Add instruction

        match instruction with
        | Log _ -> box ()
        | Find id -> box (if id > 0 then Some $"item-{id}" else None)
        | Price _ -> box 12.5m

    seen, handle

let private log (text: string) : Free<Instruction, unit> = Eff.perform (Log text)
let private find (id: int) : Free<Instruction, string option> = Eff.perform (Find id)
let private price (symbol: string) : Free<Instruction, decimal> = Eff.perform (Price symbol)

let answersTest =
    test "a unit, a None and a value answer reach the program" {
        let seen, handle = interpreter ()

        let program =
            free {
                do! log "start"
                let! missing = find 0
                let! found = find 7
                let! unitPrice = price "ABC"
                return missing, found, unitPrice
            }

        Expect.equal (Free.run handle program) (None, Some "item-7", 12.5m) "each answer has its type"
        Expect.equal (List.ofSeq seen) [ Log "start"; Find 0; Find 7; Price "ABC" ] "in program order"
    }

let wrongAnswerTest =
    test "an answer of another type raises and names both types" {
        let program = free { return! price "ABC" }
        let error = Expect.throwsC (fun () -> Free.run (fun _ -> box "text") program |> ignore) id
        Expect.stringContains error.Message "System.String" "the type it got"
        Expect.stringContains error.Message "System.Decimal" "the type it expected"
    }

/// `depth` levels, each of which waits for the level below and then performs an instruction.
let rec private nested (depth: int) : Free<Instruction, int> =
    free {
        if depth = 0 then
            return 0
        else
            let! below = nested (depth - 1)
            do! log "level"
            return below + 1
    }

let nestedBindTest =
    test "binds nested 300000 levels deep run without growing the stack" {
        // Building the program recurses once per level, as any recursive function does, so it
        // gets a large stack. Running it must not need one: a continuation that called one
        // closure per level overflowed the stack here, and took time quadratic in the depth.
        let mutable outcome = Error(exn "not run")
        let mutable instructions = 0

        let thread =
            Thread(
                (fun () ->
                    try
                        let program = nested 300000

                        let handle (_: Instruction) : objnull =
                            instructions <- instructions + 1
                            box ()

                        // Run on an ordinary stack.
                        let result = Task.Run(fun () -> Free.run handle program).Result
                        outcome <- Ok result
                    with error ->
                        outcome <- Error error),
                256 * 1024 * 1024
            )

        thread.Start()
        Expect.isTrue (thread.Join(TimeSpan.FromSeconds 60.0)) "it finishes; nested binds must not cost more per level"
        Expect.equal outcome (Ok 300000 |> Result.mapError (fun (e: exn) -> e)) "every level ran"
        Expect.equal instructions 300000 "one instruction per level"
    }

let sharedProgramTest =
    test "a program bound twice gives two independent programs" {
        let _, handle = interpreter ()

        let shared =
            free {
                let! unitPrice = price "ABC"
                do! log "priced"
                return unitPrice
            }

        let doubled = shared |> Free.map (fun value -> value * 2m)
        let labelled = shared |> Free.bind (fun value -> free { return $"price {int value}" })
        Expect.equal (Free.run handle doubled) 25m "the first"
        Expect.equal (Free.run handle labelled) "price 12" "the second"
        Expect.equal (Free.run handle shared) 12.5m "and the shared one is unchanged"
    }

let handWrittenTest =
    test "a hand-written Impure binds like one made by perform" {
        let _, handle = interpreter ()
        let raw: Free<Instruction, decimal> = Impure(Price "ABC", fun answer -> Pure(unbox<decimal> answer))

        let program =
            free {
                let! first = raw
                let! second = raw |> Free.map (fun value -> value + 1m)
                return first + second
            }

        Expect.equal (Free.run handle program) 26m "both continuations ran"
    }

let builderTest =
    test "the builder takes if, while, for, and try" {
        let seen, handle = interpreter ()

        let program =
            free {
                let mutable round = 0

                while round < 3 do
                    if round = 1 then
                        do! log "one"

                    round <- round + 1

                for id in [ 1; 2 ] do
                    let! _ = find id
                    ()

                let! recovered =
                    free {
                        try
                            do! log "before"
                            failwith "thrown by the program"
                            return "no"
                        with error ->
                            do! log "handler"
                            return error.Message
                    }

                let mutable cleaned = false

                let! value =
                    free {
                        try
                            return! price "ABC"
                        finally
                            cleaned <- true
                    }

                return round, recovered, value, cleaned
            }

        Expect.equal (Free.run handle program) (3, "thrown by the program", 12.5m, true) "the result"

        Expect.equal
            (List.ofSeq seen)
            [ Log "one"; Find 1; Find 2; Log "before"; Log "handler"; Price "ABC" ]
            "the instructions, in order"
    }

let rerunTest =
    test "a program built by a function starts its state afresh on each run" {
        let _, handle = interpreter ()

        // The code before the first instruction runs when the block is built, so a program value
        // would share `found` and the loop's position between runs. A function builds both anew.
        let count () =
            free {
                let mutable found = 0

                for id in [ 1; 0; 2 ] do
                    let! item = find id

                    if item.IsSome then
                        found <- found + 1

                return found
            }

        Expect.equal (Free.run handle (count ())) 2 "the first run"
        Expect.equal (Free.run handle (count ())) 2 "the second run"
    }

let longLoopTest =
    test "a loop of a million rounds runs, with and without instructions" {
        let mutable instructions = 0

        let handle (_: Instruction) : objnull =
            instructions <- instructions + 1
            box ()

        let program =
            free {
                let mutable total = 0

                for round in 1..1000000 do
                    if round % 1000 = 0 then
                        do! log "progress"

                    total <- total + 1

                return total
            }

        Expect.equal (Free.run handle program) 1000000 "every round ran"
        Expect.equal instructions 1000 "one instruction per thousand rounds"
    }

let resultBuilderTest =
    test "freeResult ends at the first Error, also inside a loop" {
        let seen, handle = interpreter ()

        let need (id: int) : Free<Instruction, Result<string, string>> =
            free {
                let! found = find id

                return
                    match found with
                    | Some item -> Ok item
                    | None -> Error $"{id} is missing"
            }

        let program (ids: int list) =
            freeResult {
                let items = List<string>()

                for id in ids do
                    let! item = need id
                    items.Add item

                do! liftFree (log "all found")
                return List.ofSeq items
            }

        Expect.equal (Free.run handle (program [ 1; 2 ])) (Ok [ "item-1"; "item-2" ]) "every item"
        seen.Clear()
        Expect.equal (Free.run handle (program [ 1; 0; 2 ])) (Error "0 is missing") "the first Error"
        Expect.equal (List.ofSeq seen) [ Find 1; Find 0 ] "nothing runs after it"
    }

let interpretersTest =
    testAsync "runAsync and runTask give what run gives" {
        let _, handle = interpreter ()

        let program =
            free {
                let! unitPrice = price "ABC"
                let! found = find 3
                return unitPrice, found
            }

        let expected = Free.run handle program
        let! viaAsync = Free.runAsync (fun instruction -> async { return handle instruction }) program
        let! viaTask = Free.runTask (fun instruction -> Task.FromResult(handle instruction)) program |> Async.AwaitTask
        Expect.equal viaAsync expected "runAsync"
        Expect.equal viaTask expected "runTask"
    }

// A program as a workflow: each instruction becomes a step that stores its answer.

type OrderInstruction =
    | Reserve of order: string
    | Charge of amount: decimal
    | Wait of TimeSpan

let private policy = Retry.times 3 (TimeSpan.FromSeconds 30.0)
let private never (_: AttemptContext) : Task<'T> = failwith "the harness runs no activity"

let private orderStep (instruction: OrderInstruction) : Workflow<objnull> =
    match instruction with
    | Reserve _ -> Free.answer (activity "reserve" policy (never: AttemptContext -> Task<Result<string, string>>))
    | Charge _ -> Free.answer (activity "charge" policy (never: AttemptContext -> Task<string>))
    | Wait duration -> Free.answer (sleep duration)

let private order (id: string) : Free<OrderInstruction, Result<string, string>> =
    freeResult {
        let! reservation = Eff.perform<OrderInstruction, Result<string, string>> (Reserve id)
        do! Eff.performR<OrderInstruction, unit, string> (Wait(TimeSpan.FromMinutes 5.0))
        let! receipt = Eff.performR<OrderInstruction, string, string> (Charge 30m)
        return $"{reservation} {receipt}"
    }

let private orders =
    Workflow.define "free-order" (fun (id: string) -> Free.toWorkflow orderStep (order id))

let workflowTest =
    test "a program runs as a workflow whose steps store the answers" {
        let flow = WorkflowTest.Start(orders, "A-1")
        Expect.isTrue (flow.Status.IsWaitingFor "reserve") "the first instruction is the first step"
        flow.Complete("reserve", (Ok "shelf-4": Result<string, string>))
        flow.Advance(TimeSpan.FromMinutes 5.0)
        flow.Complete("charge", "receipt-9")
        Expect.equal flow.Result (Ok "shelf-4 receipt-9") "the program's result is the workflow's"

        Expect.equal
            (List.ofSeq flow.Summary)
            [ "started"
              "scheduled 0 reserve"
              "completed 0 reserve"
              "scheduled 1 sleep"
              "completed 1 sleep"
              "scheduled 2 charge"
              "completed 2 charge"
              "finished" ]
            "one step per instruction, in program order"

        // A restart after any record replays the stored answers through the program.
        flow.CheckReplay()
    }

let workflowErrorTest =
    test "an Error answer ends the program and the workflow finishes with it" {
        let flow = WorkflowTest.Start(orders, "A-2")
        flow.Complete("reserve", (Error "out of stock": Result<string, string>))
        Expect.equal flow.Result (Error "out of stock") "the Error is the result"
        Expect.isFalse (flow.Summary |> Seq.exists (fun line -> line.Contains "charge")) "no later step ran"
        flow.CheckReplay()
    }

let workflowWrongTypeTest =
    test "a step whose result has another type than the program expects faults the instance" {
        // The program reads the answer to Charge as a string; this step gives a decimal.
        let wrongStep (instruction: OrderInstruction) : Workflow<objnull> =
            match instruction with
            | Charge _ -> Free.answer (activity "charge" policy (never: AttemptContext -> Task<decimal>))
            | other -> orderStep other

        let wrong = Workflow.define "free-wrong" (fun (id: string) -> Free.toWorkflow wrongStep (order id))
        let flow = WorkflowTest.Start(wrong, "A-3")
        flow.Complete("reserve", (Ok "shelf-4": Result<string, string>))
        flow.Advance(TimeSpan.FromMinutes 5.0)
        flow.Complete("charge", 30m)

        match flow.Status with
        | WorkflowStatus.Failed error ->
            Expect.equal error.Kind ErrorKind.BodyFailed "the program's own code threw"
            Expect.stringContains error.Message "System.Decimal" "the type the step gave"
            Expect.stringContains error.Message "System.String" "the type the program expected"
        | other -> failtest $"expected a faulted instance, got {other}"
    }

let tests =
    testList "free monad"
        [ answersTest; wrongAnswerTest; nestedBindTest; sharedProgramTest; handWrittenTest; builderTest
          rerunTest; longLoopTest; resultBuilderTest; interpretersTest; workflowTest; workflowErrorTest; workflowWrongTypeTest ]
