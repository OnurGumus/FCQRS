---
title: Schedules
category: Understand
categoryindex: 4
index: 13
---

# Schedules: start workflows at due times

A schedule starts a registered workflow at each of its due times. Ada wants a reminder every day at
09:00, Oslo time. The reminder is a workflow whose input is a `ScheduledRun`, and a schedule starts
it:

```fsharp
let remind =
    Workflow.define "remind" (fun (run: ScheduledRun<string>) -> workflow {
        let retry = Retry.times 3 (TimeSpan.FromSeconds 30.0)
        return!
            activity "send" retry (fun attempt ->
                sendReminder attempt.IdempotencyKey run.Input)
    })

let startReminders (api: IActor) =
    async {
        let reminders = Workflows.register api remind
        let schedules =
            Schedules.register api [ ScheduledWorkflow.ofHandle reminders ]
        // Schedule commands raise until the starters are wired.
        Fcqrs.wireSagaStarters api []
        do!
            Schedules.createLatest schedules "ada-daily" "remind" "Drink water"
                (ScheduleRule.Daily(TimeOnly(9, 0), "Europe/Oslo"))
        return schedules
    }
```

<div class="cs-alt"></div>

<!-- sample: workflows/csharp Concepts.cs schedule -->
```csharp
public sealed class Remind()
    : Workflow<ScheduledRun<string>, string>("remind")
{
    public override async WorkflowTask<string> Run(
        WorkflowContext ctx, ScheduledRun<string> run) =>
        await ctx.Activity(
            "send", Retry.times(3, TimeSpan.FromSeconds(30)),
            attempt => SendReminder(attempt.IdempotencyKey, run.Input));
}

public static class Reminders
{
    public static async Task<ScheduleClient> Start(IActor actor)
    {
        var reminders = WorkflowHost.Register(actor, new Remind());
        var schedules = ScheduleHost.Register(
            actor, ScheduleHost.Workflow(reminders));
        // Schedule commands throw until the starters are wired.
        ActorWiring.InitSagaStarterEmpty(actor);
        await schedules.CreateAsync("ada-daily", "remind", "Drink water",
            ScheduleRule.NewDaily(new TimeOnly(9, 0), "Europe/Oslo"));
        return schedules;
    }
}
```

`Schedules.register` registers the schedule aggregate, `fcqrs-schedule`, and starts the actor
system's one **dispatcher**, which starts runs of the listed workflows only. The list takes each
workflow's handle, so the workflows are registered first. Like every aggregate, a schedule stores no
event until the saga starters are wired, so call `Fcqrs.wireSagaStarters` once, after every aggregate
and saga is registered, as the [aggregate guide](../how-to/define-an-aggregate.html) describes.

`Schedules.createLatest` creates the schedule with the default catch-up policy, `CatchUp.Latest`.
`Schedules.create` takes the policy as its last argument. In C#, `CreateAsync` without a policy uses
`CatchUp.Latest`, and an overload takes one. A create returns once the schedule is stored, and a
second create with the same content stores nothing, so a program can create its schedules at every
start.

A C# host built with `AddFcqrs` wires the saga starters when the host starts, so register the
schedules from a singleton resolved after the host has started:

```csharp
builder.Services.AddFcqrs(connectionString, "reminders");
builder.Services.AddWorkflow<Remind, ScheduledRun<string>, string>();
builder.Services.AddSingleton(services => ScheduleHost.Register(
    services.GetRequiredService<IActor>(),
    ScheduleHost.Workflow(services.GetRequiredService<
        IWorkflowClient<ScheduledRun<string>, string>>())));

using var host = builder.Build();
await host.StartAsync();
var schedules = host.Services.GetRequiredService<ScheduleClient>();
await schedules.CreateAsync("ada-daily", "remind", "Drink water",
    ScheduleRule.NewDaily(new TimeOnly(9, 0), "Europe/Oslo"));
```

## A run starts at least once per due time

The schedule aggregate decides each run, and the dispatcher starts it:

1. The dispatcher asks the schedule to fire for a due time. The schedule decides from its own state
   whether that due time runs, and stores the workflow and the input the run gets. The dispatcher's
   index of schedules is only a hint: an index that has not seen a pause, change, or delete cannot
   start a run for a paused or deleted schedule, or for a due time that a change removed.
2. The dispatcher starts the run. Its workflow ID is `<schedule>@<due UTC>`: the schedule's ID, `@`,
   and the due time in UTC in ISO 8601 form, such as `ada-daily@2026-11-15T08:00:00.0000000Z`. Its
   input is `ScheduledRun { Schedule; Due; Input }`, because a workflow body cannot read its own ID.
3. The dispatcher reports the start, and the schedule stores it.

A pause stored before step 1 stops the run. A crash after step 1 leaves a decided run with no
reported start. After the restart the dispatcher starts it again with the same ID, and a second
start of an existing workflow stores nothing. A run therefore starts at least once for each due time
the schedule accepts, and one workflow instance serves it. A run is an ordinary workflow instance:
one that faults ends only that instance, and one still going at the next due time runs alongside
the next run.

## Due times, downtime, and changes

`ScheduleRule.Daily(time, zone)` and `ScheduleRule.Weekly(days, time, zone)` take a local time and a
time-zone ID. Due times are computed in the zone and stored in UTC, so Ada's reminder runs at 09:00
Oslo time in winter (08:00 UTC) and in summer (07:00 UTC). A local time that a clock change skips
runs at the next local time that exists, and one that occurs twice runs once, at the first
occurrence. `ScheduleRule.Every interval` counts from the creation or the last change.

Ada also pays rent on the last day of each month and wants a reminder in the morning and in the
evening of that day:

```fsharp
let remindRent (schedules: ScheduleHandle) =
    // 09:00 and 18:00, Oslo time, on the last day of each month.
    let rule =
        ScheduleRule.Monthly(
            [ MonthDay.LastDay ],
            [ TimeOnly(9, 0); TimeOnly(18, 0) ],
            "Europe/Oslo"
        )
    Schedules.createLatest schedules "ada-rent" "remind" "Pay the rent" rule
```

<div class="cs-alt"></div>

<!-- sample: workflows/csharp Concepts.cs monthly -->
```csharp
public static Task RemindRent(ScheduleClient schedules)
{
    // 09:00 and 18:00, Oslo time, on the last day of each month.
    var rule = ScheduleRule.NewMonthly(
        [MonthDay.LastDay],
        [new TimeOnly(9, 0), new TimeOnly(18, 0)],
        "Europe/Oslo");
    return schedules.CreateAsync("ada-rent", "remind", "Pay the rent", rule);
}
```

A monthly rule's days can also be a day number, such as `MonthDay.Day 15`, or a weekday such as
`MonthDay.Nth(3, DayOfWeek.Wednesday)` or `MonthDay.Last DayOfWeek.Thursday`. `Day 31` runs on the
last day of a shorter month. `ScheduleRule.DailyAt` and `ScheduleRule.WeeklyAt` take several times a
day. The [Schedules reference](../schedules-reference.html#Schedule-rules) lists every rule.

A due time is missed when the dispatcher could not fire on time, for example while the process was
stopped. The catch-up policy decides which missed due times run:

| Case | What runs |
| --- | --- |
| Missed due times, `CatchUp.Latest` (the default) | One run, for the most recent |
| Missed due times, `CatchUp.All` | One run for each, oldest first, at most for the 1000 most recent |
| Missed due times, `CatchUp.None` | None; the schedule waits for the next due time. A fire up to one minute late still counts as on time |
| `Schedules.pause`, then `Schedules.resume` | Nothing during the pause. Runs continue from the first due time after the resume: a due time inside the pause never runs, whatever the policy |
| `Schedules.change` | The new workflow, input, rule, and policy. No due time at or before the change runs |
| `Schedules.delete` | Nothing more. The ID cannot be used for a new schedule, so a run ID never belongs to two schedules |
| A run decided before a change, pause, or delete | The run still starts, on the workflow and with the input stored when it was decided |

A fire more than one second before its due time is refused, so a fault in the dispatcher cannot
start a run more than a second early.

## What a schedule does not guarantee

- **Exactly-once side effects.** A run's steps follow the [workflow rules](workflows.html): an activity
  runs at least once. Its idempotency key includes the run's ID, so it differs per due time and
  stays the same across the attempts of one run. The external system must honour it.
- **Precision below a second.** A run starts after its due time, once the dispatcher has woken, the
  schedule has stored its decision, and the run's start is stored.
- **More than one node.** The dispatcher assumes it is the only one, and FCQRS runs on one node.
- **A run whose stored input no longer reads.** After a deploy that changes a workflow's input type,
  a decided run whose stored input the workflow cannot read is lost, and one logged error is the
  only signal.

The application chooses each user's time zone, keeps run steps safe to repeat, and treats the
workflow's name and the input's stored form as persistent contracts: every schedule stores both.

## C# equivalents

| F# | C# |
|---|---|
| `Schedules.register api [ ScheduledWorkflow.ofHandle handle ]` | `ScheduleHost.Register(actor, ScheduleHost.Workflow(client))` |
| `Schedules.createLatest`, with the default `CatchUp.Latest` | `schedules.CreateAsync(id, workflow, input, rule)` |
| `Schedules.create`, `Schedules.change` | `schedules.CreateAsync`, `schedules.ChangeAsync`, with a `CatchUp` |
| `Schedules.pause`, `resume`, `delete` | `schedules.PauseAsync`, `ResumeAsync`, `DeleteAsync` |
| `Schedules.state`, `None` when the index has not read it | `schedules.State(id)`, `null` when the index has not read it |
| `ScheduleRule.Daily(time, zone)` | `ScheduleRule.NewDaily(time, zone)`, and `NewWeekly`, `NewEvery`, `NewDailyAt`, `NewWeeklyAt`, `NewMonthly` |
| `MonthDay.Nth(3, DayOfWeek.Wednesday)`, `MonthDay.LastDay` | `MonthDay.NewNth(3, DayOfWeek.Wednesday)`, `MonthDay.LastDay`, and `NewDay`, `NewLast` |

[Schedules reference](../schedules-reference.html) lists the exceptions each call raises, the ID
limits, the dispatcher's timing, the index table and the database rights it needs, what a deploy
can break, and every persistent contract. [Run scheduled jobs](../how-to/run-scheduled-jobs.html)
builds a nightly backup and a weekly invoice as runnable F# and C# programs.

