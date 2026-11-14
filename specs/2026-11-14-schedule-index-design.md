# Schedule index table, Latest default, SQLite polling: design

## Intent

FCQRS.Workflows 7.6.0 rebuilds the schedule dispatcher's in-memory index at every start by reading
every schedule event since the beginning, two per run. Restarts get slower as run history grows. The
owner approved keeping the index in a table so a restart reads one row per schedule and only the
events written since. Two small items ride along: a `Latest` default for `create`, and an
unfiltered SQLite projection poll that scans the whole journal index.

## Goals

- Startup cost of the dispatcher depends on the number of schedules, not on run history.
- `Schedules.create` without a catch-up policy uses `Latest`, as the schedules design intended.
- An unfiltered projection's incremental poll on SQLite reads by journal number, not the whole index.
- Released together: core 7.2.1 and FCQRS.Workflows 7.6.1, which depends on core 7.2.1.

## Non-goals

- Changing any stored schedule event, the aggregate, or run IDs.
- PostgreSQL index use for the entity filter.
- Multi-node.

## Anti-goals

- A journal written by 7.6.0 that fails to start under 7.6.1.
- An index row that disagrees with the aggregate in a way that starts a run the aggregate would
  refuse. The aggregate stays the gatekeeper; the index stays a hint.
- An index row written without its read position, or a position without its row.

## Approaches

| | Approach | Trade-off |
|---|---|---|
| **A (chosen)** | A table in the journal database, one row per schedule, written by a stored transactional projection in the same transaction as its read position | One new table whose shape is a contract; reuses existing projection machinery |
| B | Rebuild from the aggregates' snapshots | No table, but reads Akka snapshots from outside an actor and needs a new core API to start a projection at given positions |
| C | Put the full state in every event | Changes event shapes released in 7.6.0 |

The owner approved A.

## Design

### Index table (FCQRS.Workflows 7.6.1)

- Table `fcqrs_schedule_index` in the journal's database and schema, created on first use like the
  projection progress table, on SQLite and PostgreSQL.
- One row per schedule ID: the index entry the dispatcher keeps today, serialized (the builder
  chooses columns or a JSON column; the shape becomes a contract and is pinned by a test).
- The dispatcher's projection becomes a stored transactional projection named for the dispatcher,
  filtered to `fcqrs-schedule`, writing the row and its progress in one transaction. A deleted
  schedule's row is removed when the in-memory entry would be dropped.
- At startup the dispatcher loads every row into memory, then catches up from the stored positions,
  then plans. The rest of the dispatcher is unchanged.
- A journal written by 7.6.0 has no table and no progress for this projection: the first start
  reads the whole schedule history once and fills the table; later starts read only new events.
- Core exposes an internal function that builds a `SqlProjectionStore` from the actor's journal
  settings (the code `startTracked` already uses), so the workflows package needs no connection
  string of its own.

### Latest default

- F#: `Schedules.create` keeps its signature for callers that pass a policy, and a new
  `Schedules.createLatest`-style function or overload uses `CatchUp.Latest` (the builder picks a
  name that reads well and fits F# module rules); C#: the policy parameter becomes optional with
  `Latest` as default. Docs say `Latest` is the default.

### SQLite incremental poll (core 7.2.1)

- The unfiltered incremental capture on SQLite reads `FROM journal NOT INDEXED` (or an equivalent
  rowid/ordering-driven plan), as the filtered listing already does. PostgreSQL unchanged. A
  query-plan test pins it.

## Left to the builder

- Table columns, the projection's name, the internal store function's name.
- How the startup load and catch-up are sequenced, as long as no fire happens before both finish.
