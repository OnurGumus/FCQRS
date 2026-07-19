---
title: Use FCQRS from C#
category: Apply
categoryindex: 4
index: 12
---

# Use FCQRS from C#

C# applications use the same FCQRS model as F# applications: commands describe requests, events
record outcomes, an aggregate decides and folds, and projections build queryable views. The C# API
adds base classes, action factories, delegates, and host-builder registration around that model.

This guide uses the current host-builder API. The lower-level `ActorApi` and `ActorWiring` APIs remain
available for custom composition, but most applications do not need them.

## Compiler requirement

The examples use C# discriminated unions. At the time of writing, the `union` keyword requires a .NET
11 preview SDK and `<LangVersion>preview</LangVersion>`. FCQRS targets `net10.0` and can run in a
`net11.0` host. If preview language features are not acceptable for your project, define the domain in
an F# class library and keep the host, endpoints, and projections in C#.

FCQRS writes an explicit case discriminator for unions in the journal. Do not replace its event
serializer with a caseless union representation: two cases can have the same field shape but different
domain meaning.

## 1. Define commands, events, and state

Use commands for intent and persisted events for facts. Deferred events are replies that are published
and folded but not stored. Their fold should leave state unchanged because recovery cannot replay them.

```csharp
public record Document(string Id, string Title, string Content);

public union DocumentCommand(DocumentCommand.Create, DocumentCommand.Edit)
{
    public record Create(Document Document);
    public record Edit(string Id, string Content);
}

public union DocumentEvent(
    DocumentEvent.Created,
    DocumentEvent.Edited,
    DocumentEvent.AlreadyExists,
    DocumentEvent.NoSuchDocument)
{
    public record Created(Document Document);
    public record Edited(string Id, string Content);
    public record AlreadyExists;
    public record NoSuchDocument;
}

public record DocumentState(Document? Document = null)
{
    public static readonly DocumentState Initial = new();
}
```

## 2. Implement the aggregate

`HandleCommand` chooses an action. `ApplyEvent` reconstructs state from stored events. Keep both
functions deterministic and free of database, network, clock, and random-number calls.

```csharp
public sealed class DocumentAggregate
    : Aggregate<DocumentState, DocumentCommand, DocumentEvent>
{
    public override DocumentState InitialState => DocumentState.Initial;
    public override string EntityName => "Document";

    public override EventAction<DocumentEvent> HandleCommand(
        Command<DocumentCommand> command,
        DocumentState state) =>
        (command.CommandDetails, state.Document) switch
        {
            (DocumentCommand.Create create, null) =>
                EventActions.Persist<DocumentEvent>(
                    new DocumentEvent.Created(create.Document)),

            (DocumentCommand.Create, _) =>
                EventActions.Defer<DocumentEvent>(
                    new DocumentEvent.AlreadyExists()),

            (DocumentCommand.Edit edit, { } document)
                when document.Id == edit.Id =>
                EventActions.Persist<DocumentEvent>(
                    new DocumentEvent.Edited(edit.Id, edit.Content)),

            _ => EventActions.Defer<DocumentEvent>(
                new DocumentEvent.NoSuchDocument())
        };

    public override DocumentState ApplyEvent(
        Event<DocumentEvent> stored,
        DocumentState state) =>
        stored.EventDetails switch
        {
            DocumentEvent.Created created =>
                state with { Document = created.Document },

            DocumentEvent.Edited edited
                when state.Document is { } document && document.Id == edited.Id =>
                state with
                {
                    Document = document with { Content = edited.Content }
                },

            _ => state
        };
}
```

`EntityName` is part of persistent identity. Treat it as stable after deployment. See
[Evolve persisted events](evolve-events.html) before renaming domain types or cases.

## 3. Register the runtime

The host starts aggregates first, then sagas, the saga starter, and finally the projection. Register
one projection handler per FCQRS runtime; that handler may update several read-model tables.

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddFcqrs("Data Source=documents.db;", "documents")
    .AddAggregate<DocumentAggregate>()
    .AddProjection(
        (long offset, object message) => projection.Handle(offset, message),
        lastOffset: projection.LastCommittedOffset);

var app = builder.Build();
await app.RunAsync();
```

For a durable projection, commit its read-model changes and `offset` in the same transaction. On the
next start, pass the committed offset instead of zero. See [Add a projection](add-a-projection.html).

## 4. Send from an endpoint or application service

Registration adds a typed `Handler<DocumentCommand, DocumentEvent>` to dependency injection. The
handler waits for the matching aggregate reply. It does not by itself wait for a projection. If no
matching reply arrives within `akka.fcqrs.command-timeout` (default 30s) — for example because the
aggregate decided `UnhandledEvent` or the filter never matches — the handler raises
`TimeoutException` instead of waiting forever.

Handlers are keyed by command/event type pair. Two aggregates sharing the same `TCommand`/`TEvent`
pair make the plain registration ambiguous; resolving it then throws with guidance. Resolve the
aggregate you mean through the keyed registration instead, e.g.
`services.GetKeyedService<Handler<C, E>>(typeof(MyShard))` or `[FromKeyedServices(typeof(MyShard))]`.

```csharp
public sealed class DocumentService(
    Handler<DocumentCommand, DocumentEvent> documents,
    ISubscribe subscriptions)
{
    public async Task<Event<DocumentEvent>> Create(
        Document document,
        CancellationToken cancellationToken)
    {
        var cid = Helpers.NewCID();
        var aggregateId = Helpers.CreateAggregateId(document.Id);

        // Subscribe before sending so the projection cannot win the race.
        using var projected = subscriptions.SubscribeForFirst(cid);

        var reply = await documents(
            e => e is DocumentEvent.Created or DocumentEvent.AlreadyExists,
            cid,
            aggregateId,
            new DocumentCommand.Create(document));

        // Deferred replies are not journaled and cannot reach a projection.
        if (reply.Journaled is not { Value: false })
            await projected.Task.WaitAsync(cancellationToken);

        return reply;
    }
}
```

After `projected.Task` completes, query the read model maintained by this subscription. Use a bounded
cancellation policy because projection subscriptions are in-memory request coordination, not a durable
queue. The complete ordering and notification rules are in [Read your writes](read-your-writes.html).

## 5. Test without starting the host

Construct the aggregate and call its two methods directly:

```csharp
var aggregate = new DocumentAggregate();
var document = new Document("doc-1", "FCQRS", "draft");

var command = TestEnvelope.Command(
    new DocumentCommand.Create(document),
    TimeProvider.System);

var action = aggregate.HandleCommand(command, DocumentState.Initial);
Assert.Equal(
    EventActions.Persist<DocumentEvent>(new DocumentEvent.Created(document)),
    action);

var stored = TestEnvelope.Event(
    new DocumentEvent.Created(document),
    version: 1,
    TimeProvider.System);

var state = aggregate.ApplyEvent(stored, DocumentState.Initial);
Assert.Equal(document, state.Document);
```

Use a `FakeTimeProvider` when a test creates time-dependent envelopes. Add replay fixtures for old
events before changing their serialized shape. See [Test your domain](test-your-domain.html).

## Where to continue

- [Define an aggregate](define-an-aggregate.html) explains all aggregate actions and snapshot choices.
- [Write a saga](write-a-saga.html) shows cross-aggregate coordination and C# registration.
- [C# interop and serialization](../concepts/csharp-interop.html) explains union representation and
  compatibility.
