# Monthly and multi-time schedule rules: design

## Intent

The owner wants schedules for monthly tasks that run for years and for reports taken twice a day.
Today `ScheduleRule` has `Daily(time, zone)`, `Weekly(days, time, zone)` and `Every(interval)`: a
monthly rule and several times per day are missing. The owner also asked for "last Thursday" and
"3rd Wednesday of the month".

## Goals

- `ScheduleRule.Monthly(days: MonthDay list, times: TimeOnly list, zone)` with
  `MonthDay = Day of int | LastDay | Nth of int * DayOfWeek | Last of DayOfWeek`.
- `ScheduleRule.DailyAt(times, zone)` and `ScheduleRule.WeeklyAt(days, times, zone)` for several
  times a day.
- Released as FCQRS.Workflows 7.8.0 (minor: new public API).

## Non-goals

- Changing the shape of the existing `Daily`, `Weekly` and `Every` cases. Schedules stored by 7.7
  must read back unchanged.
- A fifth weekday (`Nth(5, ...)`); `Last` covers it.
- Cron expressions. Continue-as-new (a separate design).

## Rules

- `Day n` (1 to 31): a month without day `n` runs on its last day. The owner approved this over
  skipping the month.
- `LastDay`: the month's last day.
- `Nth(n, day)` with `n` from 1 to 4: the nth such weekday of the month.
- `Last day`: the month's last such weekday.
- Several days and several times combine into one set of local date-times per month; duplicates
  collapse (for example `Day 31` and `LastDay` in a 31-day month).
- Times and dates are local to the zone; daylight saving, catch-up, the 1000 cap on `All`, the
  minimum interval rules and the dispatcher are unchanged. A skipped local time runs at the next
  valid time; a repeated one runs once, at the first occurrence, as for `Daily`.
- `ScheduleRule.validate` rejects: an empty day or time list, `Day` outside 1 to 31, `Nth` outside
  1 to 4, an unknown zone, a value that is not a day of the week.
- Same-content comparison for a repeated `create` sorts and deduplicates days and times, as it does
  for `Weekly` days today.

## Contracts

- `MonthDay`, `Monthly`, `DailyAt`, `WeeklyAt` are stored in schedule events and the index row; they
  are persistent contracts from 7.8.0 and get golden JSON tests like the existing rule cases.
- C#: `ScheduleRule.NewMonthly(...)`, `MonthDay.NewNth(3, DayOfWeek.Wednesday)` and so on, checked in
  `test/Workflows.CSharp.Tests`.

## Docs

- The schedules section of `docs/concepts/workflows.md` gets one short example of a monthly rule;
  the full rule table goes on `docs/schedules-reference.md`. The scheduled-jobs guide can mention
  the monthly rule where it says there is none today.
