# Workflow tutorial step 10 (streams) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give C# a `WorkflowFlow`, release it as FCQRS.Workflows 7.5.0, move the workflow tutorial to 7.5.0, and add step 10, "Feed orders from a stream".

**Architecture:** `WorkflowFlow` becomes one static class in namespace `FCQRS.Workflows` that keeps F#'s curried `run`/`start` and adds C# `Run`/`Start` over `IWorkflowClient`. Step 10 is step 9's program with a new `Program.fs`/`Program.cs` that feeds five orders through `WorkflowFlow.run`, stops inside the carrier stand-in with `--stop`, and replays the batch on the next run.

**Tech Stack:** F# and C# on .NET 10 (library) and .NET 11 (tutorial), Akka.Streams, Expecto, FsLiveDocs, Python check scripts.

**Spec:** `specs/2026-11-14-workflow-tutorial-streams-design.md`

## Global Constraints

- FCQRS.Workflows is released alone as **7.5.0** and depends on FCQRS **7.1.0** (core unchanged since `af719c0`).
- A push to `main` publishes; the owner's approval of the design covers the three pushes in this plan.
- The tutorial uses published packages only; step 10 is written after 7.5.0 is listed on nuget.org.
- Every line new or changed since step 9 is shown on the page; code blocks at most 80 columns; F# and C# print the same lines.
- Step 10 does not change `Order.fs`, `Order.cs`, `Account.fs`, `Account.cs`.
- Tests run in Release. Do not rebuild `src/FCQRS.Workflows` while the full suite runs.
- Never touch `samples/workflows/orders/` or a process on port 5080.
- Commit messages: `[module] Why`, three short paragraphs, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

- A faulted or cancelled workflow in the C# `Run` flow is an element, not a stream failure: covered by Task 1's C# check (faulted element).
- A replayed element starts nothing again: covered by Task 1's second pass and Task 3's journal of `o-widget`.
- The tutorial's C# programs import both `FCQRS.Workflows` and `FCQRS.Workflows.CSharp`: `WorkflowFlow` must not be ambiguous there; covered by Task 3's C# build (`check-orders.py` builds with warnings as errors for missing cases).
- The `--stop` run must leave the same state on every machine: the carrier waits for the second call before it exits; covered by Task 3's check of the second run's two status lines.
- The stand-ins are called by two orders at once: they must be thread-safe; covered by Task 3's runs (a race would change outcomes, which the check compares).

---

### Task 1: `WorkflowFlow` for C#, released as 7.5.0

**Files:**
- Modify: `src/FCQRS.Workflows/Streams.fs` (whole file), `src/FCQRS.Workflows/FCQRS.Workflows.fsproj` (compile `Streams.fs` after `CSharp.fs`; `<Version>7.5.0</Version>`)
- Modify: `samples/workflows/csharp/Concepts.cs` (`docs:stream` region), `docs/concepts/workflows.md` (section "Feed workflows from a stream", table "C# equivalents"), `CHANGELOG.md`
- Test: `test/Workflows.CSharp.Tests/Program.cs`, existing F# `streamTest` in `test/Facade.Tests/WorkflowTests.fs`

**Interfaces:**
- Produces, in namespace `FCQRS.Workflows`:
  - `[<AbstractClass; Sealed>] type WorkflowFlow`
  - F#, unchanged at call sites: `static member run (handle: WorkflowHandle<'I,'R>) (idOf: 'I -> string) (parallelism: int) (timeout: TimeSpan) : Flow<'I, struct (string * WorkflowOutcome<'R>), NotUsed>` and `static member start (handle) (idOf) (parallelism) : Flow<'I, struct (string * StartResult), NotUsed>`
  - C#: `static member Run(client: IWorkflowClient<'I,'R>, idOf: Func<'I,string>, parallelism: int, timeout: TimeSpan) : Flow<'I, WorkflowEnded<'R>, NotUsed>` and `static member Start(client: IWorkflowClient<'I,'R>, idOf: Func<'I,string>, parallelism: int) : Flow<'I, struct (string * bool), NotUsed>` (`true` when this element started the workflow)
  - `[<Sealed>] type WorkflowEnded<'R>` with read-only members `Id: string`, `IsFinished: bool`, `Result: 'R` (default unless finished), `IsFaulted: bool`, `Error: StepError | null` (null unless faulted), `IsCancelled: bool`
- `IWorkflowClient` needs a way to reach the outcome without exceptions: add `abstract OutcomeAsync: id: string * timeout: TimeSpan -> Task<WorkflowOutcome<'TResult>>` to `IWorkflowClient` and implement it in `WorkflowHost.Register` from `handle.Result`.

- [ ] **Step 1: Write the failing C# check** in `test/Workflows.CSharp.Tests/Program.cs`, a new block "a C# stream":
  register a workflow `"streamed"` whose body returns `input * input` and throws for input 3; run `Source.From(new[] {1, 2, 3, 4, 5}).Via(WorkflowFlow.Run(client, v => $"s{v}", 2, TimeSpan.FromSeconds(20)))` into a list, while counting elements between entering the flow (counted in `idOf`) and leaving it; then run `WorkflowFlow.Start(client, v => $"s{v}", 2)` over the same five elements.
  Checks: ids are `s1..s5` in order; results of 1, 2, 4, 5 are 1, 4, 16, 25 with `IsFinished`; element 3 has `IsFaulted` and a non-null `Error`; the highest count is at most 2; every `Start` element has `Started == false`.
- [ ] **Step 2: Run it and see it fail to compile:** `dotnet run --project test/Workflows.CSharp.Tests -c Release` → error: `WorkflowFlow.Run` not found.
- [ ] **Step 3: Implement** the interfaces above. Move `Streams.fs` after `CSharp.fs` in the fsproj and declare the class in `namespace FCQRS.Workflows`, so the C# name and the F# name are one type. Update the XML comments: not durable, ID from the element, order kept, timeout fails the stream, faulted/cancelled are elements.
- [ ] **Step 4: Run the checks:** the C# check passes (`All C# workflow checks passed.`); `dotnet build test/Facade.Tests -c Release` builds and `streamTest` passes unchanged (`--filter-test-case "a stream starts one workflow per element"`).
- [ ] **Step 5: Docs.** The `docs:stream` region becomes one call returning `Flow<string, WorkflowEnded<Enriched>, NotUsed>` via `WorkflowFlow.Run(client, order => $"enrich-{order}", 8, TimeSpan.FromMinutes(5))`; the page quotes it; drop the paragraph that says C# builds its own flow and that `ResultAsync` fails the C# stream; the C# equivalents table gets `WorkflowFlow.run` → `WorkflowFlow.Run(client, idOf, parallelism, timeout)` and `WorkflowFlow.start` → `WorkflowFlow.Start(client, idOf, parallelism)`. `CHANGELOG.md` gets `## 7.5.0 (FCQRS.Workflows)` with the new API, `OutcomeAsync`, and "The package still needs FCQRS core 7.1.0."
- [ ] **Step 6: Verify:** full suite in Release (232+ passed, 0 failed); C# checks; both workflow samples; `python3 scripts/check-learning-snippets.py`; `python3 scripts/build-docs.py`; `git diff --check`; `git diff af719c0 -- src/FCQRS` is empty; pack and confirm the nuspec has version 7.5.0 and depends on FCQRS 7.1.0.
- [ ] **Step 7: Commit and push,** then wait until `https://api.nuget.org/v3-flatcontainer/fcqrs.workflows/index.json` lists 7.5.0 and the CI run succeeded.

### Task 2: Move the tutorial to 7.5.0

**Files:**
- Modify: every `samples/orders/*/{fsharp,csharp}/Orders.*proj` and every `#r "nuget: FCQRS.Workflows, 7.2.1"` and `Version="7.2.1"` in `docs/workflow-tutorial/*.fsx`

- [ ] **Step 1:** Replace `7.2.1` with `7.5.0` in those files only (`grep -rn "7\.2\.1" samples/orders docs/workflow-tutorial` is empty afterwards).
- [ ] **Step 2: Verify:** `python3 scripts/check-orders.py` prints `passed` for all nine steps with no change to any page; `python3 scripts/check-learning-snippets.py`; `python3 scripts/build-docs.py`. If any output differs, stop and report the difference.
- [ ] **Step 3: Commit and push** (`[docs] ...`); CI succeeds.

### Task 3: Step 10, "Feed orders from a stream"

**Files:**
- Create: `samples/orders/10-feed-orders-from-a-stream/{fsharp,csharp}/` with `Account.*`, `Order.*`, `Orders.*proj` copied from step 9 unchanged, and new `Program.fs`, `Program.cs`
- Create: `docs/workflow-tutorial/feed-orders-from-a-stream.fsx` (title `10. Feed orders from a stream`, `index: 10`), `.livedocs/contexts/workflow-tutorial/feed-orders-from-a-stream.fs` (if the page's fences need one, as step 9's does)
- Modify: `scripts/check-orders.py`, `docs/workflow-tutorial/guard-the-change.fsx` (end), `samples/orders/README.md`, `CLAUDE.md` ("nine steps" → ten, step 10 named)

**Interfaces:**
- Consumes: `WorkflowFlow.run` (F#), `WorkflowFlow.Run` and `WorkflowEnded<'R>` (C#) from Task 1; `definition`, `OrderRequest`, `OrderResult` from step 9's `Order.fs`/`Order.cs`.

- [ ] **Step 1: Write the failing check.** In `scripts/check-orders.py`, add `feed_orders_from_a_stream(root)` running `[('10-feed-orders-from-a-stream', ['--stop'], 1), ('10-feed-orders-from-a-stream', [], 0)]` in one folder, and register it in `STEPS`. First run must equal:
  ```
  Deposited 500 to ada
    the process stops while two orders are being shipped
  ```
  Second run must start with `Deposited 500 to ada`, then `o-widget is running: <path> ship` and `o-gadget is running: <path> ship` (same path, the `ship` step's path under step 9's code), then in order `o-widget finished: shipped`, `o-gadget finished: shipped`, `o-gizmo finished: out of stock`, `o-crate faulted: <message as step 7's page shows>`, `o-sprocket finished: shipped`, `At most 2 orders were in the stream at once`, and the journal of `o-widget` (one `Started` first, `Finished` last). Fill `<path>`, the message, and the journal lines from step 9's page and code, not from a run.
- [ ] **Step 2: Run it and see it fail:** `python3 scripts/check-orders.py 10-feed-orders-from-a-stream` → fails (folder missing).
- [ ] **Step 3: Write `Program.fs` and `Program.cs`** from step 9's, with these changes and nothing else:
  - Stand-ins are quiet and thread-safe (concurrent collections or a lock). The fraud check stand-in stays, quiet.
  - `Carrier(stop: bool)`: with `stop`, a ship call counts itself; the first waits until a second call arrives; then the program prints `  the process stops while two orders are being shipped` and exits with code 1. Without `stop`, a call takes two seconds (`Task.Delay`, not `Thread.Sleep`). The `--crash` switch goes; `--stop` replaces it.
  - After registering: deposit 500 to `ada`; print the status of `o-widget` and `o-gadget` when it is `Running`, as `{id} is running: {pending joined with ", "}`.
  - Batch: five `OrderRequest`s for `ada`, quantity 2, unit price 40, items `widget`, `gadget`, `gizmo`, `crate`, `sprocket`; ID `o-{item}`.
  - `Source.From(batch).Via(WorkflowFlow.run orders idOf 2 (TimeSpan.FromSeconds 30.0))`, materialized with the actor system's materializer and awaited; print each outcome as it arrives in step 9's words (`finished: shipped`, `finished: {status}`, `faulted: {message}`).
  - Count orders between entering the flow and their outcome; print `At most {n} orders were in the stream at once`.
  - Print the journal of `o-widget` with step 9's `printJournal`, then stop.
- [ ] **Step 4: Run the check:** `python3 scripts/check-orders.py 10-feed-orders-from-a-stream` passes for F# and C#. Run it three times; it passes each time.
- [ ] **Step 5: Write the page** in the order: the batch and its IDs (a real element carries an order number; the ID comes from the element so a replayed element reaches its instance), the flow and its limit, quiet stand-ins, "Run it" with the two runs as `text` blocks, what the second run shows (registering resumed the two orders; the stream did not resume, the program fed the batch again; the finished order's journal has one `Started`), and what the stream does not do (a real source replays from an offset of its own). Then move "What you built" here from step 9 and add the stream to it. Step 9's page ends with a link to step 10 and no longer names streams as left out. Update `samples/orders/README.md` and `CLAUDE.md`.
- [ ] **Step 6: Verify:** `python3 scripts/check-orders.py` (all ten steps); `python3 scripts/check-learning-snippets.py` (every changed region shown, 80 columns); `python3 scripts/build-docs.py` (pages, links); `git diff --check`.
- [ ] **Step 7: Commit and push** (`[docs] ...`); CI succeeds, including the job "Orders workflow tutorial".
