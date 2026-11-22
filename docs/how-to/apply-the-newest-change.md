---
title: Apply only the newest change
category: Apply
categoryindex: 5
index: 12
---

# Apply only the newest change

A reservation changes several times, and each change must reach a booking system. Applying a
change takes time. When versions 2 and 3 arrive while version 1 is applied, only version 3 needs to
be applied next: version 2 is already out of date. One workflow per reservation does this. It
receives each change as a signal, applies one change at a time, and after each one takes every
change that arrived meanwhile and keeps the newest.

The complete programs are in `samples/reservations/fsharp` and `samples/reservations/csharp`. They
reference the projects under `src/`. [Write a workflow](write-a-workflow.html) introduces
activities and signals.

## Describe a change

Each change carries the reservation's ID and a version that grows by one with each change. The
reservation's last change is marked final:

<!-- sample: reservations/fsharp Reservation.fs messages -->
```fsharp
// One change to a reservation, as its event describes it. Each change has
// the next version, and the last change of a reservation is final.
type ReservationChanged =
    { Reservation: string
      Version: int
      Final: bool }

// The external system that applies a change. A call gets a key that is the
// same for every attempt of one step.
type IBooking =
    abstract Apply: key: string * change: ReservationChanged -> Task<int>
```

<div class="cs-alt"></div>

<!-- sample: reservations/csharp Reservation.cs messages -->
```csharp
// One change to a reservation, as its event describes it. Each change has
// the next version, and the last change of a reservation is final.
public sealed record ReservationChanged(
    string Reservation, int Version, bool Final);

// The external system that applies a change. A call gets a key that is the
// same for every attempt of one step.
public interface IBooking
{
    Task<int> Apply(string key, ReservationChanged change);
}
```

The workflow compares versions, never arrival order. A projection can hand an event over again, so
a change can arrive twice or after a newer one.

## Apply one change at a time

The workflow's ID is the reservation's ID. It waits for a `changed` signal, takes every change that
is already stored, and applies the newest:

<!-- sample: reservations/fsharp Reservation.fs workflow -->
```fsharp
// How long the workflow waits for a reservation's next change.
let quietLimit = TimeSpan.FromDays 30.0

// How long it waits after the booking system failed every attempt.
let retryAfter = TimeSpan.FromMinutes 10.0

// Takes every change that is already stored and keeps the newest. A wait of
// zero takes a stored signal or gives None at once.
let rec newest (current: ReservationChanged) =
    workflow {
        match! waitSignalFor<ReservationChanged> "changed" TimeSpan.Zero with
        | Some next when next.Version > current.Version -> return! newest next
        | Some _ -> return! newest current
        | None -> return current
    }

// Applies the newest stored change. When the booking system fails every
// attempt, it waits and applies the newest change again, so the instance
// keeps taking signals.
let rec applyNewest (booking: IBooking) (change: ReservationChanged) =
    workflow {
        let! latest = newest change
        try
            let! _ =
                activity "apply" policy (fun attempt ->
                    booking.Apply(attempt.IdempotencyKey, latest))
            return latest
        with :? WorkflowStepFailedException ->
            let! next = waitSignalFor<ReservationChanged> "changed" retryAfter
            match next with
            | Some newer when newer.Version > latest.Version ->
                return! applyNewest booking newer
            | _ -> return! applyNewest booking latest
    }

// Applies one change at a time. Changes that arrive while one is applied
// are stored, and only the newest of them is applied next.
let rec follow (booking: IBooking) (applied: int) =
    workflow {
        match! waitSignalFor<ReservationChanged> "changed" quietLimit with
        | None -> return applied
        | Some change when change.Version <= applied ->
            return! follow booking applied
        | Some change ->
            let! latest = applyNewest booking change
            if latest.Final then
                return latest.Version
            else
                return! follow booking latest.Version
    }

let definition booking =
    Workflow.define "reservation" (fun (_: string) -> follow booking 0)
```

<div class="cs-alt"></div>

<!-- sample: reservations/csharp Reservation.cs workflow -->
```csharp
public sealed class ReservationWorkflow(IBooking booking)
    : Workflow<string, int>("reservation")
{
    // How long the workflow waits for a reservation's next change.
    public static readonly TimeSpan QuietLimit = TimeSpan.FromDays(30);

    // How long it waits after the booking system failed every attempt.
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    private static readonly RetryPolicy Policy =
        Retry.times(5, TimeSpan.FromSeconds(30));

    public override async WorkflowTask<int> Run(
        WorkflowContext ctx, string reservation) => await Follow(ctx, 0);

    // Takes every change that is already stored and keeps the newest. A wait
    // of zero takes a stored signal or gives no signal at once.
    private static async WorkflowTask<ReservationChanged> Newest(
        WorkflowContext ctx, ReservationChanged current)
    {
        var next = await ctx.WaitSignal<ReservationChanged>(
            "changed", TimeSpan.Zero);
        if (!next.Received)
            return current;
        return await Newest(
            ctx, next.Value.Version > current.Version ? next.Value : current);
    }

    // Applies the newest stored change. When the booking system fails every
    // attempt, it waits and applies the newest change again, so the instance
    // keeps taking signals.
    private async WorkflowTask<ReservationChanged> ApplyNewest(
        WorkflowContext ctx, ReservationChanged change)
    {
        var latest = await Newest(ctx, change);
        try
        {
            await ctx.Activity("apply", Policy, attempt =>
                booking.Apply(attempt.IdempotencyKey, latest));
            return latest;
        }
        catch (WorkflowStepFailedException)
        {
            var next = await ctx.WaitSignal<ReservationChanged>(
                "changed", RetryAfter);
            if (next.Received && next.Value.Version > latest.Version)
                return await ApplyNewest(ctx, next.Value);
            return await ApplyNewest(ctx, latest);
        }
    }

    // Applies one change at a time. Changes that arrive while one is applied
    // are stored, and only the newest of them is applied next.
    private async WorkflowTask<int> Follow(WorkflowContext ctx, int applied)
    {
        var change = await ctx.WaitSignal<ReservationChanged>(
            "changed", QuietLimit);
        if (!change.Received)
            return applied;
        if (change.Value.Version <= applied)
            return await Follow(ctx, applied);
        var latest = await ApplyNewest(ctx, change.Value);
        if (latest.Final)
            return latest.Version;
        return await Follow(ctx, latest.Version);
    }
}
```

- **A signal that arrives during the `apply` step is stored.** Signals with one name are taken
  oldest first, by the next wait for that name.
- **A wait with a timeout of zero takes a stored signal or ends at once.** `newest` calls itself
  until no signal is left, so it reads every change that arrived during the last `apply`.
- **A change at or below the applied version applies nothing.** That covers an event handed over
  twice and one that arrives after a newer one.
- **A failed `apply` does not end the workflow.** When the booking system fails all five attempts,
  `applyNewest` catches `WorkflowStepFailedException`, waits up to `retryAfter` for a newer change,
  and applies the newest change again. The instance keeps taking signals while it waits.
- **The workflow ends after the final change, or when no change arrives for `quietLimit`.** Both
  give the last version it applied.

## Send each change from a projection

A projection over the reservation's events calls `feed` for each change, registered with
`Fcqrs.projection` and `Named` progress in F#, or a named `AddProjection` in C#, as
[Start a workflow from an aggregate event](start-a-workflow-from-an-event.html) shows. Not a
transactional projection: on SQLite its handler runs while the database's single writer lock is
held, and the workflow's start and signal are journal writes that wait for that lock.

A projection handler that raises terminates the process or stops the projection, as
[Add a projection](add-a-projection.html#Handle-failures-visibly) describes, and the restarted
projection hands it the same event again. So `feed` reports a refused change as a value:

<!-- sample: reservations/fsharp Reservation.fs fed -->
```fsharp
// What happened to a change sent to the reservation's workflow.
type Fed =
    // The workflow stored it.
    | Taken
    // The workflow had ended and had applied this version or a newer one:
    // the projection handed the event over again.
    | Repeated
    // The workflow had ended before this version. Nothing will apply it.
    | Late
```

<div class="cs-alt"></div>

<!-- sample: reservations/csharp Reservation.cs fed -->
```csharp
// What happened to a change sent to the reservation's workflow.
public enum Fed
{
    // The workflow stored it.
    Taken,
    // The workflow had ended and had applied this version or a newer one:
    // the projection handed the event over again.
    Repeated,
    // The workflow had ended before this version. Nothing will apply it.
    Late,
}
```

`feed` starts the reservation's workflow and then signals it:

<!-- sample: reservations/fsharp Program.fs feed -->
```fsharp
// What a projection's handler does with each reservation event. A handler
// that raises stops the projection, so feed gives a value for a change that
// the workflow refused.
let feed (change: ReservationChanged) =
    async {
        let id = change.Reservation
        // A start for an instance that exists stores nothing.
        let! _ = reservations.Start id id
        try
            do! Workflows.signal reservations id "changed" change
            return Taken
        with :? InvalidOperationException ->
            // The workflow has ended, after a final change or after the
            // quiet limit. Its result is the last version it applied.
            let! outcome = reservations.Result id (TimeSpan.FromSeconds 30.0)
            match outcome with
            | WorkflowOutcome.Finished applied when change.Version <= applied ->
                return Repeated
            | _ -> return Late
    }
```

<div class="cs-alt"></div>

<!-- sample: reservations/csharp Program.cs feed -->
```csharp
// What a projection's handler does with each reservation event. A handler
// that raises stops the projection, so Feed gives a value for a change that
// the workflow refused.
async Task<Fed> Feed(ReservationChanged change)
{
    var id = change.Reservation;
    // A start for an instance that exists stores nothing.
    await reservations.StartAsync(id, id);
    try
    {
        await reservations.SignalAsync(id, "changed", change);
        return Fed.Taken;
    }
    catch (InvalidOperationException)
    {
        // The workflow has ended, after a final change or after the quiet
        // limit. Its result is the last version it applied.
        var status = await reservations.StatusAsync(id);
        if (status.IsCompleted)
        {
            var applied = await reservations.ResultAsync(
                id, TimeSpan.FromSeconds(30));
            if (change.Version <= applied)
                return Fed.Repeated;
        }
        return Fed.Late;
    }
}
```

The start comes first, so the instance exists when the signal arrives. A second start for the same
ID stores nothing, so `feed` starts the workflow for every change without checking whether it
runs. A signal is stored before it is acknowledged.

A workflow that has ended refuses a signal with `InvalidOperationException`. `feed` then reads its
result, the last version it applied:

- **A version at or below it is `Repeated`:** an event handed over again. The handler can ignore it.
- **A newer version is `Late`:** a change that arrived after the final change or after
  `quietLimit`, which nothing will apply. Record it where a person sees it, such as a table or an
  alert. Choose `quietLimit` so that no reservation can change after it, such as the time the
  reservation expires.

Other exceptions, such as a `TimeoutException` when the workflow does not answer, still raise.

[Add a projection](add-a-projection.html) shows how to run a handler over an aggregate's events.

## Run it

The program holds the booking system's first call until it has sent versions 2, 3, and 2 again,
then sends the final version 4 after version 3 is applied. After the workflow has ended, it sends
version 2 once more and a version 5:

```text
applied r-1 version 1
applied r-1 version 3
applied r-1 version 4
r-1 ended at version 4
version 2 again: Repeated
version 5: Late
```

## What the workflow does not do

- **It applies a change at least once.** An `apply` step that was running when the process stopped
  runs again with the same idempotency key, so the booking system must recognise a repeated key.
- **It retries a failed change without end.** Each failed round adds an `apply` step and a wait to
  the journal. If the booking system can be down for days, count the rounds and alert someone
  after a limit.
- **A new `apply` step has a new idempotency key.** An attempt that timed out may still have
  applied its change, and the next `apply` sends that version or a newer one under another key.
  The booking system should treat a version it has already applied as done.
- **It gives no order across reservations.** Each reservation has its own instance, and two
  reservations' changes are applied independently.
- **Its journal grows with every change.** Each signal is stored, and each round adds an `apply`
  step and a wait that finds no signal. A workflow has no snapshot, so recovery replays all of it.
  A reservation with dozens of changes keeps a short journal; one that changes without end needs
  a different design.
- **A change arrives only through its signal.** If the booking system needs the reservation's
  full state, send it in the change or let the activity read it.

Next: [Start a workflow from an aggregate event](start-a-workflow-from-an-event.html) starts a
workflow for each event an aggregate stores.
