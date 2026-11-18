---
title: Run scheduled jobs
category: Apply
categoryindex: 5
index: 11
---

# Run scheduled jobs

This recipe runs two jobs in one service on one machine: a backup of a folder every night at 02:00
and an invoice for a customer every Monday at 08:00, both on Oslo time. Each job is a workflow, and
a schedule starts it at each due time. The schedules, the runs, and their results are stored in the
service's journal, a SQLite file next to the executable.

A service like this can also run its jobs on a separate workflow server, such as Temporal, which
keeps the schedules and the run history in its own process and database. Here the service's own
process holds them, and its journal stores them. [What replaces the workflow
server](#What-replaces-the-workflow-server) lists where that state lives and what the service must
do instead.

> **Motivation:** Use schedules when work must start at local times of day, survive restarts, and
> catch up on the due times that passed while the machine was off.

[Write a workflow](write-a-workflow.html) explains workflows, steps, and idempotency keys. The
complete programs are in `samples/schedules/fsharp` and `samples/schedules/csharp`. They reference
the projects under `src/`; `Schedules.createLatest`, and C#'s `CreateAsync` without a catch-up
policy, need FCQRS.Workflows 7.7.0 or later.

## Give each job's external system a key

The backup store and the billing system stand outside the service. Each call carries a key:

<!-- sample: schedules/fsharp Jobs.fs services -->
```fsharp
// The systems the jobs call. Each does its work once per key, so a repeated
// call with the same key changes nothing. Both return whether they did it.
type IBackupStore =
    abstract Copy: key: string * folder: string -> Task<bool>

type IBilling =
    abstract Issue: invoice: string * customer: string -> Task<bool>
```

<div class="cs-alt"></div>

<!-- sample: schedules/csharp Jobs.cs services -->
```csharp
// The systems the jobs call. Each does its work once per key, so a repeated
// call with the same key changes nothing. Both return whether they did it.
public interface IBackupStore
{
    Task<bool> Copy(string key, string folder);
}

public interface IBilling
{
    Task<bool> Issue(string invoice, string customer);
}
```

The stand-ins in the samples keep the keys in memory. A real backup store or billing system must
keep them where a restart does not lose them.

## Write each job as a workflow

<!-- sample: schedules/fsharp Jobs.fs backup -->
```fsharp
// A run's input is a ScheduledRun: the schedule's ID, the due time the run
// serves, and the schedule's input, here the folder to back up.
let backup (store: IBackupStore) =
    Workflow.define "backup" (fun (run: ScheduledRun<string>) -> workflow {
        // The key holds the run's ID, which holds the due time. It is the same
        // for every attempt of one run and differs between runs.
        let retry = Retry.times 3 (TimeSpan.FromMinutes 10.0)
        return!
            activity "copy" retry (fun attempt ->
                store.Copy(attempt.IdempotencyKey, run.Input))
    })
```

<div class="cs-alt"></div>

<!-- sample: schedules/csharp Jobs.cs backup -->
```csharp
// A run's input is a ScheduledRun: the schedule's ID, the due time the run
// serves, and the schedule's input, here the folder to back up.
public sealed class BackupWorkflow(IBackupStore store)
    : Workflow<ScheduledRun<string>, bool>("backup")
{
    // The key holds the run's ID, which holds the due time. It is the same
    // for every attempt of one run and differs between runs.
    public override async WorkflowTask<bool> Run(
        WorkflowContext ctx, ScheduledRun<string> run) =>
        await ctx.Activity(
            "copy", Retry.times(3, TimeSpan.FromMinutes(10)),
            attempt => store.Copy(attempt.IdempotencyKey, run.Input));
}
```

A workflow that a schedule starts takes a `ScheduledRun<'Input>` (`ScheduledRun<TInput>` in C#). It
holds the schedule's ID, the due time the run serves in UTC, and the schedule's input. A workflow
body cannot read its own ID, so this is how a run knows which due time it serves.

Each run is a workflow instance whose ID is the schedule's ID, `@`, and the due time in UTC in ISO
8601 form. An activity's idempotency key is the workflow's name, the instance's ID, and the step's
position, so the backup due at 02:00 Oslo time on 16 November 2026 gets the key
`backup/nightly-backup@2026-11-16T01:00:00.0000000Z/0`. The key differs every night. It stays the
same for every attempt of one run and after a restart, so a store that honours it copies the folder
once per run.

The invoice uses a key of its own:

<!-- sample: schedules/fsharp Jobs.fs invoice -->
```fsharp
// The time zone of both schedules.
let zone = "Europe/Oslo"

// The invoice number: the customer and the ISO week of the due time in the
// schedule's zone, such as "Ada-2026-W47".
let invoiceNumber (run: ScheduledRun<string>) =
    let local =
        TimeZoneInfo.ConvertTimeFromUtc(
            run.Due, TimeZoneInfo.FindSystemTimeZoneById zone)
    let week = ISOWeek.GetWeekOfYear local
    $"{run.Input}-{ISOWeek.GetYear local}-W{week:D2}"

let invoice (billing: IBilling) =
    Workflow.define "invoice" (fun (run: ScheduledRun<string>) -> workflow {
        // Keyed by the week, not by the run: a second run in the same week
        // finds the invoice issued.
        let retry = Retry.times 3 (TimeSpan.FromSeconds 30.0)
        return!
            activity "issue" retry (fun _ ->
                billing.Issue(invoiceNumber run, run.Input))
    })
```

<div class="cs-alt"></div>

<!-- sample: schedules/csharp Jobs.cs invoice -->
```csharp
public static class Jobs
{
    // The time zone of both schedules.
    public const string Zone = "Europe/Oslo";

    // The invoice number: the customer and the ISO week of the due time in the
    // schedule's zone, such as "Ada-2026-W47".
    public static string InvoiceNumber(ScheduledRun<string> run)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(
            run.Due, TimeZoneInfo.FindSystemTimeZoneById(Zone));
        var week = ISOWeek.GetWeekOfYear(local);
        return $"{run.Input}-{ISOWeek.GetYear(local)}-W{week:D2}";
    }
}

public sealed class InvoiceWorkflow(IBilling billing)
    : Workflow<ScheduledRun<string>, bool>("invoice")
{
    // Keyed by the week, not by the run: a second run in the same week
    // finds the invoice issued.
    public override async WorkflowTask<bool> Run(
        WorkflowContext ctx, ScheduledRun<string> run) =>
        await ctx.Activity(
            "issue", Retry.times(3, TimeSpan.FromSeconds(30)),
            _ => billing.Issue(Jobs.InvoiceNumber(run), run.Input));
}
```

A due time that runs has one run, and a second start of a run stores nothing, so a key built from
the run would already stop a second invoice for the same due time. The week also covers two runs in
one week. Changing the rule from Monday to Wednesday after Monday's run lets Wednesday's run start,
because a change only stops the due times at or before it. With the week in the key, the billing
system finds the invoice issued. The number is computed from `run.Due`, the due time the run serves,
not the time it starts.

## Register the workflows and the dispatcher

<!-- sample: schedules/fsharp Program.fs register -->
```fsharp
let backups = Workflows.register api (backup store)
let invoicing = Workflows.register api (invoice billing)

// The dispatcher starts only the workflows in this list.
let schedules =
    Schedules.register api [
        ScheduledWorkflow.ofHandle backups
        ScheduledWorkflow.ofHandle invoicing
    ]

// A schedule is an aggregate, and an aggregate stores nothing until the saga
// starters are wired. Wire them after the last registration.
Fcqrs.wireSagaStarters api []
```

<div class="cs-alt"></div>

<!-- sample: schedules/csharp Program.cs register -->
```csharp
var backups = WorkflowHost.Register(actor, new BackupWorkflow(store));
var invoicing = WorkflowHost.Register(actor, new InvoiceWorkflow(billing));

// The dispatcher starts only the workflows in this list.
var schedules = ScheduleHost.Register(
    actor,
    ScheduleHost.Workflow(backups),
    ScheduleHost.Workflow(invoicing));

// A schedule is an aggregate, and an aggregate stores nothing until the saga
// starters are wired. Wire them after the last registration.
ActorWiring.InitSagaStarterEmpty(actor);
```

The order matters:

1. `Workflows.register` (`WorkflowHost.Register` in C#) registers each workflow and resumes the runs
   that were going when the process stopped.
2. `Schedules.register` (`ScheduleHost.Register`) takes the workflows' handles, so the workflows are
   registered first. It registers the schedule aggregate, `fcqrs-schedule`, and starts the actor
   system's one **dispatcher**, which starts runs of the listed workflows only. A second call on the
   same actor system raises `InvalidOperationException`.
3. `Fcqrs.wireSagaStarters` (`ActorWiring.InitSagaStarterEmpty`) comes after the last aggregate or
   saga is registered. Every aggregate, the schedule aggregate included, waits for the saga
   starters before it stores an event, and terminates the process when they are not wired within
   `akka.fcqrs.saga-start-timeout`. Until they are wired, `Schedules.create` raises
   `InvalidOperationException` without sending anything, and the dispatcher starts no run. A
   service without sagas passes an empty list.

A C# host built with `AddFcqrs` wires the saga starters when the host starts.
[Schedules](../concepts/schedules.html) shows how to
register the schedules there.

## Create the schedules

<!-- sample: schedules/fsharp Program.fs create -->
```fsharp
// A create with the content of the stored schedule stores nothing, so the
// program creates its schedules at every start.

// After downtime, only the latest backup matters: CatchUp.Latest.
Schedules.createLatest schedules "nightly-backup" "backup" "/srv/app/data"
    (ScheduleRule.Daily(TimeOnly(2, 0), zone))
|> Async.RunSynchronously

// Every week must be invoiced: CatchUp.All runs each missed Monday.
Schedules.create schedules "weekly-invoice" "invoice" "Ada"
    (ScheduleRule.Weekly([ DayOfWeek.Monday ], TimeOnly(8, 0), zone))
    CatchUp.All
|> Async.RunSynchronously
```

<div class="cs-alt"></div>

<!-- sample: schedules/csharp Program.cs create -->
```csharp
// A create with the content of the stored schedule stores nothing, so the
// program creates its schedules at every start.

// After downtime, only the latest backup matters: CatchUp.Latest.
await schedules.CreateAsync("nightly-backup", "backup", "/srv/app/data",
    ScheduleRule.NewDaily(new TimeOnly(2, 0), Zone));

// Every week must be invoiced: CatchUp.All runs each missed Monday.
await schedules.CreateAsync("weekly-invoice", "invoice", "Ada",
    ScheduleRule.NewWeekly([DayOfWeek.Monday], new TimeOnly(8, 0), Zone),
    CatchUp.All);
```

`ScheduleRule.Daily(TimeOnly(2, 0), zone)` is due every day at 02:00 local time in the zone
`Europe/Oslo`. `ScheduleRule.Weekly([ DayOfWeek.Monday ], TimeOnly(8, 0), zone)` is due on Mondays
at 08:00. The zone is a time-zone ID, and `create` raises `ArgumentException` for one the system
cannot read, before anything is sent. FCQRS computes each due time in the zone and stores it in UTC,
so the backup runs at 01:00 UTC in winter and at 00:00 UTC in summer.

02:00 is the hour in which Oslo changes its clocks. On 28 March 2027 the clocks move from 02:00 to
03:00, so 02:00 does not exist, and that night's backup runs at 03:00, the next local time that
does. On 25 October 2026 the clocks move back from 03:00 to 02:00, so 02:00 occurs twice, and the
backup runs once, at the first occurrence. A time outside the hour from 02:00 to 03:00 avoids both
cases in this zone.

The two schedules differ in their catch-up policy, which decides what runs for the due times that
passed while the service was stopped. A missed backup is replaced by the next one, so the backup
uses `Schedules.createLatest`, whose policy `CatchUp.Latest` runs only the most recent. Every week
must be invoiced, so the invoice uses `Schedules.create` with `CatchUp.All` (in C#, the
`CreateAsync` overload that takes a policy), which runs each missed Monday, oldest first. [After the
machine was off](#After-the-machine-was-off) shows both.

A create returns once the schedule is stored. A second create with the same ID and the same content
stores nothing, so the program creates both schedules at every start. A create with the same ID and
a different content raises `InvalidOperationException`, and so does a create whose workflow is not
in the dispatcher's list or takes another input type.

A create is compared with the content the schedule was first created with, also after a change. So
editing the rule in the create call, for example to move the backup to 03:00, makes the next
start's create raise. Move a stored schedule with `Schedules.change` (`ChangeAsync` in C#), which
replaces its rule, input, workflow, and catch-up policy. A change to the content the schedule
already has stores nothing, so the program can keep the create as it was and call change with the
new rule at every start.

## Read the next due time

<!-- sample: schedules/fsharp Program.fs state -->
```fsharp
// The index reads the schedules through a projection, so a schedule appears
// in it shortly after its create returns. NextDue is in UTC.
let nextRun (id: string) =
    waitFor id (fun () -> (Schedules.state schedules id).IsSome)
    let next = (Schedules.state schedules id).Value.NextDue.Value
    TimeZoneInfo.ConvertTimeFromUtc(
        next, TimeZoneInfo.FindSystemTimeZoneById zone)

let backupAt = nextRun "nightly-backup"
printfn "nightly-backup: next run at %s %s time"
    (backupAt.ToString "HH:mm") zone
let invoiceAt = nextRun "weekly-invoice"
printfn "weekly-invoice: next run on %O at %s %s time"
    invoiceAt.DayOfWeek (invoiceAt.ToString "HH:mm") zone
```

<div class="cs-alt"></div>

<!-- sample: schedules/csharp Program.cs state -->
```csharp
// The index reads the schedules through a projection, so a schedule appears
// in it shortly after its create returns. NextDue is in UTC.
async Task<DateTime> NextRun(string id)
{
    await WaitFor(id, () => schedules.State(id) is not null);
    var next = schedules.State(id)!.NextDue!.Value;
    return TimeZoneInfo.ConvertTimeFromUtc(
        next, TimeZoneInfo.FindSystemTimeZoneById(Zone));
}

var backupAt = await NextRun("nightly-backup");
Console.WriteLine($"nightly-backup: next run at {backupAt:HH:mm} {Zone} time");
var invoiceAt = await NextRun("weekly-invoice");
Console.WriteLine($"weekly-invoice: next run on {invoiceAt.DayOfWeek}"
    + $" at {invoiceAt:HH:mm} {Zone} time");
```

`Schedules.state` (`State` in C#) reads the dispatcher's index, not the schedule. The index follows
the schedules' events through a projection, so a schedule that was just created can be missing for
a moment, and the program waits for it. `NextDue` is the due time the schedule accepts next, in UTC,
or nothing while the schedule is paused or deleted.

## Watch runs start

A nightly schedule starts nothing while the program runs for a few seconds. The program therefore
also creates a demo schedule that starts the backup workflow every two seconds:

<!-- sample: schedules/fsharp Program.fs demo -->
```fsharp
// A deleted schedule's ID cannot be used again, so each start creates a demo
// schedule with a new ID. CatchUp.All runs every due time, even one whose
// run starts late.
let demo = $"demo-{Guid.NewGuid():N}"
printfn "demo: a backup every 2 seconds"

Schedules.create schedules demo "backup" "demo-data"
    (ScheduleRule.Every(TimeSpan.FromSeconds 2.0)) CatchUp.All
|> Async.RunSynchronously

let runs () =
    copies.Keys |> Seq.filter (fun key -> key.Contains demo) |> Seq.length

waitFor "three demo runs" (fun () -> runs () >= 3)
// An Every rule counts from the creation.
let created = (Schedules.state schedules demo).Value.Anchor
Schedules.delete schedules demo |> Async.RunSynchronously
printfn "demo: deleted"

// A run's workflow ID is the schedule's ID, "@", and the due time in UTC.
for run in 1..3 do
    let due = created.AddSeconds(2.0 * float run)
    let id = $"""{demo}@{due.ToString "O"}"""
    let outcome =
        backups.Result id (TimeSpan.FromSeconds 10.0) |> Async.RunSynchronously
    let copied =
        match outcome with
        | WorkflowOutcome.Finished copied -> copied
        | other -> failwith $"{id} ended with {other}"
    let result = if copied then "copied" else "already copied"
    printfn $"demo: run {run}, {2 * run} s after creation: {result}"

// A run decided before the delete still starts. The schedule leaves the index
// once no such run is waiting.
waitFor "the demo schedule to leave the index" (fun () ->
    (Schedules.state schedules demo).IsNone)
```

<div class="cs-alt"></div>

<!-- sample: schedules/csharp Program.cs demo -->
```csharp
// A deleted schedule's ID cannot be used again, so each start creates a demo
// schedule with a new ID. CatchUp.All runs every due time, even one whose
// run starts late.
var demo = $"demo-{Guid.NewGuid():N}";
Console.WriteLine("demo: a backup every 2 seconds");

await schedules.CreateAsync(demo, "backup", "demo-data",
    ScheduleRule.NewEvery(TimeSpan.FromSeconds(2)), CatchUp.All);

int Runs() => store.Copies.Keys.Count(key => key.Contains(demo));

await WaitFor("three demo runs", () => Runs() >= 3);
// An Every rule counts from the creation.
var created = schedules.State(demo)!.Anchor;
await schedules.DeleteAsync(demo);
Console.WriteLine("demo: deleted");

// A run's workflow ID is the schedule's ID, "@", and the due time in UTC.
for (var run = 1; run <= 3; run++)
{
    var due = created.AddSeconds(2 * run);
    var id = $"{demo}@{due:O}";
    // Throws when the run faulted or was cancelled.
    var copied = await backups.ResultAsync(id, TimeSpan.FromSeconds(10));
    var result = copied ? "copied" : "already copied";
    Console.WriteLine($"demo: run {run}, {2 * run} s after creation: {result}");
}

// A run decided before the delete still starts. The schedule leaves the index
// once no such run is waiting.
await WaitFor("the demo schedule to leave the index",
    () => schedules.State(demo) is null);
```

Run it:

```bash
dotnet run --project samples/schedules/fsharp -c Release
dotnet run --project samples/schedules/csharp -c Release
```

Both programs print the same lines.

```text
nightly-backup: next run at 02:00 Europe/Oslo time
weekly-invoice: next run on Monday at 08:00 Europe/Oslo time
demo: a backup every 2 seconds
  copied demo-data
  copied demo-data
  copied demo-data
demo: deleted
demo: run 1, 2 s after creation: copied
demo: run 2, 4 s after creation: copied
demo: run 3, 6 s after creation: copied
demo: no run is waiting
```

- `ScheduleRule.Every` counts from the schedule's creation, the `Anchor` in its state, so the due
  times are 2, 4, and 6 seconds after it.
- `Schedules.create` takes the catch-up policy as its last argument. The demo passes `CatchUp.All`,
  so a run that starts late does not make the dispatcher skip a due time.
- `backups.Result` (`ResultAsync`) reads each run's result by its ID, built from the schedule's ID
  and the due time.
- `Schedules.delete` ends the schedule. A run that the schedule decided before the delete still
  starts, and the schedule leaves the index once no such run waits. A deleted schedule's ID cannot
  be used for a new schedule, so the program gives its demo schedule a new ID at each start.

Run the program again. It starts on the first run's journal: both creates find the same content and
store nothing, and the next due times are unchanged. It prints the same lines unless 02:00 Oslo
time or Monday 08:00 passed between the two runs. Then the run for that due time starts when the
program starts, and its line appears too, as [After the machine was
off](#After-the-machine-was-off) describes.

## After the machine was off

Nothing starts a run while the process is stopped. When it starts again, the dispatcher reads its
index and the schedule events stored since the last stop, and starts no run before it has read
both. Then the catch-up policy decides which of the due times that passed get a run:

- `CatchUp.Latest`, which `createLatest` uses, starts one run, for the most recent. A machine that
  was off for three nights makes one backup, for the last night's 02:00. The earlier nights get no
  backup, which is what a backup needs: the latest copy replaces them.
- `CatchUp.All`, which the invoice uses, starts one run for each, oldest first, but at most for the
  1000 most recent. A machine that was off across two Mondays starts two invoice runs when it comes
  back. Each run's `run.Due` is its own Monday 08:00, so each gets that week's invoice number, and
  the billing system issues one invoice per week. With `CatchUp.Latest`, the earlier week would
  never be invoiced.
- `CatchUp.None` starts none and waits for the next due time. A run asked for up to one minute after
  its due time still counts as on time.

The schedule decides each run, and stores the decision, before the run starts. A crash between the
decision and the start leaves a decided run, which the dispatcher starts after the restart with the
same ID; a second start of a run stores nothing. A run therefore starts at least once for each due
time the schedule accepts, and one workflow instance serves it.

## What replaces the workflow server

The schedules, the dispatcher, the workflows, and their runs are in the service's process. What they
store is in `jobs.db`:

- Akka.NET's `journal` table holds the schedules' events and each run's steps and results, beside
  its `snapshot` and `tags` tables.
- `fcqrs_schedule_index` holds the dispatcher's index, one row per schedule. The projection
  `fcqrs-schedule-index` writes each row in the transaction that stores its read position.
- `fcqrs_projection_progress` and `fcqrs_projection_progress_lock` hold that projection's read
  positions.

The dispatcher creates the last three tables on first use, so the database user needs the rights
that the [Schedules reference](../schedules-reference.html#Database-rights) lists. A start reads one row per schedule and
the schedule events stored since the last stop, not the history of every run. Between due times the
dispatcher sleeps until the earliest one. While fewer than 16 starts are running, it wakes at least
every `akka.fcqrs.schedule-check-interval`, one minute by default, so a changed clock delays a run
by at most that long. [Configure the database](configure-the-database.html) moves the journal to
PostgreSQL.

The service now takes over what the server did: it must be running for anything to start. Run it
as the machine runs its other services, such as a systemd unit or a Windows service, so it starts
with the machine. The catch-up policy decides what runs for the time it was off.

## What a schedule does not do

- **Every rule a calendar can express.** Besides `Daily`, `Weekly`, and `Every`, `ScheduleRule` has
  `Monthly` for days of the month, such as the first or the last Friday, and `DailyAt` and
  `WeeklyAt` for several times a day; the
  [Schedules reference](../schedules-reference.html#Schedule-rules) lists them. For a job these
  cannot express, such as every other Tuesday, use a weekly schedule whose run reads the local date
  of `run.Due` and ends without work in the other weeks. The date comes from the run's input, so the
  body stays deterministic. Each run is still a stored workflow instance.
- **More than one node.** The dispatcher assumes it is the only one, and FCQRS runs on one node.
  Run one copy of the service.
- **Exactly-once work.** A run starts at least once for each due time the schedule accepts, and its
  activities run at least once: a step that was running when the process stopped runs again, with
  the same key. The backup store and the billing system must honour their keys.
- **Precision below a second.** A run starts after its due time, once the dispatcher has woken, the
  schedule has stored its decision, and the run's start is stored.
- **A quick first start after upgrading from FCQRS.Workflows 7.6.0.** A journal written by 7.6.0 has
  no `fcqrs_schedule_index`. The first start on 7.7.0 reads every stored schedule event once to
  build it, two events per run, and starts no run until it has finished. On SQLite this took about
  0.5 ms per event. Later starts read only the new events. The
  [Schedules reference](../schedules-reference.html#First-start-after-FCQRS-Workflows-7-6-0) has
  the details.

## What this recipe leaves to you

- The stand-in services keep their keys in memory. Real ones must store them.
- Treat these as persistent contracts, which a refactoring must not change:
  - the workflow names `backup` and `invoice`, which every schedule stores and which the backup key
    begins with, so a renamed workflow gives every run a new key;
  - the step names `copy` and `issue`, which each run's journal stores;
  - the input type `ScheduledRun<string>` and the schedule inputs, which every schedule and run
    stores. A decided run whose stored input no longer reads as the input type is lost: the
    dispatcher logs one error and the schedule goes on;
  - the invoice-number format and its zone `Europe/Oslo`, because the billing system recognises a
    repeated invoice by that number: a new format issues a second invoice for a week already billed;
  - the schedule IDs and the run IDs built from them.
- A run whose activity fails three times faults and stays faulted. The schedule goes on with the
  next due time. Watch for faulted runs, and decide whether a missed backup or invoice needs a
  person.
- A run that is still going at the next due time runs alongside the next one.

Next: [Schedules](../concepts/schedules.html) covers
pausing, resuming, and changing schedules, and what the dispatcher's index guarantees. The
[Schedules reference](../schedules-reference.html) lists the exceptions each call raises, the ID
limits, the dispatcher's timing, and the index table.
