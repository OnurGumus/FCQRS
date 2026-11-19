using FCQRS.Workflows;
using FCQRS.Workflows.CSharp;

// docs:messages
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
// docs:end

// docs:fed
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
// docs:end

// docs:workflow
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
// docs:end
