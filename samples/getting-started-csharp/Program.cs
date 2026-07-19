using System.Collections.Concurrent;
using FCQRS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static FCQRS.Common;
using static FCQRS.CSharp;

var readModel = new ConcurrentDictionary<string, Document>();
var database = Path.Combine(AppContext.BaseDirectory, "getting-started-csharp.db");

void HandleProjection(long offset, object message)
{
    if (message is Event<DocumentCreated> stored)
        readModel[stored.EventDetails.Document.Id] = stored.EventDetails.Document;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();

builder.Services
    .AddFcqrs($"Data Source={database};", "getting-started-csharp")
    .AddAggregate<DocumentAggregate>()
    .AddProjection(HandleProjection, lastOffset: 0);

using var host = builder.Build();
await host.StartAsync();

var documents = host.Services.GetRequiredService<Handler<CreateDocument, DocumentCreated>>();
var subscriptions = host.Services.GetRequiredService<FCQRS.Query.ISubscribe>();

var documentId = Guid.NewGuid().ToString("N");
var document = new Document(documentId, "FCQRS notes", "created from C#");
var correlationId = Values.NewCID();
var aggregateId = Values.CreateAggregateId(documentId);

// Subscribe before sending so the projection cannot publish first.
using var projected = subscriptions.SubscribeForFirst(correlationId);

var stored = await documents(
    _ => true,
    correlationId,
    aggregateId,
    new CreateDocument(document));

await projected.Task;

var queried = readModel[documentId];
Console.WriteLine($"stored version {stored.Version}; query returned '{queried.Content}'");
Console.WriteLine($"journal: {database}");

await host.StopAsync();

public sealed record Document(string Id, string Title, string Content);
public sealed record CreateDocument(Document Document);
public sealed record DocumentCreated(Document Document);
public sealed record DocumentState(Document? Document = null)
{
    public static readonly DocumentState Initial = new();
}

public sealed class DocumentAggregate
    : Aggregate<DocumentState, CreateDocument, DocumentCreated>
{
    public override DocumentState InitialState => DocumentState.Initial;
    public override string EntityName => "GettingStartedCSharpDocument";

    public override EventAction<DocumentCreated> HandleCommand(
        Command<CreateDocument> command,
        DocumentState state) =>
        state.Document is null
            ? EventActions.Persist(new DocumentCreated(command.CommandDetails.Document))
            : EventActions.Ignore<DocumentCreated>();

    public override DocumentState ApplyEvent(
        Event<DocumentCreated> stored,
        DocumentState state) =>
        state with { Document = stored.EventDetails.Document };
}
