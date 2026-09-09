using System.Collections.Concurrent;
using static FCQRS.Common;

// Each account has one immutable registration, so one signal per account is enough.
public sealed class UserView
{
    private readonly ConcurrentDictionary<string, string> names = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> ready = new();

    private TaskCompletionSource Ready(string id) => ready.GetOrAdd(id,
        _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

    // docs:projection
    public void Project(long offset, object message)
    {
        if (message is Event<UserRegistered> stored && stored.Sender is { } sender)
        {
            var id = sender.Value.ToString();
            names[id] = stored.EventDetails.Name;
            Ready(id).TrySetResult();
        }
    }
    // docs:end

    public bool TryGet(string id, out string? name) => names.TryGetValue(id, out name);

    // Also works for a repeated POST: replay or an earlier write may have signaled already.
    public Task WaitFor(string id, CancellationToken cancellationToken) =>
        Ready(id).Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
}
