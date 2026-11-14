# Workflow schedules: design

## Intent

A person on Twitter runs a small service that does scheduled backups and invoicing against a
Temporal server in WSL, and said FCQRS would have been a simpler fit. FCQRS has no schedules: a
workflow starts only when code calls `Start`. The owner wants schedules that users of an
application create and change while it runs, such as "remind me every day at 9".

Success: an application registers a run workflow, creates a schedule for a user with one call, and
a run starts at each due time, across restarts and downtime, without code of its own for timers.

## Goals

- Runtime schedules: create, change, pause, resume, delete, each one call from F# and C#.
- Each due time starts one instance of a registered workflow with the schedule's stored input.
- Correct across restarts, crashes between steps, and downtime.
- Tens of thousands of schedules on one node without one resident actor per schedule.
- Local time with a time zone: "every day at 9 in Europe/Oslo" stays at 9 across daylight saving.

## Non-goals

- Multi-node. FCQRS runs on one node; the dispatcher assumes it is the only one.
- Cron expressions in the first version. The rule type leaves room for them.
- Exactly-once runs. A run starts at least once per accepted due time, and its ID makes a repeated
  start store nothing.
- A UI, or queries beyond what a projection over the schedule events can build.
- Precision below a second.

## Anti-goals

- Startup work that grows with the number of schedules beyond one pass over their events.
- A paused, changed, or deleted schedule that starts a run because an index was behind.
- A run that is lost because the process stopped between deciding to fire and starting the run.
- A schedule whose failed run ends the schedule.

## Constraints

- CLAUDE.md: guarantees stated at their boundary, persistent contracts (aggregate name, event
  cases, fields, workflow IDs), F# and C# equivalent, fail-fast for integrity errors.
- The aggregate's name and its event cases are persistent contracts from the first release.
- Workflow IDs of runs are a contract too: an application may look a run up by it.
- No new package dependency for the first version; time zones come from `TimeZoneInfo`.
- Released as a minor version of FCQRS.Workflows, with a minor version of core for the projection
  filter below, released together.

## Approaches

| | Approach | Trade-off |
|---|---|---|
| A | One workflow instance per schedule, looping, changed by signals | Simplest. Every schedule is resident and recovered at each start: fine for hundreds, wrong for tens of thousands |
| B | One scheduler workflow holding every schedule | Little memory, but one journal that grows forever with every user's changes and fires |
| **C (chosen)** | Each schedule is an aggregate; one dispatcher per node keeps an in-memory index of next due times from a projection and starts runs | More to build. Nothing resident per schedule, and it reuses FCQRS's aggregate, projection, and idempotent-start guarantees |

The owner chose C, for schedules that users create at runtime.

## Design

### The schedule aggregate

- One aggregate instance per schedule, ID chosen by the application (for example `ada-daily`).
- State: the target workflow's name, the input (stored like a workflow input), the rule, the time
  zone, the catch-up policy, active or paused, deleted, the last due time accepted, and whether
  that due time's run is known to have started.
- Commands and their rules:
  - `Create`: stores the schedule. A repeat with the same content gets the first verdict; a
    different content for an existing ID is rejected.
  - `Change` (rule, time zone, input, policy), `Pause`, `Resume`, `Delete`.
  - `Fire due`: accepted only when the schedule is active, not deleted, no earlier `Firing` waits
    for its `Fired`, and `due` is the next due time the stored rule gives after the last accepted
    one (or, after downtime, what the catch-up policy allows). Stores `Firing due` with the input
    the run will get, so a later `Change` does not alter a run already decided. A repeat of an
    accepted `Fire due` gets the first verdict.
  - `Started due`: stores `Fired due` once the run's start is stored. It is accepted after a
    `Pause`, `Change`, or `Delete` too: a run decided before them still starts. My call.
  - `Resume`: the next due time is the first one after the resume. Due times that fell inside the
    pause are skipped whatever the catch-up policy says: a pause means no runs. My call.
  - `Create` for a deleted ID is refused; the application uses a new ID. Run IDs therefore never
    repeat across a delete and a new schedule. My call.
- The aggregate decides; the dispatcher's index is only a hint. A stale index can propose a fire,
  and the aggregate refuses it.

### Rules

- `Daily(time, zone)`, `Weekly(days, time, zone)`, `Every(interval)` measured from the schedule's
  creation or last change.
- A local time that does not exist on a day (spring forward) runs at the next valid local time; a
  local time that occurs twice (fall back) runs once, at the first occurrence. My call.
- Due times are stored in UTC.

### Catch-up after downtime

- Policy per schedule, `Latest` by default: run once for the most recent missed due time, then
  continue. `All`: run every missed due time in order. `None`: skip to the next future due time.

### The dispatcher

- One per node, started by a registration call that names the run workflows it may start.
  `create` and `change` refuse a target workflow that is not registered. If a stored schedule names
  a workflow that is no longer registered (a deploy dropped it), the dispatcher logs an error at
  each due time and sends no `Fire`, so the schedule resumes when the workflow is registered
  again. My call.
- Builds an in-memory index (schedule ID to next due time and state) from a projection over the
  schedule aggregate's events that reads from the start of the journal, and a few hundred bytes per
  schedule.
- That projection reads only the schedule aggregate's rows. Today a projection reads every row of
  the journal, and repeats a full scan every `FullScanInterval`, so runs (each a workflow with its
  own journal) would make startup and the scans grow without bound. Core projections therefore get
  a persistence-ID prefix filter, applied in the SQL that captures and reads rows, on SQLite and
  PostgreSQL, for `Fcqrs.projection`, transactional projections, and the C# registrations. A
  projection without a filter reads everything, as today. The owner chose this (option A) over a
  stored index table and over accepting the full read.
- Sleeps until the earliest due time (a single timer), then for each due schedule:
  1. sends `Fire due` to the aggregate; on refusal, it updates its index and moves on;
  2. starts the run workflow with ID `<schedule ID>@<due time in UTC, ISO 8601>`. The run's input
     is `{ Schedule: string; Due: DateTime; Input: 'Input }`, because a workflow body cannot read
     its own ID and a run that serves a period must know which one. My call;
  3. sends `Started due` to the aggregate.
- Across a crash between steps: the projection shows `Firing due` without `Fired due`, and on
  startup the dispatcher repeats steps 2 and 3 for those schedules only. A repeated start stores
  nothing, so a run starts once per accepted due time from the journal's point of view.
- A run is an ordinary workflow instance. A failed run faults that instance only; the schedule
  continues. Runs of one schedule can overlap when a run is still going at the next due time.

### API shape

F# and C#, names left to the builder:

- register the dispatcher with the run workflows it may start;
- `create`, `change`, `pause`, `resume`, `delete` for a schedule ID;
- read a schedule's state, from the dispatcher's index. It lags the aggregate by one projection
  pass, and the documentation says so. FCQRS has no API that reads an aggregate's state, and a
  query command answered with a deferred event would reach every subscriber. My call.

### Documentation and tests

- A concept-page section "Run workflows on a schedule" with the reminder example, the guarantees
  above at their boundary, and what the application still owns (idempotent run steps, time zones
  of its users).
- Tests: due-time computation (daylight saving in both directions, weekly days, catch-up
  policies) without an actor system; dispatcher behaviour with a shifted clock (`ShiftedScheduler`)
  covering fire, pause, change, delete, restart, a crash between `Firing` and `Fired`, and downtime
  under each policy; and a check that a paused schedule with a stale index starts no run.

## Left to the builder

- Names of the module, functions, commands, events, and the aggregate (the aggregate name and event
  cases become contracts, so choose them once).
- How the dispatcher is hosted (actor, hosted service) and how C# registers it.
- The in-memory index's data structure and timer mechanics.
- Whether the tutorial gets a step; not in scope for the first version unless the owner asks.
