using System.Collections.Concurrent;
using FCQRS.Workflows;
using FCQRS.Workflows.CSharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using static FCQRS.CSharp;
using static Jobs;

// The journal and the schedules' index are in this file.
var database = Path.Combine(AppContext.BaseDirectory, "jobs.db");
var logging = LoggerFactory.Create(_ => { });
var configuration = new ConfigurationBuilder().Build();
var actor = ActorApi.Create(
    configuration, logging, $"Data Source={database}", "jobs");

var store = new MemoryBackupStore();
var billing = new MemoryBilling();

// docs:register
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
// docs:end

// docs:create
// A create with the content of the stored schedule stores nothing, so the
// program creates its schedules at every start.
await schedules.CreateAsync("nightly-backup", "backup", "/srv/app/data",
    ScheduleRule.NewDaily(new TimeOnly(2, 0), Zone));

await schedules.CreateAsync("weekly-invoice", "invoice", "Ada",
    ScheduleRule.NewWeekly([DayOfWeek.Monday], new TimeOnly(8, 0), Zone));
// docs:end

// docs:state
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
// docs:end

// docs:demo
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
// docs:end

Console.WriteLine("demo: no run is waiting");
await actor.Stop();

// Waits for a condition, for at most 30 seconds.
static async Task WaitFor(string what, Func<bool> condition)
{
    var deadline = DateTime.UtcNow.AddSeconds(30);
    while (!condition())
    {
        if (DateTime.UtcNow > deadline)
            throw new TimeoutException($"timed out waiting for {what}");
        await Task.Delay(100);
    }
}

// Stand-ins for the backup store and the billing system. Each remembers the
// keys it has seen, in memory only.
public sealed class MemoryBackupStore : IBackupStore
{
    public readonly ConcurrentDictionary<string, string> Copies = new();

    public Task<bool> Copy(string key, string folder)
    {
        var copied = Copies.TryAdd(key, folder);
        if (copied) Console.WriteLine($"  copied {folder}");
        return Task.FromResult(copied);
    }
}

public sealed class MemoryBilling : IBilling
{
    public readonly ConcurrentDictionary<string, string> Invoices = new();

    public Task<bool> Issue(string number, string customer)
    {
        var issued = Invoices.TryAdd(number, customer);
        if (issued) Console.WriteLine($"  issued invoice {number}");
        return Task.FromResult(issued);
    }
}
