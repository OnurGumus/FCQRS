module Program

// Runs under Node after Fable compiles it. A failed check throws, and Node exits with an error.

open FCQRS.Model.Data
open FCQRS.Model.FreeMonad

let mutable private failures = 0

let private check (name: string) (actual: 'T) (expected: 'T) =
    if actual = expected then
        printfn "ok   %s" name
    else
        failures <- failures + 1
        printfn "FAIL %s: expected %A, got %A" name expected actual

type Instruction =
    | Log of string
    | Find of int

let private handle (instruction: Instruction) : obj | null =
    match instruction with
    | Log _ -> box ()
    | Find id -> box (if id > 0 then Some $"item-{id}" else None)

let private log text : Free<Instruction, unit> = Eff.perform (Log text)
let private find id : Free<Instruction, string option> = Eff.perform (Find id)

let rec private nested depth : Free<Instruction, int> =
    free {
        if depth = 0 then
            return 0
        else
            let! below = nested (depth - 1)
            do! log "level"
            return below + 1
    }

let private count () =
    free {
        let mutable found = 0

        for id in [ 1; 0; 2 ] do
            let! item = find id

            if item.IsSome then
                found <- found + 1

        let! recovered =
            free {
                try
                    failwith "thrown by the program"
                    return ""
                with error ->
                    return error.Message
            }

        return found, recovered
    }

let private need ids =
    freeResult {
        for id in ids do
            let! item = liftFree (find id)

            do!
                match item with
                | Some _ -> Ok()
                | None -> Error $"{id} is missing"

        return "all found"
    }

check "unit, None and Some answers" (Free.run handle (count ())) (2, "thrown by the program")
check "a second run starts afresh" (Free.run handle (count ())) (2, "thrown by the program")
check "nested binds" (Free.run handle (nested 2000)) 2000
check "freeResult, every item" (Free.run handle (need [ 1; 2 ])) (Ok "all found")
check "freeResult, the first Error" (Free.run handle (need [ 1; 0; 2 ])) (Error "0 is missing")

match ValueLens.TryCreate<ShortString, _, _> "Ada" with
| Ok name -> check "ShortString.ToString" (name.ToString()) "Ada"
| Error errors -> check "ShortString.TryCreate" (Error errors) (Ok())

async {
    let! viaAsync = Free.runAsync (fun instruction -> async { return handle instruction }) (count ())
    check "runAsync" viaAsync (2, "thrown by the program")

    if failures > 0 then
        failwith $"{failures} Fable checks failed"
}
|> Async.StartImmediate
