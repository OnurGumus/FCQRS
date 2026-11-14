# Workflow tutorial step 10, feed orders from a stream: design

## Intent

The workflow tutorial (`docs/workflow-tutorial/`, `samples/orders/`) has nine steps and leaves
streams to the concept page. The owner asked for a tutorial step that uses the feature of the
concept page's section "Feed workflows from a stream" (`WorkflowFlow`).

The owner did not say why beyond pointing at that section. My reading, not theirs: the tutorial
should let a reader run the stream entry point, and see where its guarantee ends.

## Goals

- C# gets `WorkflowFlow.Run` and `WorkflowFlow.Start`, so the F# and C# pages of the step call one
  function each. The owner chose this over a C# flow built by hand.
- A tenth step, "Feed orders from a stream": an F# and a C# console program, a page, and the same
  checks as the other nine.
- The step shows a batch, then a stop and a second run. The owner chose this over a batch only.

## Non-goals

- A durable stream, stored offsets, or a real source such as a queue or a file that grows.
- Any change to what F# `WorkflowFlow.run` and `WorkflowFlow.start` do.
- The other open gaps of the workflow package.
- Touching `samples/workflows/orders/`, the owner's untracked demo.

## Anti-goals

- Output that differs from run to run. `scripts/check-orders.py` compares it line by line, and F#
  with C#.
- A C# page with plumbing that the F# page does not have.
- A check that passes only when the machine is fast enough.
- A second run whose output would be the same if nothing had been running when the process
  stopped.

## Constraints

- The tutorial uses published packages only, so the C# API must be released before the step is
  written. Core is unchanged since 7.1.0, so `FCQRS.Workflows` is released alone, as 7.5.0.
- A push to `main` publishes every package version that nuget.org does not have yet, and a
  published version cannot be withdrawn. Part 1 ends with such a push. The owner's approval of this
  design covers the pushes of the three parts; without it, the builder stops before each push.
- Every line that is new or changed since step 9 is shown on the page. Code blocks are at most 80
  columns. F# and C# print the same lines.
- Step names, the workflow name, and stored payload types are persistent contracts. The step does
  not change `Order.fs`, `Order.cs`, `Account.fs`, or `Account.cs`.
- CLAUDE.md's documentation rules, and its workflow guarantees as stated there.

## Approaches

How the C# program gets its flow:

| | Approach | Trade-off |
|---|---|---|
| A | F# calls `WorkflowFlow.run`; C# builds the flow from its client with `SelectAsync`, as the concept page does | No release. The C# page shows about ten lines of plumbing, and C# must catch a faulted order itself |
| **B (chosen)** | Give C# a `WorkflowFlow` first | A new API and a release. Both pages call one function |

What the step shows:

| | Approach | Trade-off |
|---|---|---|
| 1 | A batch: five orders, two at once, outcomes in input order | Short. Does not show that the stream is not durable |
| **2 (chosen)** | The batch, then a stop and a second run that replays the source | Longer. Shows the limit the concept page states |

The owner chose B and 2.

## Design

The work has three parts, in this order.

### Part 1: `WorkflowFlow` for C#, released as FCQRS.Workflows 7.5.0

- `WorkflowFlow.Run(client, idOf, parallelism, timeout)` takes an `IWorkflowClient<TInput, TResult>`
  and gives a flow from `TInput` to the ID with the workflow's outcome, in input order, as F#
  `WorkflowFlow.run` does.
- `WorkflowFlow.Start(client, idOf, parallelism)` gives a flow from `TInput` to the ID with whether
  this element started the workflow, as `StartAsync` answers.
- A faulted or cancelled workflow is an element of the `Run` flow, not a failure of the stream. A
  workflow that does not end within `timeout` fails the stream, as in F#.
- F# keeps `WorkflowFlow.run` and `WorkflowFlow.start` unchanged in source.
- The C# checks (`test/Workflows.CSharp.Tests`) get a stream check: several elements, a limit of
  two, outcomes in input order, a faulted element emitted, and a second pass over the same elements
  that starts nothing.
- The concept page's C# example becomes the one call. The paragraph that says a C# program builds
  its own flow goes, and the table of C# equivalents gets the two rows.

My calls, not the owner's:

- The C# functions live under the same name as the F# module, `WorkflowFlow`, so both pages read
  alike. The tutorial's C# programs import both workflow namespaces, so the name must resolve
  without a clash there. An F# program compiled against 7.4.0 may need a rebuild; no stored or
  wire shape changes.
- C# reads an outcome through named members (`Result`, `Error`), not through F#'s `Item`.

### Part 2: move the tutorial to 7.5.0

- Once 7.5.0 is listed on nuget.org, all nine steps and their pages move from 7.2.1 to 7.5.0 in a
  commit of their own.
- `scripts/check-orders.py` must pass with no change to any page's output. If an output changes,
  the work stops and the difference is reported.

This is my call. The owner did not object when I proposed it.

### Part 3: step 10

- Program folders `samples/orders/10-feed-orders-from-a-stream/{fsharp,csharp}`, page
  `docs/workflow-tutorial/feed-orders-from-a-stream.fsx`, title "10. Feed orders from a stream".
- The program starts from step 9's code with an empty journal. It does not continue from step 9's
  database. Only `Program.fs` and `Program.cs` change.

What the program does:

- **The batch.** Five orders of one customer, one per item, in this order: `widget`, `gadget`,
  `gizmo`, `crate`, `sprocket`. Under step 9's stand-ins, `gizmo` ends as "out of stock" and `crate`
  faults at the carrier. The other three ship.
- **The ID comes from the element.** The order ID is `o-` and the item. The page says that a real
  element carries an order number, and why the ID must be computed from the element.
- **The flow.** `WorkflowFlow.run` (C# `WorkflowFlow.Run`) with a limit of two. The program prints
  each outcome as it arrives, so the lines are in input order.
- **The limit, shown.** The program counts the orders between entering the flow and their outcome,
  and prints the highest count at the end: 2.
- **The stop.** With `--stop`, the carrier stand-in ends the process, with exit code 1, once two
  orders are being shipped at the same time. It waits for the second call before it stops, so the
  state is the same on every machine: `o-widget` and `o-gadget` each have a stored `ship` step
  without a result, and no order has ended. The first run prints no outcome.
- **The second run.** Without the flag, the program first prints the status of `o-widget` and
  `o-gadget`. Both are running at `ship`: registering the workflow resumed them, before the stream
  handed over any element. Then the same batch runs again. The two resumed orders give their
  outcomes, and the other three start. Last, the program prints the journal of `o-widget`: one
  `Started` record, ending at `Finished`, which shows that the repeated element stored nothing.
- **Quiet stand-ins.** The warehouse, the carrier, and the fraud check stop printing in this step,
  because the lines of two orders would mix. The page says so. They become safe for two calls at
  once. The carrier's `--crash` switch goes; `--stop` replaces it.

Expected output, to be confirmed against the programs:

```text
$ dotnet run -- --stop
Deposited 500 to ada
  the process stops while two orders are being shipped

$ dotnet run
Deposited 500 to ada
o-widget is running: 6 ship
o-gadget is running: 6 ship
o-widget finished: shipped
o-gadget finished: shipped
o-gizmo finished: out of stock
o-crate faulted: ...
o-sprocket finished: shipped
At most 2 orders were in the stream at once
Journal of order/default-shard/o-widget:
  1 Started
  ...
  15 Finished
```

The two status lines rest on the carrier's two-second call: the program asks for the status
within milliseconds of registering. Step 5's second run rests on the same margin.

What the page covers, in order: the batch and its IDs, the flow and its limit, the first run, the
second run, and what the stream does not do. The orders resumed and the stream did not: the program
had to feed the batch again, and a real source must replay from an offset of its own.

Around the step:

- "What you built" moves from step 9's page to step 10's and gains the stream. Step 9's page ends
  with a link to step 10 and no longer lists streams among what the tutorial left out.
- `scripts/check-orders.py` gets the step, with two runs in one folder: `--stop` with exit code 1,
  then a plain run. CI already runs that script.
- `samples/orders/README.md`, CLAUDE.md, and any other text that says "nine steps" say ten.

## Left to the builder

- The exact signatures and where the C# functions are defined, as long as F# source keeps working
  and C# calls `WorkflowFlow.Run` and `WorkflowFlow.Start`.
- How C# names the members of an outcome, as long as no stored or wire shape changes.
- How the program materializes the stream, and how it counts the orders in the stream.
- The page's wording, within the documentation rules.
- The test details of Part 1.
