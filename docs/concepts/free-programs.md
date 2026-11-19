---
title: Free programs
category: Understand
categoryindex: 4
index: 14
---

# Free programs: logic that names its operations

A free program describes what to do as a list of instructions and leaves how to do it to an
interpreter. The same program can run against real services, against fixed answers in a test, or
as a durable workflow. The types are in the `FCQRS.Model.FreeMonad` module of the FCQRS.Model
package, which also compiles with [Fable](https://fable.io/).

## Start with the instructions

An order reserves stock and charges the customer. Each operation it needs is a case of an
instruction type:

```fsharp
type OrderInstruction =
    | Reserve of order: string
    | Charge of amount: decimal

let reserve order : Free<OrderInstruction, Result<string, string>> =
    Eff.perform (Reserve order)

let charge amount : Free<OrderInstruction, Result<string, string>> =
    Eff.perform (Charge amount)
```

`Eff.perform` makes a program of one instruction. Its result is the interpreter's answer, read as
the type the function declares: `reserve` expects a `Result<string, string>`. Writing one typed
function per instruction keeps that type in one place.

## Write the program

`freeResult` builds a program that gives a `Result`. A `let!` on an `Error` ends it:

```fsharp
let order (id: string) =
    freeResult {
        let! shelf = reserve id
        let! receipt = charge 30m
        return $"{shelf} {receipt}"
    }
```

`order "A-1"` calls no service. It gives a value of type
`Free<OrderInstruction, Result<string, string>>`: either `Pure` with a result, or `Impure` with the
next instruction and a function that takes the answer and gives the rest of the program. `free`
builds a program whose result is not a `Result`. Both builders take `if`, `while`, `for`,
`try ... with`, and `try ... finally`.

## Run it with an interpreter

An interpreter answers each instruction and passes the answer, boxed, to the rest of the program.
It is a loop over the two cases:

```fsharp
let rec run (answer: OrderInstruction -> obj) program =
    match program with
    | Pure result -> result
    | Impure(instruction, next) -> run answer (next (answer instruction))

let fixedAnswers instruction =
    match instruction with
    | Reserve _ -> box (Ok "shelf-4": Result<string, string>)
    | Charge _ -> box (Ok "receipt-9": Result<string, string>)

let result = run fixedAnswers (order "A-1")
// Ok "shelf-4 receipt-9"
```

FCQRS.Model 6.2.0 and later ship this loop as `Free.run`, so a test writes
`Free.run fixedAnswers (order "A-1")`. An answer may be null there, which is how `()` and `None`
are boxed.

An interpreter for real services answers with a `Task`:

```fsharp
type IWarehouse =
    abstract Reserve: order: string -> Task<Result<string, string>>

type IPayments =
    abstract Charge: amount: decimal -> Task<Result<string, string>>

let live (warehouse: IWarehouse) (payments: IPayments) instruction =
    task {
        match instruction with
        | Reserve id ->
            let! reserved = warehouse.Reserve id
            return box reserved
        | Charge amount ->
            let! charged = payments.Charge amount
            return box charged
    }
```

`Free.runTask (live warehouse payments) (order id)` runs the program with it and gives a
`Task<Result<string, string>>`. `Free.runAsync` does the same for an interpreter that answers with
an `Async`.

The program's code is the same in both cases. The services are arguments of the interpreter; the
program never sees them.

## What the types do not check

- **An answer's type is checked when the program runs.** On .NET, an answer of another type than
  the instruction's function declares raises an exception that names both types. Under Fable the
  answer is not checked, because JavaScript keeps no generic type to test.
- **A program value shares its first block's state.** A block's code up to its first instruction
  runs when the block is built. A `let mutable` or a `for` loop placed there belongs to the value,
  so a second run of the same value continues from the first run's state. `order` is a function,
  so each call builds a fresh program; write programs that keep state the same way.
- **A program is sequential.** One instruction follows another, and nothing runs in parallel unless
  an interpreter does it inside one instruction. An instruction can carry programs for the
  interpreter to run together, as
  [Run instructions together](../how-to/run-a-free-program-as-a-workflow.html#Run-instructions-together)
  shows.

Binds nested to any depth run in a loop on .NET, so a program that recurses below a `let!` neither
grows the stack nor slows down with depth. Under Fable, bind keeps a closure per level.

Next: [Run a free-monad program as a workflow](../how-to/run-a-free-program-as-a-workflow.html)
gives each instruction a durable workflow step.
