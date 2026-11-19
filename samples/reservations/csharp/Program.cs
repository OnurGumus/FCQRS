using System.Collections.Concurrent;
using FCQRS;
using FCQRS.Workflows;
using FCQRS.Workflows.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Each run starts with an empty journal, so its output is the same.
var database = Path.Combine(AppContext.BaseDirectory, "reservations.db");
foreach (var file in new[] { database, database + "-wal", database + "-shm" })
    File.Delete(file);

var booking = new Booking();
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Services.AddFcqrs($"Data Source={database}", "reservations");
builder.Services.AddSingleton<IBooking>(booking);
builder.Services.AddWorkflow<ReservationWorkflow, string, int>();

using var host = builder.Build();
await host.StartAsync();
var reservations =
    host.Services.GetRequiredService<IWorkflowClient<string, int>>();

// docs:feed
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
// docs:end

Task<Fed> Send(int version, bool final) =>
    Feed(new ReservationChanged("r-1", version, final));

// Version 1 starts the workflow. Versions 2 and 3, and version 2 once more,
// arrive while version 1 is applied.
await Send(1, false);
await booking.Started;
foreach (var version in new[] { 2, 3, 2 })
    await Send(version, false);
booking.Release();

// Version 4, the final one, arrives after version 3 is applied.
while (!booking.Applied.Contains(3))
    await Task.Delay(50);
await Send(4, true);

var ended = await reservations.ResultAsync("r-1", TimeSpan.FromSeconds(30));
Console.WriteLine($"r-1 ended at version {ended}");

// After the workflow ended, the projection hands version 2 over again, and
// a version 5 arrives that nothing will apply.
Console.WriteLine($"version 2 again: {await Send(2, false)}");
Console.WriteLine($"version 5: {await Send(5, false)}");

await host.StopAsync();

// A stand-in for the booking system. It holds the first change until the
// program releases it, so that later changes arrive while it is applied.
public sealed class Booking : IBooking
{
    private readonly TaskCompletionSource started = new();
    private readonly TaskCompletionSource release = new();

    public Task Started => started.Task;
    public ConcurrentQueue<int> Applied { get; } = new();
    public void Release() => release.TrySetResult();

    public async Task<int> Apply(string key, ReservationChanged change)
    {
        started.TrySetResult();
        await release.Task;
        Applied.Enqueue(change.Version);
        Console.WriteLine(
            $"applied {change.Reservation} version {change.Version}");
        return change.Version;
    }
}
