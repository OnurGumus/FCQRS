# Schedule index table Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Dispatcher restarts cost one row per schedule plus new events; `create` defaults to `Latest`; SQLite's unfiltered incremental poll stops scanning the whole index.

**Architecture:** The dispatcher's index moves to a table in the journal database, written by a stored transactional projection with its progress in one transaction; core exposes an internal journal store builder and fixes the SQLite incremental query.

**Tech Stack:** F#, Akka.NET persistence, SQLite and PostgreSQL, Expecto.

**Spec:** `specs/2026-11-14-schedule-index-design.md`

## Global Constraints

- Release together: core **7.2.1**, FCQRS.Workflows **7.6.1** depending on core 7.2.1.
- No stored schedule event, aggregate state, or run ID changes. A 7.6.0 journal must start under 7.6.1.
- The aggregate stays the gatekeeper; the index is a hint.
- Table `fcqrs_schedule_index`; its row shape is a persistent contract, pinned by a golden test.
- Tests in Release; PostgreSQL tests when `FCQRS_TEST_POSTGRES` is set; do not rebuild src/FCQRS.Workflows while the full suite runs.
- Never touch `samples/workflows/orders/` or a process on port 5080.
- Implementers commit only; the controller pushes. Commit messages `[module] Why`, three short paragraphs, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

- A 7.6.0 journal (no table, no stored progress) upgrades in place: first start fills the table from the full history, runs continue, nothing is fired twice. Test in Task 2.
- A crash between writing an index row and its progress cannot happen (one transaction); a crash before the transaction commits replays the event. Test in Task 2.
- A restart over a long history reads only the events written since the last stop (read counter). Test in Task 2.
- A schedule deleted while the dispatcher was down disappears from the table and the index. Test in Task 2.
- SQLite incremental poll plan uses the rowid/ordering path for an unfiltered projection. Test in Task 1.

---

### Task 1: Core 7.2.1: SQLite incremental poll and an internal journal store

**Files:** `src/FCQRS/ProjectionStorage.fs`, `src/FCQRS/Projections.fs`, `src/FCQRS/FCQRS.fsproj` (7.2.1), `test/Facade.Tests/ProjectionCatchUpTests.fs`, `CHANGELOG.md` (`## 7.2.1 (FCQRS core)`).

**Interfaces:** Produces `Projections.journalStore (actor: IActor) : SqlProjectionStore` (internal), built from the actor's journal settings exactly as `startTracked` does today; `startTracked` uses it.

- [ ] Write a failing EXPLAIN QUERY PLAN test for the unfiltered incremental capture on SQLite (expects a rowid/ordering path, not a scan of `journal_uq`), using the same command builders as the store.
- [ ] Run it, see it fail.
- [ ] Change the unfiltered incremental capture SQL on SQLite (PostgreSQL untouched); extract `journalStore`.
- [ ] Run the projection tests and the full suite with PostgreSQL; all pass.
- [ ] Commit.

### Task 2: The schedule index table

**Files:** `src/FCQRS.Workflows/Schedules.fs`, `test/Facade.Tests/ScheduleTests.fs`.

**Interfaces:** Consumes `Projections.journalStore`, `Projections.start` with `TransactionalProjectionOptions.Entities = ["fcqrs-schedule"]`.

- [ ] Write failing tests: (a) restart over 500 runs reads only events written after the stop (read counter or row count); (b) a journal written without the table (simulate 7.6.0: drop the table and its progress rows) starts, fills the table, and fires the next due time once; (c) delete while down removes the row; (d) golden test of a row's stored shape; (e) PostgreSQL variants of (a) and (b).
- [ ] Run them, see them fail.
- [ ] Implement the table, the transactional projection, the startup load then catch-up, row removal for dropped entries.
- [ ] Run the schedule tests three times, the full suite with PostgreSQL, the C# checks.
- [ ] Commit.

### Task 3: Latest default, docs, release prep

**Files:** `src/FCQRS.Workflows/Schedules.fs`, `src/FCQRS.Workflows/CSharp.fs`, `test/Facade.Tests/ScheduleTests.fs`, `test/Workflows.CSharp.Tests/Program.cs`, `docs/concepts/workflows.md`, `CLAUDE.md`, `CHANGELOG.md` (`## 7.6.1 (FCQRS.Workflows)`), `src/FCQRS.Workflows/FCQRS.Workflows.fsproj` (7.6.1).

- [ ] Write failing tests: F# create without a policy behaves as `Latest` after downtime; the C# overload without a policy does the same.
- [ ] Run them, see them fail; implement; run them.
- [ ] Update the concept page (Latest is the default; restarts read one row per schedule plus new events; the table and its contract), CLAUDE.md, CHANGELOG.
- [ ] Verify: snippet check, docs build, samples, pack (7.6.1 depends on core 7.2.1).
- [ ] Commit.
