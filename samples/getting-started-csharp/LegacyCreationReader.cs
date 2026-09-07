using Akka.Persistence.Journal;
using Microsoft.Extensions.Configuration;
using static FCQRS.Common;

// The first sample journaled Event<DocumentCreated>. New code handles Event<DocumentEvent>.
// Adapt the envelope on read, preserving the stored bytes, version, identity, and metadata.
public sealed class LegacyCreationReader : IEventAdapter
{
    public static object Read(object message) => message is Event<DocumentCreated> old
        ? new Event<DocumentEvent>(old.EventDetails, old.CreationDate, old.Id, old.Sender,
                                  old.CorrelationId, old.Version, old.Metadata)
        : message;

    public string Manifest(object message) => "";
    public object ToJournal(object message) => message;
    public IEventSequence FromJournal(object message, string manifest) => EventSequence.Single(Read(message));

    public static void Configure(IConfigurationBuilder configuration) =>
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["config:akka:persistence:journal:sql:event-adapters:legacy-creation"] = typeof(LegacyCreationReader).AssemblyQualifiedName,
            [$"config:akka:persistence:journal:sql:event-adapter-bindings:{typeof(Event<DocumentCreated>).AssemblyQualifiedName}"] = "legacy-creation"
        });
}
