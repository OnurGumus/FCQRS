using System.Globalization;
using FCQRS.Workflows;
using FCQRS.Workflows.CSharp;

// docs:services
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
// docs:end

// docs:backup
// A run's input is a ScheduledRun: the schedule's ID, the due time the run
// serves, and the schedule's input, here the folder to back up.
public sealed class BackupWorkflow(IBackupStore store)
    : Workflow<ScheduledRun<string>, bool>("backup")
{
    // The key holds the run's ID, which holds the due time. It is the same
    // for every attempt of one run and differs between runs.
    public override async WorkflowTask<bool> Run(
        WorkflowContext ctx, ScheduledRun<string> run) =>
        await ctx.Activity("copy", Retry.times(3), attempt =>
            store.Copy(attempt.IdempotencyKey, run.Input));
}
// docs:end

// docs:invoice
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
        await ctx.Activity("issue", Retry.times(3), _ =>
            billing.Issue(Jobs.InvoiceNumber(run), run.Input));
}
// docs:end
