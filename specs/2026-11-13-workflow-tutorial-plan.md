# Workflow Tutorial Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a nine-step "Tutorial: Workflows" track on an orders domain, next to the existing bank
tutorial (renamed "Tutorial: CQRS" in the sidebar). Each step is an F# and C# console program, and
every line of each program is on a page.

**Architecture:** Programs live in `samples/orders/<n>-<slug>/{fsharp,csharp}/` and use the
published packages. Pages live in `docs/workflow-tutorial/` as literate `.fsx` like the bank's.
`scripts/check-orders.py` runs every step and checks its output. `scripts/check-learning-snippets.py`
checks that the pages quote every line and every change.

**Tech Stack:** FCQRS.Workflows 7.2.1, FCQRS 7.0.0, .NET 11 (C# 15 unions), FsLiveDocs via
`scripts/build-docs.py`, Python 3 checks, GitHub Actions.

**Spec:** `specs/2026-11-13-workflow-tutorial-design.md`

## Global Constraints

- Packages: `FCQRS.Workflows` 7.2.1, plus `SQLitePCLRaw.bundle_e_sqlite3` 3.0.2 and
  `SourceGear.sqlite3` 3.50.4.5 as in `samples/accounts/*/fsharp/*.fsproj`. No project references.
- `net11.0`; `samples/orders/global.json` is a copy of `samples/accounts/global.json`;
  `samples/orders/paket.dependencies` is a copy of `samples/accounts/paket.dependencies`.
- Every project's `AssemblyName` is `Orders`. The database is `orders.db` beside the binary.
- F# and C# programs print identical output, line for line.
- CLAUDE.md documentation rules. F# in Stroustrup style (scripts/DOCUMENTATION.md). Tutorial code
  lines at most 80 columns, other tutorial blocks 72.
- Workflow name `order`. Step names: `reserve-stock`, `ship`, `approval`, `release-stock`,
  `check-fraud`; change ID `fraud-check`. These are persistent contracts once a page shows them.
- Do not touch `samples/workflows/orders/` (the owner's untracked demo), and never stop a process on
  port 5080.
- No library change. A library defect found on the way is reported to the owner, not worked around.
- Commit locally after each task. Do not push until the owner asks.

## Review Focus

1. **A reader following in one folder keeps `orders.db` between steps.** An order started in step
   N is "already started" in step N+1, and a running one is parked. Every page except 5's second
   run, 8 and 9 tells the reader to delete `bin/Debug/net11.0/orders.db` first. Task 3's and each
   step task's page check: the "Run it" section names the database.
2. **Concurrent orders make output order random.** Programs start one order, wait for its outcome,
   and only then start the next. The exceptions are steps 7 to 9, where an order is left waiting on
   purpose. `check-orders.py` compares the whole output line for line, so a reordering fails.
3. **GUIDs and clock values differ per run.** `check-orders.py` replaces GUIDs with `<guid>` and
   ISO timestamps with `<time>` before comparing. The pages show the same placeholders.
4. **Exit codes.** Step 5's `--crash` run exits with code 1 on purpose. `check-orders.py` accepts it
   only for that run.
5. **The F# and C# outputs drift apart.** For every run, `check-orders.py` asserts that the F# and C#
   outputs are equal after normalisation.

---

### Task 1: Step 1 programs and the run check

**Files:**
- Create: `samples/orders/global.json`, `samples/orders/paket.dependencies`
- Create: `samples/orders/1-place-an-order/fsharp/{Orders.fsproj,Order.fs,Program.fs}`
- Create: `samples/orders/1-place-an-order/csharp/{Orders.csproj,Order.cs,Program.cs}`
- Create: `scripts/check-orders.py`

**Interfaces:**
- Produces: `check-orders.py` functions `copy(temp, step, language) -> Path` (copies sources,
  project file and `global.json` into a clean folder; a later step copied into the same folder keeps
  its `bin/`), `run(project, *args, expect_exit=0) -> str` (build with
  `-warnaserror:CS8509;FS0025`, run, normalise GUIDs and timestamps), `same(actual, expected,
  output)`, and a `STEPS` table of `(folder, check function)`. Run as
  `python3 scripts/check-orders.py [step-folder ...]`.
- Produces (samples): `OrderRequest = { Customer; Item; Quantity; UnitPrice }`, the interface
  `IWarehouse.Reserve(key, item, quantity) -> Task<string>`, a fake `Warehouse` class in
  `Program.fs`/`Program.cs` that prints `  warehouse reserved {quantity} {item} (key {key})` and
  returns `r-{n}` (counter per process; the same key returns the same ID), and
  `definition (warehouse: IWarehouse)` in F# / `OrderWorkflow(IWarehouse)` in C#.

- [ ] **Step 1: Write `check-orders.py` with the step 1 expectations**

`place_an_order` runs the program twice in the same folder. First run, expected exactly:

```text
Started o-1
  warehouse reserved 2 widget (key order/o-1/0)
o-1 finished: reservation r-1
Start o-1 again: already started (Completed)
o-1 status: Completed
Journal: <N> records for <persistence id of o-1>
```

Fill in `<N>` and the persistence ID from the first real run, then pin them. Second run: the first
line is `Start o-1: already started (Completed)` and no line contains `warehouse`. For each run,
assert that the F# and C# outputs are equal.

- [ ] **Step 2: Run it and see it fail**

Run: `python3 scripts/check-orders.py 1-place-an-order`
Expected: FAIL (the step folder does not exist).

- [ ] **Step 3: Write the step 1 programs**

Model the setup on `samples/workflows/fsharp/Program.fs` and `samples/workflows/csharp/Program.cs`,
and the journal query on `samples/accounts/3-restart-the-bank/fsharp/Program.fs`. Order: connect,
create the actor, register the workflow, start `o-1` (`ada`, `widget`, 2, 40), print, start it
again, read status, print the journal count, stop the actor. The `reserve-stock` step uses
`Retry.none`. The result type is `OrderResult = { Reservation: string }`. Split every source file
into `// docs:<name>` … `// docs:end` regions that cover all non-blank lines. Project files and
`global.json` have no regions (quoted whole).

- [ ] **Step 4: Run the check**

Run: `python3 scripts/check-orders.py 1-place-an-order`
Expected: `1-place-an-order fsharp ok`, `1-place-an-order csharp ok`, exit 0.

- [ ] **Step 5: Commit** `samples/orders/` and `scripts/check-orders.py`.

### Task 2: Coverage and changed-region rules in the snippet check

**Files:**
- Modify: `scripts/check-learning-snippets.py`

**Interfaces:**
- Consumes: the region markers from Task 1.
- Produces: the marker `<!-- sample: orders/<step>/<lang> <file> file -->` (region name `file`
  quotes the whole file without its `docs:` marker lines). `PAGES` includes
  `docs/workflow-tutorial/*`. Two new rules for `samples/orders/` only:
  - **Coverage:** every non-blank line of every `.fs`/`.cs` file is inside a region, and every
    other file (`.fsproj`, `.csproj`, `global.json`) is quoted whole somewhere in the track.
  - **Changes:** for step N, every region (or whole file) that is new or differs from the previous
    step's same file and region is quoted on step N's page. Pages map to steps by the step folder in
    their markers. A page may quote an unchanged region too.

- [ ] **Step 1: Write the rules and run against the current state**

Run: `python3 scripts/check-learning-snippets.py`
Expected: FAIL, listing every region and file of `1-place-an-order` as not quoted on a page
(no page exists yet).

- [ ] **Step 2: Confirm the existing pages still pass the old rules**

Temporarily skip the orders rules (a local edit, not committed) and run the script.
Expected: `Checked <n> sample excerpts.` with the same `<n>` as on `main`.

- [ ] **Step 3: Commit** the script. (The check is red until Task 3 adds the page; commit the two
  tasks together if CI runs between them.)

### Task 3: The new sidebar section and step 1's page

**Files:**
- Create: `docs/workflow-tutorial/place-an-order.fsx` (front matter `categoryindex: 3`, `index: 1`)
- Modify: `docs/concepts/*.md` and `docs/configuration.md`, `docs/how-to/*` (renumber
  `categoryindex` 3→4, 4→5, 5→6)
- Modify: `scripts/build-docs.py` (`finish`: rewrite the two sidebar section labels in every
  generated page)

**Interfaces:**
- Consumes: step 1 sample regions; marker syntax from Task 2.
- Produces: the page pattern every later step page follows: a hidden header that loads the
  package and the step's F# files (`#r "nuget: FCQRS.Workflows, 7.2.1"`, `#load` of the step's
  `Order.fs`), sections in CLAUDE.md's order, every F# block followed by its C# pair after
  `<div class="cs-alt"></div>`, and a final "Run it" section. "Run it" shows the delete-database
  command and the exact output from `check-orders.py`, with `<guid>`/`<time>` shown as `…`.

- [ ] **Step 1: Record the current output URLs**

Run: `python3 scripts/build-docs.py && (cd output && find . -name '*.html' | sort) > /tmp/urls-before.txt`
(write the list to the scratchpad instead of `/tmp`).

- [ ] **Step 2: Write the page, renumber the sections, and add the label rewrite**

The page covers: what a workflow is, the project and setup (whole files), the input and result,
the activity and its stored result, registering, starting, a repeated start, status, and the
journal. The rewrite turns the rendered label for `tutorial/` into "Tutorial: CQRS" and the label
for `workflow-tutorial/` into "Tutorial: Workflows". Find the exact rendered labels in `output/`
first.

- [ ] **Step 3: Run the snippet check and the docs build**

Run: `python3 scripts/check-learning-snippets.py --update && python3 scripts/check-learning-snippets.py && python3 scripts/build-docs.py`
Expected: both exit 0; `Checked local links and fragments`.

- [ ] **Step 4: Check that no URL moved and the labels show**

Run the `find` from Step 1 into `urls-after.txt` and diff the two lists.
Expected: only `workflow-tutorial/place-an-order.html` is added. Then serve `output/` and look
at the sidebar in a browser: two sections, "Tutorial: CQRS" with six pages, and "Tutorial:
Workflows" with one.

- [ ] **Step 5: Commit.**

### Tasks 4 to 11: one task per step

Each task follows the same five steps. Only the table differs.

1. Copy the previous step's two folders to the new step's folders and add the expectations to
   `check-orders.py`.
2. Run `python3 scripts/check-orders.py <step>`. Expected: FAIL on the new expectations.
3. Change the programs as the table says, keeping regions covering every line. Write the page
   (`docs/workflow-tutorial/<slug>.fsx`, `index` = step number) quoting every new or changed
   region, and saying for each which file it goes in and which region it replaces.
4. Run `python3 scripts/check-orders.py <step>`, then
   `python3 scripts/check-learning-snippets.py --update && python3 scripts/check-learning-snippets.py`.
   Expected: both pass.
5. Commit.

From step 4 on, programs run orders one after another: start, wait for the outcome, and print it
before the next start.

| Task | Step folder and page | Program change | Expected output the check pins (both languages) |
|---|---|---|---|
| 4 | `2-retry-the-warehouse` | The warehouse throws `warehouse busy` on the first two calls per key and prints `  warehouse busy (key …)`. `reserve-stock` uses `{ Retry.times 5 with Schedule = FixedInterval 200 ms }`. | Two `warehouse busy (key order/o-1/0)` lines, then one `warehouse reserved` line with the same key, then `o-1 finished: reservation r-1`. |
| 5 | `3-read-the-clock` | After `reserve-stock`: `now` (placed at), `newGuid` (confirmation), `sleep 2 s`, then activity `ship` on a fake `ICarrier.Ship(key, orderId) -> Task<string>` printing `  carrier shipped {orderId} (key …)` and returning `s-{n}`. `OrderResult` adds `Confirmation`, `Shipment`. The program prints the status once during the sleep. | A `o-1 status: Running [...sleep]` line (exact format from `WorkflowStatus`), a `carrier shipped o-1` line, and a final line with the reservation, `<guid>` and the shipment. |
| 6 | `4-wait-for-approval` | Totals over 1000 wait for `waitSignalFor<bool> "approval"` with a 3 s timeout, before the sleep. No answer or `false` returns status `not approved` without shipping. Orders: `o-1` (80, no wait), `o-2` (1200, signalled `true` right after its start, so the signal arrives early), `o-3` (2000, never signalled). `OrderResult` adds `Status: string` (`shipped` / `not approved`). | `o-1` and `o-2` shipped, `o-3 finished: not approved` after the timeout, and no `carrier` line for `o-3`. |
| 7 | `5-restart-during-shipping` | `--crash`: the carrier prints `  the process stops before this step is stored` and exits with code 1, as in `samples/workflows`. Nothing else changes. | Run 1 (`--crash`, exit 1) ends inside `o-1`'s `ship`. Run 2 (same folder): `o-1` is already started; no `warehouse` line for `o-1`; one `carrier shipped o-1` line with the same key as run 1; then `o-2` and `o-3` as in step 4. |
| 8 | `6-charge-the-customer` | Adds `Account.fs`/`Account.cs`: commands `Open`, `Deposit`, `Debit(orderId, amount)`, events `Opened`, `Deposited`, `Debited(orderId, amount)`, `Rejected(reason)`. State keeps the debited order IDs. A repeated `Debit` for a stored order ID answers `DeferEvent(Debited …)`, checked before the balance. Model on `samples/accounts/5-transfer-money`. The workflow, after approval: `send accounts customer (Debit(orderId, total))`. On `Rejected`, activity `release-stock` (warehouse `Release(key, reservation)`, printing `  warehouse released …`), then status `payment refused`. `not approved` now also releases. Accounts: `ada` 1500, `bob` 50. Orders: `o-1` ada 80 shipped, `o-2` ada 1200 approved and shipped, `o-3` bob 2000 not approved, `o-4` bob 80 payment refused. | The four outcomes in order; `warehouse released` lines for `o-3` and `o-4` only; `ada` balance printed as 220 at the end. |
| 9 | `7-when-a-step-fails` | Item `gizmo`: the warehouse always throws `out of stock`. The body catches `WorkflowStepFailedException` from `reserve-stock` and returns status `out of stock`. Item `crate`: the carrier always throws, `ship` uses `Retry.times 3`, and the failure is not caught. The last order `o-big` (bob, 2000) waits for approval with a 7-day timeout. The program prints its status and exits without signalling. | `o-5 finished: out of stock`; `o-6 faulted: …` (F# from `WorkflowOutcome.Faulted`, C# from `WorkflowFaultedException`, same text); `o-big status: Running [...approval]`. |
| 10 | `8-add-a-step` | Runs on step 7's database: the check copies step 7, runs it, then copies step 8 into the same folder and runs again. Adds activity `check-fraud` right after `reserve-stock` (fake `IFraudCheck.Check(key, customer, total) -> Task<bool>`, always `true`, printing `  fraud check passed …`). The program prints `o-big`'s status, signals it `approval true` while parked, then runs `o-7` (ada, 80) to completion. | `o-big status: Parked (…)` with the reason text; `o-7` shipped with a `fraud check passed` line. |
| 11 | `9-guard-the-change` | Runs on step 8's database (the check copies 7, 8, 9 into one folder and runs each). `let! change = version "fraud-check" 2` before `check-fraud`, which runs only when `change >= 2`. The program waits for `o-big`'s outcome without signalling again. | `o-big` resumes and ships, using the approval stored while it was parked; no `fraud check` line for `o-big`; `o-8` (new) runs `check-fraud`. If `o-big` stays parked, stop and report it to the owner as a library defect. |

### Task 12: Links, overview, CI, and contributor notes

**Files:**
- Create: `samples/orders/README.md` (like `samples/accounts/README.md`: one line per step)
- Modify: `docs/overview.fsx` (both tracks, and when to start with which: aggregates and read
  models → CQRS; long-running processes, external calls, waits → Workflows)
- Modify: `docs/concepts/workflows.md`, `docs/how-to/write-a-workflow.md` (link step 1 near the
  top)
- Modify: `docs/index.html` (a second button next to "Start the tutorial"), `README.md`,
  `.livedocs/config.json` navigation (no new top-level entry unless the homepage change needs one)
- Modify: `.github/workflows/ci.yaml` (`- name: Orders tutorial` running `python3
  scripts/check-orders.py` after "Accounts tutorial")
- Modify: `CLAUDE.md` (learning-path structure and architecture map: `docs/workflow-tutorial/`,
  `samples/orders/`, the check), `scripts/DOCUMENTATION.md` (the `file` region, coverage and
  change rules, the label rewrite)

- [ ] **Step 1: Make the edits.**
- [ ] **Step 2: Full verification**

Run, in order:
`python3 scripts/check-orders.py`,
`python3 scripts/check-learning-snippets.py`,
`python3 scripts/build-docs.py`,
`git diff --check`.
Expected: all exit 0. The docs build ends with `Checked local links and fragments in <n> HTML pages.`

- [ ] **Step 3: Read the track as a reader**

Serve `output/`. In a new empty folder, follow steps 1 to 3 from the F# pages only, then 1 to 3
from the C# pages only, copying only what the pages show. Expected: the same output as the pages.
Fix any page that leaves a reader guessing.

- [ ] **Step 4: Commit** and report to the owner. Do not push.
