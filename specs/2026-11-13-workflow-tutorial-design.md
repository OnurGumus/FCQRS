# Workflow tutorial: design

## Intent

The documentation has a step-by-step tutorial for aggregates, read models, and sagas (the bank,
`docs/tutorial/`), but none for workflows. Workflows are covered only by a concept page
(`docs/concepts/workflows.md`) and a task guide (`docs/how-to/write-a-workflow.md`). The tutorial is
split into two tracks, CQRS and Workflows, so a reader can learn workflows step by step.

The owner's requirement: a tutorial is **reconstructable and followable**. A reader starts from an
empty folder, adds what each page shows, runs it, and sees the output the page shows.

## Goals

- A sidebar with two tutorial sections, "Tutorial: CQRS" (the existing bank) and "Tutorial:
  Workflows" (new).
- A workflow track of nine steps on the orders domain, each with a complete F# and C# console
  program that uses only published NuGet packages.
- Every step checked: the programs build and run in CI, and their output matches what the page
  shows.

## Non-goals

- HTTP endpoints, ASP.NET Core, or the Aspire dashboard. The owner ruled the web parts out.
  Telemetry is linked (`docs/how-to/observability.md`), not taught.
- Parallel branches (`race`, `and!`), child workflows, cancellation, recursion, and streams. The last
  page links the concept page sections for them.
- Any library change. A defect found while writing is reported, not worked around.
- Touching `samples/workflows/orders/`. It is the owner's own untracked learning demo; the tutorial
  gets new files elsewhere.

## Anti-goals

- A step that only runs from a checkout of this repository (project references, repository paths).
- A page whose shown output differs from what the program prints.
- Any existing documentation URL that stops resolving.

## Constraints

- CLAUDE.md documentation rules: one concrete example first, define terms when first needed, F# and
  C# semantically equivalent, Stroustrup brace style for F#, no em dashes as structure, 80-column
  code blocks in the tutorial (72 for non-code blocks).
- Workflow guarantees as CLAUDE.md states them (at-least-once activities, deterministic body, start
  by ID, stored signals, no snapshot, parking).
- Packages: `FCQRS.Workflows` 7.2.1, which depends on `FCQRS` 7.0.0.
- `samples/accounts/` targets .NET 11 with C# 15 unions and a `global.json` selecting the .NET 11 SDK,
  plus a `paket.dependencies` stub that stops Paket. The orders samples follow the same setup.
- `scripts/build-docs.py` maps each top-level `docs/` folder to one sidebar section by
  `categoryindex`.

## Approaches

| | Layout | Trade-off |
|---|---|---|
| **A (chosen)** | Two sibling sections: `docs/tutorial/` stays and is renamed "Tutorial: CQRS"; new `docs/workflow-tutorial/` is "Tutorial: Workflows" | No URL moves, no adapter change beyond a section number. The two URLs do not mirror each other. |
| B | Nested `tutorial/cqrs/` and `tutorial/workflows/` | Cleanest URLs, but six pages and their links move, redirects are needed, and the adapter must nest a second sidebar level. |
| C | One Tutorial section, part 2 starting at step 7 | Cheapest, but no real split; a workflow reader starts mid-sequence. |

The owner chose A. Earlier, the owner chose the orders domain (the stages of their demo) over
continuing the bank, and console programs over the demo's HTTP service.

## The workflow track

Domain: an `order` workflow. It reserves stock at a warehouse, waits for approval of large orders,
charges the customer's account, and ships. The warehouse and the carrier are fakes inside the
program (classes behind an interface) so a reader needs nothing else running.

| # | Page | The program adds | The reader learns |
|---|---|---|---|
| 1 | Place an order | `Workflow.define "order"`, one activity `reserve-stock`, `Workflows.register`, `Start`, `Result`, `Status`. Starts `o-1` twice. Prints what the journal holds. | A workflow, a step, a stored step result, start by ID: the second start stores nothing and the warehouse is not called again |
| 2 | Retry the warehouse | The warehouse fails the first two calls per idempotency key; a retry policy | `RetryPolicy`, `AttemptContext.IdempotencyKey`, at-least-once, one stored result for three attempts |
| 3 | Read the clock | `now`, `newGuid` for a confirmation number, a short `sleep` before the `ship` activity | Why the body must not read the clock or generate GUIDs itself; stored values; status while sleeping |
| 4 | Wait for approval | `waitSignalFor<bool> "approval"` for totals over 1000; the program signals one order, lets another time out | Signals, the timeout result, a signal sent early is kept |
| 5 | Restart during shipping | Nothing in the workflow. `--crash` stops the process inside `ship`; the second run resumes | Replay: stored steps do not run again, the interrupted step does, with the same key. Same as bank step 3 and 6: the second run reuses the first run's database |
| 6 | Charge the customer | An `Account` aggregate; `send` a `Debit`; a rejection runs `release-stock` | Command steps, a reply as a verdict, the repeat-verdict rule, `OutcomeUnknown`, compensation. Links to the CQRS track for aggregates |
| 7 | When a step fails for good | An item the warehouse always refuses. First uncaught (`Faulted`), then caught as `WorkflowStepFailedException` | `Faulted` is final; catching a failure that has a business meaning |
| 8 | Add a step | A new step while an order from step 7 is still running: the order is parked | Step names and kinds are persistent contracts, parking |
| 9 | Guard the change | The new step inside `version`: the parked order resumes and finishes | `version`, recovering a parked instance |

Steps 1 to 7 are the owner's demo stages without HTTP and telemetry. Steps 8 and 9 are my addition: they
mirror the CQRS track's last step (changing stored events), and the concept page already explains
`version`.

Each page follows CLAUDE.md's order: the example, the concept it needs, the guarantee at its
boundary, the failure mode, then the next page. Each page ends with a "Run it" section showing the
exact command and output.

## Files

- `docs/workflow-tutorial/<page>.fsx` × 9, front matter `category: Tutorial: Workflows`. Like the
  bank pages: `#r "nuget: FCQRS.Workflows, 7.2.1"`, `#load` the step's F# files, excerpts through
  `<!-- sample: orders/<step>/fsharp ... -->` and the C# pair after `<div class="cs-alt"></div>`.
  Hidden checks only where something can be checked without an actor system.
- `samples/orders/<n>-<step>/{fsharp,csharp}/`: complete programs, assembly name `Orders` in each
  step (as the bank uses `Accounts`), so step 5's second run and steps 8 and 9 can reuse a database.
  `samples/orders/global.json`, `paket.dependencies` stub, and `README.md` as in `samples/accounts/`.
- `scripts/check-orders.py`, modelled on `scripts/check-accounts.py`: copy each step to a clean
  folder, build with match-completeness warnings as errors, run, and compare the output with the
  page. A CI job runs it next to `check-accounts.py`.
- `scripts/check-learning-snippets.py`: include the new pages.
- `docs/tutorial/*.fsx`: category renamed to "Tutorial: CQRS". URLs unchanged.
- `docs/overview.fsx`: list both tracks, and say when to start with which.
- `docs/concepts/workflows.md` and `docs/how-to/write-a-workflow.md`: link the track.
- `docs/index.html` (homepage), `README.md`, `.livedocs/config.json` navigation: add an entry to the
  workflow track next to the existing "Start the tutorial".
- `CLAUDE.md` learning-path structure and architecture map: the new track and `samples/orders/`.
- `scripts/DOCUMENTATION.md`: the new check.

## Every line is on a page (owner's decision)

A reader rebuilds each program from the pages alone, with no clone.

- Step 1 shows every file in full: the project files, `global.json`, the `paket.dependencies` stub
  only if a reader needs it (it does not: it exists for this repository), and all source.
- Each later page shows every block that changed since the previous step, and says which file and
  where it goes. Unchanged files are named, not repeated.
- Every sample file is divided into `docs:` regions that together cover the whole file.
- A check (in `check-learning-snippets.py` or `check-orders.py`) fails when a sample line is in no
  region, or when a region that differs from the previous step's sample is not shown on that step's
  page. This proves the pages add up to the programs that CI runs, without assembling programs from
  the page text.
- The CQRS track keeps its current form; changing it is a separate decision.

## Calls made after the builder check

These are my calls, settled from the code and the docs build; the owner can override them.

- **Section names.** FsLiveDocs names a sidebar section after its folder, and `build-docs.py` drops
  `category`. So the bank section is "Tutorial" and the new folder renders as its own section. The
  adapter rewrites the two labels in the rendered sidebar to "Tutorial: CQRS" and "Tutorial:
  Workflows" after rendering, next to its other post-render fixes. URLs stay as they are.
- **The track stands alone.** Step 1 shows the whole setup (project file, `global.json`, actor
  system, storage). Step 6 introduces a small `Account` aggregate with enough explanation to follow,
  including the repeat-verdict rule, and links to the CQRS track for depth.
- **Step 7 has one program and two outcomes.** The body catches `WorkflowStepFailedException` from
  `reserve-stock` and returns a rejected order (a failure with a business meaning). A carrier failure
  in `ship` stays uncaught, so a second order ends `Faulted`. Two order IDs, one run.
- **Step 7 leaves an order running.** A large order waits for approval with a long timeout and is
  never signalled, so step 8 finds an instance that old code started.
- **Step 8 is two steps.** "8 Add a step" inserts a step without a guard: the running order is
  parked, and its status says so. "9 Guard the change" wraps the new step in `version`: the restart
  resumes the parked order and it finishes. Each is a real program the reader writes and runs, and
  both continue from the previous step's database, as bank step 6 does. (A parked instance keeps
  its signals for code that matches its journal: `WorkflowActor.fs`.) If the builder finds that a
  parked instance does not resume after the fix, they report it as a library defect.

So the track has nine pages.

## Left to the builder

- Section numbering: the new section sits right after "Tutorial: CQRS"; renumber later sections'
  `categoryindex` as needed, and confirm that no output URL changes.
- Exact page and folder slugs, fake-warehouse wording, order IDs and amounts.
- How the programs print, as long as output is deterministic enough to check (no raw GUIDs or
  timestamps compared literally).
- Whether step 6's `Account` reuses the bank's `Account` shape or is a smaller one.
