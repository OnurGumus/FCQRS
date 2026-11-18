---
title: Run a free-monad program as a workflow
category: Apply
categoryindex: 5
index: 13
---

# Run a free-monad program as a workflow

A free-monad program lists the operations it needs as an instruction type, and an interpreter
decides what each instruction does. `Free.toWorkflow` is an interpreter that carries out each
instruction as a workflow step, so the program's progress is stored and survives a restart. Use it
for logic that also runs outside a workflow, with another interpreter. A workflow that runs
nowhere else is shorter with the `workflow` builder, which [Write a workflow](write-a-workflow.html)
explains.

[Free programs](../concepts/free-programs.html) explains instructions, builders, and
interpreters. The free monad is in `FCQRS.Model.FreeMonad`, and `Free.toWorkflow` in
`FCQRS.Workflows`. This page is F# only: the free monad has no C# API.

## Write the program

An order reserves stock, waits five minutes, and charges the customer. Each operation is a case of
`OrderInstruction`, and `Eff.perform` makes a program of one instruction whose result is the
interpreter's answer:

```fsharp
type OrderInstruction =
    | Reserve of order: string
    | Wait of TimeSpan
    | Charge of amount: decimal

let reserve order : Free<OrderInstruction, Result<string, string>> =
    Eff.perform (Reserve order)

let wait duration : Free<OrderInstruction, Result<unit, string>> =
    Eff.performR (Wait duration)

let charge amount : Free<OrderInstruction, Result<string, string>> =
    Eff.performR (Charge amount)

let order (id: string) =
    freeResult {
        let! shelf = reserve id
        do! wait (TimeSpan.FromMinutes 5.0)
        let! receipt = charge 30m
        return $"{shelf} {receipt}"
    }
```

`freeResult` ends the program at the first `Error`. `Reserve` answers with a `Result`, so an order
that is out of stock ends there. `Eff.performR` wraps an answer that is not a `Result` in `Ok`.

The program calls no service and reads no clock. It only names instructions, so it has no
dependency on workflows.

## Give each instruction a step

`step` maps an instruction to the workflow step that carries it out. `Free.answer` boxes the step's
result, because the cases give results of different types:

```fsharp
type IWarehouse =
    abstract Reserve:
        key: string * order: string -> Task<Result<string, string>>

type IPayments =
    abstract Charge: key: string * amount: decimal -> Task<string>

let policy = Retry.times 3 (TimeSpan.FromSeconds 30.0)

let step (warehouse: IWarehouse) (payments: IPayments) instruction =
    match instruction with
    | Reserve order ->
        activity "reserve" policy (fun attempt ->
            warehouse.Reserve(attempt.IdempotencyKey, order))
        |> Free.answer
    | Wait duration -> sleep duration |> Free.answer
    | Charge amount ->
        activity "charge" policy (fun attempt ->
            payments.Charge(attempt.IdempotencyKey, amount))
        |> Free.answer

let orders warehouse payments =
    Workflow.define "order" (fun (id: string) ->
        Free.toWorkflow (step warehouse payments) (order id))
```

`orders` is an ordinary workflow definition. Register it with `Workflows.register` and start it by
ID like any other. The services arrive as arguments of `step`; the program never sees them.

Each instruction stores its answer as its step's result. After a restart the workflow replays the
stored answers through the program and continues at the instruction it had reached: a stored
`reserve` does not run again. An instruction that was running when the process stopped runs again,
so the warehouse and the payment service must honour the idempotency key.

## Test it

`WorkflowTest` runs the workflow in memory, and the test gives each step's outcome:

```fsharp
let finishes (warehouse: IWarehouse) (payments: IPayments) =
    let flow = WorkflowTest.Start(orders warehouse payments, "A-1")
    flow.Complete("reserve", (Ok "shelf-4": Result<string, string>))
    flow.Advance(TimeSpan.FromMinutes 5.0)
    flow.Complete("charge", "receipt-9")
    // A restart after any stored record must store the same journal.
    flow.CheckReplay()
    flow.Result = Ok "shelf-4 receipt-9"
```

[Test a workflow](test-a-workflow.html) describes the harness. The program can also be tested
without a workflow: `Free.run`, in FCQRS.Model 6.2.0 and later, runs it with a function that
answers each instruction, as [Free programs](../concepts/free-programs.html#Run-it-with-an-interpreter)
shows.

## What the program must keep

- **The program's code must be deterministic.** It runs again on every replay, like a workflow
  body. It reads time and random values through instructions, never directly.
- **Step names are persistent contracts.** The name each instruction's step has, such as
  `"reserve"`, is stored. Renaming it, or changing the order in which a program performs its
  instructions, parks the instances that are running. Change a program that has running instances
  as [Change a workflow while instances are running](../concepts/workflows.html#Change-a-workflow-while-instances-are-running)
  describes; a `version` step can be one more instruction.
- **Answer types are checked when the program runs.** A step whose result has another type than
  the instruction's caller expects faults the instance with an exception that names both types.
  Pair each instruction with a typed function, as `reserve` and `charge` do, so the type is written
  once.
- **A program is sequential.** One instruction follows another. For steps that run together, map
  one instruction to a step built with `all` or `race`.

Next: [Test your domain](test-your-domain.html) checks decisions, replay, and retries.
