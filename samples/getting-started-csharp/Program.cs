using System.Collections.Concurrent;
using FCQRS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static FCQRS.Common;
using static FCQRS.CSharp;

if (args is ["--check"]) { DocumentChecks.Run(); return 0; }
if (!(args is [] or ["--recover", _] or ["--edit" or "--publish" or "--pause-publication", _, _]))
{
    Console.Error.WriteLine("Usage: dotnet run [-- --check | --recover ID | --edit ID CONTENT | --publish ID SLUG | --pause-publication ID SLUG]");
    return 1;
}
var database = Environment.GetEnvironmentVariable("DOCSTORE_DATABASE")
    ?? Path.Combine(AppContext.BaseDirectory, "getting-started-csharp.db");
var documentId = args.Length > 1 ? args[1] : Guid.NewGuid().ToString("N");
var pause = args is ["--pause-publication", _, _];
var publishing = pause || args is ["--publish", _, _];
var readModel = new ConcurrentDictionary<string, Document>();
var published = new TaskCompletionSource<Event<DocumentEvent>>(TaskCreationOptions.RunContinuationsAsynchronously);
var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

void HandleProjection(long offset, object message)
{
    // Query journals can supply the old envelope too; use the same reader as recovery.
    if (LegacyCreationReader.Read(message) is not Event<DocumentEvent> stored) return;
    switch (stored.EventDetails)
    {
        case DocumentCreated created: readModel[created.Document.Id] = created.Document; break;
        case DocumentEdited edited:
            if (!readModel.TryGetValue(edited.Id, out var document))
                throw new InvalidOperationException("Projection received an edit before creation");
            readModel[edited.Id] = document with { Content = edited.Content };
            break;
        case PublicationFinished finished when publishing && finished.Id == documentId && finished.Slug == args[2]:
            published.TrySetResult(stored);
            break;
    }
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
LegacyCreationReader.Configure(builder.Configuration);
// docs:wire
builder.Services.AddFcqrs($"Data Source={database};", "getting-started-csharp")
    .AddAggregate<DocumentAggregate>()
    .AddAggregate<SlugAggregate>()
    .AddSaga<PublicationSaga, DocumentEvent, PublicationData, PublicationState>(
        sp => new PublicationSaga(sp.AggregateFactory<DocumentAggregate>(), sp.AggregateFactory<SlugAggregate>(), pause, paused),
        PublicationSaga.StartsOn)
    .AddProjection(HandleProjection, lastOffset: 0);
// docs:end
using var host = builder.Build();
await host.StartAsync();
try
{
    var documents = host.Services.GetRequiredService<Handler<DocumentCommand, DocumentEvent>>();
    var subscriptions = host.Services.GetRequiredService<FCQRS.Query.ISubscribe>();
    var aggregateId = Values.CreateAggregateId(documentId);
    var document = new Document(documentId, "FCQRS notes", "first event");
    Task<Event<DocumentEvent>> Send(DocumentCommand command) => documents(_ => true, Values.NewCID(), aggregateId, command);

    if (args.Length == 0)
    {
        var correlationId = Values.NewCID();
        using var projected = subscriptions.SubscribeForFirst(correlationId);
        var stored = await documents(_ => true, correlationId, aggregateId, new CreateDocument(document));
        await projected.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Console.WriteLine($"stored version {stored.Version}; query returned '{readModel[documentId].Content}'");
        var repeated = await Send(new CreateDocument(document with { Content = "replacement attempt" }));
        var original = ((DocumentCreated)repeated.EventDetails).Document;
        Console.WriteLine($"repeat reply version {repeated.Version}; document contains '{original.Content}'");
    }
    else if (args[0] == "--recover")
    {
        var reply = await Send(new CreateDocument(document with { Content = "replacement attempt" }));
        var original = ((DocumentCreated)reply.EventDetails).Document;
        Console.WriteLine($"recovery reply version {reply.Version}; document contains '{original.Content}'");
    }
    else if (args[0] == "--edit")
    {
        var content = args[2];
        // docs:edit-request
        var correlationId = Values.NewCID();
        using var projected = subscriptions.SubscribeForFirst(correlationId);
        var reply = await documents(_ => true, correlationId, aggregateId, new EditDocument(documentId, content));
        switch (reply.EventDetails)
        {
            case DocumentEdited edited:
                if (reply.Journaled?.Value == true)
                {
                    await projected.Task.WaitAsync(TimeSpan.FromSeconds(30));
                    Console.WriteLine($"edited version {reply.Version}; query returned '{readModel[documentId].Content}'");
                }
                else Console.WriteLine($"edit reply version {reply.Version}; document contains '{edited.Content}'");
                break;
            case DocumentRejected rejected: Console.WriteLine($"edit rejected: {rejected.Reason}"); break;
            default: throw new InvalidOperationException("Unexpected edit reply");
        }
        // docs:end
    }
    else
    {
        var slug = args[2];
        var reply = await Send(new PublishDocument(slug));
        if (reply.EventDetails is DocumentRejected rejected)
            Console.WriteLine($"publication rejected: {rejected.Reason}");
        else if (pause)
        {
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Console.WriteLine($"publication paused after the reservation; restart with --publish {documentId} {slug}");
        }
        else
        {
            // Installed before startup, this observer also sees replay and a recovered
            // saga finishing under its original correlation id.
            var stored = await published.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var finished = (PublicationFinished)stored.EventDetails;
            Console.WriteLine($"publication version {stored.Version}; {slug} -> {finished.Result}");
            Console.WriteLine($"query returned '{readModel[documentId].Content}'");
        }
    }
    Console.WriteLine($"document id: {documentId}");
    Console.WriteLine($"journal: {database}");
}
finally { await host.StopAsync(); }
return 0;
