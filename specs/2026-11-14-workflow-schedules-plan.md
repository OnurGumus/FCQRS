# Workflow schedules Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Users of an application create, change, pause, resume and delete recurring schedules at runtime; each due time starts one run of a registered workflow, across restarts and downtime.

**Architecture:** Core projections learn to read only the rows of named entity types. FCQRS.Workflows adds a pure due-time calculator, a `Schedule` aggregate that decides every fire, and one dispatcher per node that keeps an in-memory index of next due times from a filtered projection and fires in two phases (`Fire` → start run → `Started`).

**Tech Stack:** F# on .NET 10, Akka.NET (Akka.Persistence.Sql, cluster sharding), Expecto, SQLite and PostgreSQL.

**Spec:** `specs/2026-11-14-workflow-schedules-design.md`

## Global Constraints

- Core and FCQRS.Workflows are released together: core **7.2.0**, FCQRS.Workflows **7.6.0**, which depends on core 7.2.0.
- A projection without a filter reads everything, exactly as today.
- The aggregate's entity name, its event cases and fields, and the run ID format `<schedule ID>@<due time in UTC, ISO 8601>` are persistent contracts from the first release.
- The run's input is `{ Schedule: string; Due: DateTime; Input: 'Input }`.
- Catch-up policies `Latest` (default), `All`, `None`. Rules `Daily(time, zone)`, `Weekly(days, time, zone)`, `Every(interval)`.
- No new package dependency; time zones come from `TimeZoneInfo`.
- FCQRS runs on one node; the dispatcher assumes it is the only one.
- Tests run in Release; do not rebuild `src/FCQRS.Workflows` while the full suite runs; PostgreSQL tests run when `FCQRS_TEST_POSTGRES` is set.
- Never touch `samples/workflows/orders/` or a process on port 5080.
- Commit messages: `[module] Why`, three short paragraphs, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Implementers commit only; the controller pushes.
- Documentation follows CLAUDE.md: guarantees at their boundary, F# and C# equivalent, no em dashes as structure.

## Review Focus

- A schedule paused while the dispatcher's index still says active: the aggregate refuses `Fire`, no run starts. Test in Task 4.
- A crash after `Firing due` and before the run's start: the run starts after restart, once. Test in Task 4.
- A schedule whose zone has daylight saving, across both transitions: the run stays at the local time (skipped hour → next valid time; repeated hour → first occurrence). Test in Task 2.
- A process stopped across several due times: `Latest` starts one run for the latest missed time, `All` one per missed time in order, `None` none. Test in Task 4.
- A projection filtered to one entity type in a journal full of other rows: it never hands another type's row to its handler, and a late-committed row of its own type is still applied (per-ID tracking kept). Test in Task 1.

---

### Task 1: Core projections read only named entity types (core 7.2.0)

**Files:**
- Modify: `src/FCQRS/ProjectionStorage.fs` (`CaptureAllAsync`, `CaptureSinceAsync`, and any other journal query a filtered projection runs), `src/FCQRS/Projections.fs` (`Tracking`, the read path, `start`, `startTracked`, `TransactionalProjectionOptions`), `src/FCQRS/FSharp.fs` (`Projection` record and constructors), `src/FCQRS/CSharpInterop.fs` and `src/FCQRS/HostExtensions.fs` (C# registrations), `src/FCQRS/FCQRS.fsproj` (`<Version>7.2.0</Version>`)
- Modify: `docs/how-to/catch-up-projections.md`, `docs/how-to/add-a-projection.md`, `docs/configuration.md` (projection options paragraph), `CHANGELOG.md` (`## 7.2.0 (FCQRS core)`), `CLAUDE.md` (Projection section, one bullet)
- Test: `test/Facade.Tests/ProjectionCatchUpTests.fs`

**Interfaces:**
- Produces:
  - F#: `Projection` gains `Entities: string list` (empty = every entity); `Projection.single`, `multi`, `filtered` keep their signatures and set `Entities = []`; new `Projection.ofEntities (names: string list) (p: Projection) : Projection`.
  - `TransactionalProjectionOptions` gains `member val Entities : string seq = Seq.empty with get, set`.
  - C#: each `AddProjection`/`QueryApi` overload that starts a tracked projection gets an optional `entities: string seq` argument (or an options overload) with the same meaning.
  - Internal: `Projections.startTracked api progress handle` gains an entity list parameter; `FCQRS.Workflows` calls it.
- An entity name `N` selects persistence IDs that start with `N/` (aggregates and workflows: `<name>/<shard>/<id>`). Escape `%`, `_` and the escape character when building `LIKE`.

- [ ] **Step 1: Verify the read path before designing it.** Find out, against Akka.Persistence.Sql as referenced by `src/FCQRS/paket.references`, whether envelopes from `CurrentEventsByPersistenceId` carry the global ordering as their `Offset` (`Sequence`). Write the finding in the report. If they do, a filtered projection reads each target persistence ID's new events with `CurrentEventsByPersistenceId` and merges a pass by that offset, so a pass keeps journal order across IDs. If they do not, a filtered pass applies events per persistence ID in sequence order, and the XML comments, CLAUDE.md and the catch-up page say so for filtered projections. Unfiltered projections keep `CurrentAllEvents`.
- [ ] **Step 2: Write failing tests** in `ProjectionCatchUpTests.fs`:
  - `entityFilterReadsOnlyItsTypes`: two aggregate types `FilterKeep` and `FilterSkip`, 20 events each interleaved; a `FromStart` projection with `Projection.ofEntities ["FilterKeep"]` sees exactly the 20 `FilterKeep` events in each ID's sequence order, and none of `FilterSkip`.
  - `entityFilterTransactional`: same with `TransactionalProjectionOptions.Entities = ["FilterKeep"]`; the progress table holds rows only for `FilterKeep/...` IDs.
  - `entityFilterSkipsOtherRowsInSql`: with 2,000 `FilterSkip` events and 10 `FilterKeep` events, a filtered `CatchUpAsync` reads no `FilterSkip` envelope (count envelopes the read journal hands over through an internal counter or by asserting the handler and an internal read count, whichever the implementation exposes to `Facade.Tests` via InternalsVisibleTo).
  - An unfiltered projection in the same journal still sees all 2,030 events.
  - On PostgreSQL (inside the existing `FCQRS_TEST_POSTGRES` list): `entityFilterTransactional`, plus a late commit of a `FilterKeep` row numbered before already-applied rows is still applied (copy the existing late-commit test's setup).
- [ ] **Step 3: Run them and see them fail** (`dotnet build test/Facade.Tests -c Release`, then `dotnet test/Facade.Tests/bin/Release/net10.0/Facade.Tests.dll --filter-test-case "entity filter"`): compile errors for `ofEntities`/`Entities`.
- [ ] **Step 4: Implement** the interfaces. Capture queries add `AND persistence_id LIKE @p0 ESCAPE '\' OR ...` for each entity; full scans use the same filter; the read path follows Step 1.
- [ ] **Step 5: Run the focused tests, then the full suite** with PostgreSQL (start `postgres:17` in Docker as `FCQRS_TEST_POSTGRES` describes in CI): all pass.
- [ ] **Step 6: Docs and changelog** as listed under Files.
- [ ] **Step 7: Commit** (`[be] ...`).

### Task 2: Due-time calculator (pure)

**Files:**
- Create: `src/FCQRS.Workflows/Schedules.fs` (types and calculator; compile it after `Api.fs`, before `Streams.fs`)
- Create: `test/Facade.Tests/ScheduleRuleTests.fs` (add to the fsproj after `WorkflowMachineTests.fs`, add its `tests` to the list in `Tests.fs`)

**Interfaces:**
- Produces, in namespace `FCQRS.Workflows`:
  - `type ScheduleRule = Daily of time: TimeOnly * zone: string | Weekly of days: DayOfWeek list * time: TimeOnly * zone: string | Every of interval: TimeSpan`
  - `type CatchUp = Latest | All | None` (`[<RequireQualifiedAccess>]`)
  - `module ScheduleRule = val next: rule: ScheduleRule -> anchor: DateTime -> after: DateTime -> DateTime` (all UTC; `anchor` is the creation or last change time, used by `Every`; result is the first due time strictly after `after`)
  - `val missed: rule -> anchor -> lastDue: DateTime -> now: DateTime -> policy: CatchUp -> DateTime list` (due times in `(lastDue, now]` the policy allows, oldest first)
  - `val validate: rule -> Result<unit, string>` (unknown zone, empty days, non-positive interval)

- [ ] **Step 1: Write failing tests:** `Daily(09:00, "Europe/Oslo")` from 2026-11-14T10:00Z gives 2026-11-15T08:00Z; across 2027-03-28 (spring forward) a `Daily(02:30, Oslo)` gives 03:00 local that day (01:00Z); across 2026-10-25 (fall back) `Daily(02:30, Oslo)` gives the first 02:30 (00:30Z) once; `Weekly([Mon; Thu], 08:00, "UTC")`; `Every(90 min)` anchored at creation; `missed` with three missed days: `Latest` → [last], `All` → three in order, `None` → []; `validate` rejects `"Mars/Base"`, `[]`, `TimeSpan.Zero`.
- [ ] **Step 2: Run, see them fail** (compile errors).
- [ ] **Step 3: Implement** with `TimeZoneInfo.FindSystemTimeZoneById` and `IsInvalidTime`/`GetAmbiguousTimeOffsets`.
- [ ] **Step 4: Run, see them pass.**
- [ ] **Step 5: Commit** (`[be] ...`).

### Task 3: The Schedule aggregate

**Files:**
- Modify: `src/FCQRS.Workflows/Schedules.fs`
- Test: `test/Facade.Tests/ScheduleRuleTests.fs` (pure `decide`/`fold` tests)

**Interfaces:**
- Produces (names are contracts; keep them):
  - Entity name `"fcqrs-schedule"`.
  - `ScheduleCommand = Create of ScheduleSpec | Change of ScheduleSpec | Pause | Resume | Delete | Fire of due: DateTime | Started of due: DateTime`
  - `ScheduleSpec = { Workflow: string; Input: Payload; Rule: ScheduleRule; CatchUp: CatchUp }`
  - `ScheduleEvent = Created of ScheduleSpec * at: DateTime | Changed of ScheduleSpec * at: DateTime | Paused of at: DateTime | Resumed of at: DateTime | Deleted of at: DateTime | Firing of due: DateTime * input: Payload | Fired of due: DateTime | Refused of reason: string`
  - `ScheduleState` with spec, anchor, active/paused/deleted, last accepted due, and a pending `Firing` (due, input) awaiting `Fired`.
  - `decide`/`fold` exactly as the design's command rules say (a repeat answered with `DeferEvent` of the first verdict; a refusal is `DeferEvent(Refused reason)`; `Fire` accepted only per the design, using the command's `CreationDate` as now).
- [ ] **Step 1: Write failing pure tests** for each rule in the design's "The schedule aggregate" section: create/repeat/conflicting create; pause then `Fire` refused; `Fire` while a `Firing` waits refused; `Started` after `Pause` accepted; resume skips paused due times; `Fire` for a due time other than the next allowed one refused; `Create` after `Delete` refused; `Change` stores a new anchor.
- [ ] **Step 2: Run, see them fail.**
- [ ] **Step 3: Implement `decide` and `fold`.**
- [ ] **Step 4: Run, see them pass.**
- [ ] **Step 5: Commit** (`[be] ...`).

### Task 4: The dispatcher and the public API

**Files:**
- Modify: `src/FCQRS.Workflows/Schedules.fs` (dispatcher, `Schedules` module), `src/FCQRS.Workflows/Api.fs` (internal start of a registered workflow from a `Payload`), `src/FCQRS.Workflows/CSharp.fs` (C# surface)
- Test: `test/Facade.Tests/ScheduleTests.fs` (new; after `WorkflowTests.fs`; listed in `Tests.fs`), `test/Workflows.CSharp.Tests/Program.cs`

**Interfaces:**
- Consumes: Task 1's `startTracked` entity filter; Task 2's `ScheduleRule.next`/`missed`; Task 3's aggregate.
- Produces:
  - `type ScheduledRun<'Input> = { Schedule: string; Due: DateTime; Input: 'Input }`
  - `module Schedules`:
    - `register (api: IActor) (runs: ScheduledWorkflow list) : ScheduleHandle`, where `ScheduledWorkflow.of (handle: WorkflowHandle<ScheduledRun<'I>, 'R>)` erases the input type;
    - `create handle id workflowName (input: 'I) rule catchUp : Async<unit>`, `change ...`, `pause handle id`, `resume handle id`, `delete handle id`, each raising `InvalidOperationException` with the aggregate's refusal reason;
    - `state handle id : ScheduleView option` from the index (lags one projection pass).
  - C#: `Schedules.Register(actor, params ScheduledWorkflow[])`, `CreateAsync`, `ChangeAsync`, `PauseAsync`, `ResumeAsync`, `DeleteAsync`, `State`.
  - HOCON `akka.fcqrs.schedule-check-interval` (default `1m`): the longest the dispatcher sleeps before it re-reads the index, so a clock change or a missed wake costs at most that long.
- [ ] **Step 1: Write failing tests** in `ScheduleTests.fs`, each on a fresh SQLite journal:
  - `everyFires`: `Every(1s)` schedule; within 5 s at least three runs exist with IDs `<id>@<due>` and inputs carrying their due times.
  - `pauseStops`: pause; no run starts for 3 s; resume; runs start again, none for due times inside the pause.
  - `staleIndexRefused`: pause through the aggregate while the dispatcher's projection is held back (internal hook), then let the dispatcher fire: no run starts and the index updates.
  - `changeApplies`, `deleteStops`, `createDeletedRefused`, `unregisteredRefused` (create naming an unregistered workflow raises), `droppedWorkflowWaits` (restart without the workflow registered: no `Fire`, an error log; register again: runs resume).
  - `crashBetween`: internal hook stops the actor system right after `Firing` is stored; restart; exactly one run for that due time exists and `Fired` is stored.
  - `downtimeLatest`, `downtimeAll`, `downtimeNone`: stop, shift `ShiftedScheduler` forward three intervals, restart; run counts 1, 3, 0 for the missed times.
  - `failedRunContinues`: a run that faults; the next due time still starts a run.
  - C# check: register, create a `Every(1s)` schedule, see a run with the right input.
- [ ] **Step 2: Run, see them fail.**
- [ ] **Step 3: Implement** the dispatcher as the design's "The dispatcher" section says, using a filtered in-memory projection (`["fcqrs-schedule"]`) for the index, the two-phase fire, the startup re-drive of pending `Firing`s only, and the check interval. Start runs through the new internal payload start in `Api.fs`.
- [ ] **Step 4: Run the focused tests, then the full suite and the C# checks:** all pass.
- [ ] **Step 5: Commit** (`[be] ...`).

### Task 5: Documentation and release

**Files:**
- Modify: `docs/concepts/workflows.md` (new section "Run workflows on a schedule" before "Feed workflows from a stream", with an F# example and a C# example from `samples/workflows/csharp/Concepts.cs`, quoted with a sample marker), `.livedocs/contexts/concepts/workflows.fs` (one more snippet line), `CLAUDE.md` (Workflow section: schedule guarantees), `CHANGELOG.md` (`## 7.6.0 (FCQRS.Workflows)`), `src/FCQRS.Workflows/FCQRS.Workflows.fsproj` (`<Version>7.6.0</Version>`)

- [ ] **Step 1: Write the section:** the reminder example (`Daily(09:00, "Europe/Oslo")`); what is guaranteed (a run per accepted due time, its ID, catch-up, pause/resume rules, a decided run starts after a later change); what is not (exactly-once side effects: run steps still need idempotency keys; multi-node; precision below a second; state reads lag one pass).
- [ ] **Step 2: Verify:** `python3 scripts/check-learning-snippets.py`; `python3 scripts/build-docs.py`; `dotnet run --project samples/workflows/csharp -c Release`; `git diff --check`; pack both packages and confirm FCQRS.Workflows 7.6.0 depends on FCQRS 7.2.0.
- [ ] **Step 3: Commit** (`[docs] ...`). The controller pushes after the final review; CI publishes both packages.
