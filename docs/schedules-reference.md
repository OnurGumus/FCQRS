---
title: Schedules reference
category: Reference
categoryindex: 6
index: 3
---

# Schedules reference

This page lists the rules behind FCQRS.Workflows schedules: when each schedule rule is due, what
each call checks, the limits on IDs, how the dispatcher times its starts, where it stores its
index, what a deploy can break, and what FCQRS stores.
[Schedules](concepts/schedules.html) explains the
model and its guarantee, and [Run scheduled jobs](how-to/run-scheduled-jobs.html) builds runnable
F# and C# programs.

The F# functions are in the `Schedules` module. The C# `ScheduleClient` methods `CreateAsync`,
`ChangeAsync`, `PauseAsync`, `ResumeAsync`, `DeleteAsync`, and `State` call them and raise the same
exceptions, and `ScheduleHost.Register` calls `Schedules.register`.

## Registration

`Schedules.register` registers the schedule aggregate, `fcqrs-schedule`, and starts the actor
system's one dispatcher, which starts runs of the listed workflows only.

- Register each workflow with `Workflows.register` first, because the list takes each workflow's
  handle.
- The schedule aggregate, like every aggregate, waits for the actor system's saga starters before
  it stores an event, and terminates the process when they are not wired within
  `akka.fcqrs.saga-start-timeout`. Call `Fcqrs.wireSagaStarters` once, after every aggregate and
  saga is registered, this one included. An application without sagas passes an empty list.
- Until the saga starters are wired, the dispatcher fires nothing.
- A C# host built with `AddFcqrs` wires the saga starters when the host starts. Register the
  schedules after that, for example from a singleton resolved once the host has started, as
  [Schedules](concepts/schedules.html) shows.

## Schedule rules

A rule names the instants a schedule is due. FCQRS computes them in the rule's time zone, a
time-zone ID such as `Europe/Oslo`, and stores them in UTC.

| Rule | Due |
| --- | --- |
| `ScheduleRule.Daily(time, zone)` | Every day at `time` |
| `ScheduleRule.DailyAt(times, zone)` | Every day at each of `times` |
| `ScheduleRule.Weekly(days, time, zone)` | On each of `days` at `time` |
| `ScheduleRule.WeeklyAt(days, times, zone)` | On each of `days` at each of `times` |
| `ScheduleRule.Monthly(days, times, zone)` | On each `MonthDay` in `days`, every month, at each of `times` |
| `ScheduleRule.Every interval` | Every `interval` after the creation or the last change, from one second to 366 days |

A monthly rule's days are `MonthDay` values:

| `MonthDay` | Day of the month |
| --- | --- |
| `MonthDay.Day n`, `n` from 1 to 31 | Day `n`. A month without it runs on its last day: `Day 31` runs on 30 April, and `Day 29` on 28 February outside a leap year |
| `MonthDay.LastDay` | The last day |
| `MonthDay.Nth(n, weekday)`, `n` from 1 to 4 | The `n`th such weekday: `Nth(3, DayOfWeek.Wednesday)` is the third Wednesday. There is no fifth; use `Last` |
| `MonthDay.Last weekday` | The last such weekday, the fourth or the fifth |

- The days and times of one rule form one set of local date-times, and a date-time that two entries
  name is due once. `Day 31` and `LastDay` name the same day in every month; `Day 30` and `LastDay`
  name the same day in a 30-day month.
- A local time that a clock change skips is due at the next local time that exists. In Oslo,
  `ScheduleRule.Monthly([ MonthDay.Last DayOfWeek.Sunday ], [ TimeOnly(2, 30) ], "Europe/Oslo")` is
  due on 28 March 2027 at 03:00 local time, 01:00 UTC. Two times inside the skipped hour, such as
  02:15 and 02:45, are one due time, at 03:00. A skipped time moves forward in whole minutes and
  keeps its seconds: 02:15:30 is due at 03:00:30, and 02:45:10 at 03:00:10, before it.
- A local time that occurs twice is due once, at the first occurrence. The same rule is due on
  25 October 2026 at the first 02:30, 00:30 UTC, and not at the second, 01:30 UTC.
- The catch-up policies, the 1000 limit of `CatchUp.All`, and the dispatcher treat every rule the
  same.

In C#, `ScheduleRule.NewMonthly`, `NewDailyAt`, and `NewWeeklyAt` take F# lists, which a collection
expression builds. `MonthDay.LastDay` is a property:

```csharp
var club = ScheduleRule.NewMonthly(
    [MonthDay.NewNth(3, DayOfWeek.Wednesday), MonthDay.LastDay],
    [new TimeOnly(18, 0)],
    "Europe/Oslo");
```

## Exceptions each call raises

`create` and `change` check their arguments before they send anything, and every command checks
the schedule ID first.

| Call | Exception | When |
| --- | --- | --- |
| `create`, `change` | `ArgumentException` | `ScheduleRule.validate` rejects the rule: a time zone the system cannot read, a weekly or monthly rule without days, a rule without times of day, a value that is not a day of the week, a `MonthDay.Day` outside 1 to 31, a `MonthDay.Nth` outside 1 to 4, or an `Every` interval shorter than one second or longer than 366 days |
| `create`, `change` | `ArgumentNullException` | The rule or the catch-up policy is missing |
| `create`, `change`, `pause`, `resume`, `delete` | `ArgumentException` | The schedule ID breaks a rule in [Schedule and run IDs](#Schedule-and-run-IDs) |
| `create`, `change`, `pause`, `resume`, `delete` | `InvalidOperationException` | The saga starters are not wired yet. Nothing is sent |
| `create`, `change` | `InvalidOperationException` | The dispatcher does not list the workflow, or the input's type differs from the workflow's |
| `create` | `InvalidOperationException` | A schedule with this ID exists with a different content, or was deleted |
| `change`, `pause` | `InvalidOperationException` | The schedule does not exist or was deleted |
| `resume` | `InvalidOperationException` | The schedule does not exist, was deleted, or is not paused |
| `delete` | `InvalidOperationException` | The schedule does not exist |
| `register` | `InvalidOperationException` | The actor system has a dispatcher already: a second `Schedules.register` on one actor system |
| `register` | `ArgumentException` | `akka.persistence.journal.sql` and `akka.persistence.query.journal.sql` differ in connection string, schema, table, or columns. Nothing is registered |
| `register` | `ArgumentException` | A workflow is listed twice |
| `register` | `ConfigurationException` | `akka.fcqrs.schedule-check-interval` is not positive |

A second `create` with the same content stores nothing and raises nothing. Before the comparison,
the days and the times of a rule are sorted and their repeats removed, so `[Thursday; Monday]` and
`[Monday; Thursday]` are the same content. A day written two ways stays two entries: a monthly rule
with `Day 31` and one with `LastDay` are different contents, though they are due at the same times.

## Schedule and run IDs

A schedule ID must:

- not be blank;
- not contain `~` or `#`;
- have at most `Schedules.MaxIdLength` (200) characters;
- fit its runs' persistence IDs in the journal's 255-character column.

A run's workflow ID is the schedule's ID, `@`, and the due time in UTC in ISO 8601 (`"O"`) form,
such as `ada-daily@2026-11-15T08:00:00.0000000Z`. A run is journaled under the persistence ID
`<escaped workflow name>/default-shard/<escaped run ID>`. Akka escapes both names as
`Uri.EscapeDataString` does, and a run ID adds 35 escaped characters to the schedule ID.

| Limit | Value |
| --- | --- |
| Journal column for a persistence ID | 255 characters |
| Escaped workflow name and escaped schedule ID together | at most 205 characters |
| One character outside ASCII, escaped | 6 to 12 characters |

`create` and `change` raise `ArgumentException` when the escaped workflow name and the escaped
schedule ID take more than 205 characters. With the workflow `remind`, a schedule ID of 34
characters outside ASCII is already too long.

## Read a schedule's state

`Schedules.state` (`State` in C#) reads the dispatcher's index, not the schedule aggregate. The index
follows the schedule's events through a projection, so it lags by one projection pass: a `pause`
that has just returned may not show yet.

## Dispatcher timing

- The dispatcher sleeps until the earliest due time in its index.
- While fewer than 16 starts are running, it wakes at least every
  `akka.fcqrs.schedule-check-interval`, one minute by default, so a clock change or a missed timer
  delays a run by at most that long. The [configuration reference](configuration.html) lists the
  setting.
- While 16 starts are running, it reads nothing and waits for one of them to end. No other run
  could start before then, but the error for a schedule whose workflow is not registered is logged
  when a start ends, not at its due time.
- A run whose start fails, or hangs until its ask times out, is tried again after the shorter of
  the check interval and five seconds, counted from the failure. The dispatcher does not wait for
  it, so it delays no other schedule while fewer than 16 starts hang at once.

## The index is stored in the journal's database

The dispatcher's projection, `fcqrs-schedule-index`, keeps the index in the table
`fcqrs_schedule_index`, in the journal's database and schema. `Schedules.register` raises
`ArgumentException` and registers nothing when the write journal and the query journal differ, as
[Exceptions each call raises](#Exceptions-each-call-raises) lists.

- The table has one row per schedule. Each row is written in the transaction that stores the
  projection's read position for that schedule, so a row never runs ahead of or behind its
  position.
- At startup the dispatcher reads every row, then the schedule events stored after those positions,
  and fires nothing until it has read both. A restart therefore reads one row per schedule and the
  events written since the last stop, however many runs the schedules have had.
- A deleted schedule's row is removed once no run decided before the delete waits to start.

### Rebuild

The schedule events are the source of the index, and the table is derived data.

- When the table is missing, the dispatcher creates it, deletes the projection's read positions, and
  reads the whole schedule history to fill it.
- When a row cannot be read, for example a row written by a newer version of FCQRS.Workflows, the
  dispatcher logs a warning that names the schedule, deletes every row and the read positions, and
  does the same.
- After a rebuild the dispatcher can ask again for a run decided before the stop. The schedule
  answers with its first verdict, and a second start of the run stores nothing.

### Database rights

The dispatcher creates three tables in the journal's schema on first use, then reads and writes
them:

| Table | Holds |
| --- | --- |
| `fcqrs_projection_progress` and `fcqrs_projection_progress_lock` | The projection's read positions |
| `fcqrs_schedule_index` | One row per schedule |

The journal's database user needs the right to create tables in that schema, and afterwards to read
and write these three. FCQRS.Workflows 7.6.0 only read the journal. To create the tables up front
instead, match what FCQRS creates. The index table is:

```sql
CREATE TABLE fcqrs_schedule_index (
    schedule_id TEXT NOT NULL PRIMARY KEY,
    entry TEXT NOT NULL
);
```

Without these rights the dispatcher cannot load the index: it logs an error every second and no
schedule fires.

### First start after FCQRS.Workflows 7.6.0

A journal written by FCQRS.Workflows 7.6.0 has no index table. The first start on it reads every
schedule event once, two for each run, in one transaction per event, and no schedule fires until it
has finished.

- On SQLite this took about 0.5 ms per schedule event, roughly 8 minutes per million events.
  PostgreSQL was not measured.
- Later starts read only new events.
- When the first start takes longer than the projection's `CatchUpTimeout` of 30 seconds, the
  dispatcher logs a warning and keeps waiting.

## Deploys that change what a schedule names

| Deploy | Effect | What brings runs back |
| --- | --- | --- |
| The dispatcher no longer lists a workflow that stored schedules name | The dispatcher sends no fire for those schedules, logs an error at each due time, and waits. A decided run whose workflow is not listed waits the same way | A later start that lists the workflow again. The catch-up policy then decides which missed due times run |
| A listed workflow's input type changes, so a decided run's stored input no longer reads as the workflow's input (the stored JSON is a string and the workflow now takes an `int`, for example) | The dispatcher logs one error that names the schedule, the due time, the workflow, and the exception, reports the run's start to the schedule, and the schedule goes on to its next due time. The run for that due time is lost, and the error is the only signal | `Schedules.change` with an input the workflow reads. Until then, later due times store the schedule's current input and fail the same way |

The dispatcher does not read a stored input again after it fails, because with the same code it
would fail the same way. Rolling the deploy back afterwards does not bring the lost run back. Keep
the input's stored form readable, as for any persistent contract.

## Projections that read every entity

A projection without entity names reads the schedule aggregate's events too, as
`Event<ScheduleEvent>`. A handler given to `Fcqrs.projection` that raises for an event it does not
know, such as a catch-all `failwith`, terminates the process. Let such a handler ignore other events,
or name the entities it reads, as
[Add a projection](how-to/add-a-projection.html#Read-only-some-entities) shows.

## Persistent contracts

Every schedule stores its workflow's name and its input's stored form, so the application treats
both as persistent contracts. FCQRS also stores these, so they are persistent contracts too:

- the schedule aggregate's name, `fcqrs-schedule`;
- `ScheduleEvent`, `ScheduleRule`, `MonthDay`, `CatchUp`, and `ScheduleSpec`;
- the snapshot state, `ScheduleState` and `AcceptedDue`;
- run IDs, every run's input, and `ScheduledRun { Schedule; Due; Input }`;
- the index table `fcqrs_schedule_index`, its columns `schedule_id` and `entry`, the text stored in
  `entry`, and its rows;
- the projection's name, `fcqrs-schedule-index`.

Nothing but the dispatcher should write to the index table.

FCQRS.Workflows 7.7 cannot read the rules added in 7.8.0, `DailyAt`, `WeeklyAt`, and `Monthly`, in
a schedule event or an index row. Roll back to 7.7 only before any schedule uses one of them.
