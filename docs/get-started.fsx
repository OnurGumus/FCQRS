(**
---
title: 0. Quickstart
category: Learn FCQRS
categoryindex: 2
index: 2
---
*)

(*** hide ***)
#r "nuget: FCQRS, 6.0.0"

(**
# 0. Quickstart: follow one request end to end

The application in this quickstart is DocStore, a small document store. It keeps titled text
documents: a caller can create a document, edit its content, and look a document up by its id. This
page builds the create-and-read slice of DocStore in a single file: a command creates a document, the
aggregate stores a `Created` event, a projection updates the read model that serves lookups, and the
program queries that model after receiving the projection's confirmation.

> **Course position:** this is the first practical stage. It assumes only basic F# or C# and introduces
> the runtime vocabulary used by chapters 1 through 5.

> **Motivation:** Build one complete vertical slice first. An aggregate alone hides the asynchronous
> handoff to projections, while a full command-to-query loop exposes the architecture you will use in
> a real application.

The read model is kept in memory so the example needs only the `FCQRS` package. It is rebuilt by
replaying the journal from offset zero whenever the program starts. Later stages explain why this works
and replace the teaching shortcuts with production decisions.

## Run a complete sample

The repository contains two small projects that perform the complete flow on stable .NET 10:

| Language | Project |
|---|---|
| F# | [`samples/getting-started-fsharp`](https://github.com/OnurGumus/FCQRS/tree/main/samples/getting-started-fsharp) |
| C# | [`samples/getting-started-csharp`](https://github.com/OnurGumus/FCQRS/tree/main/samples/getting-started-csharp) |

After cloning FCQRS, run either project from the repository root:

```text
dotnet run --project samples/getting-started-fsharp
dotnet run --project samples/getting-started-csharp
```

Both send one command, persist one event to SQLite, wait for an in-memory projection, and query the
result. The C# sample begins with one concrete command and event type, so it does not require preview
union syntax. The C# examples later on this page show how the domain expands to several cases.

## Create the project

```text
dotnet new console -lang F# -n DocStore
cd DocStore
dotnet add package FCQRS
```

<div class="cs-alt" data-fs="text" data-cs="text"></div>

```text
dotnet new console -n DocStore
cd DocStore
dotnet add package FCQRS
```

Replace `Program.fs` (`Program.cs` in C#) with the code from the following sections. The C# tabs are direct counterparts;
because the expanded command and event examples use C# discriminated unions, they require the compiler
setup described in [C# interop and serialization](concepts/csharp-interop.html). Use the linked stable
.NET 10 C# sample when you want a runnable project without preview union syntax.
*)

(*** hide ***)
open System
open System.Collections.Concurrent
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Common
open FCQRS.Model.Data
open FCQRS.FSharp

(**
## 1. Define the aggregate

An aggregate receives commands and produces events. Its current state is rebuilt by folding its stored
events. This document aggregate accepts `Create` and `Edit` commands. A command that cannot change the
document produces a deferred reply instead of a stored event.
*)

module Document =
    type Root =
        { Id: string
          Title: string
          Content: string }

    type State = { Document: Root option }

    type Command =
        | Create of Root
        | Edit of id: string * content: string

    type Event =
        | Created of Root
        | Edited of id: string * content: string
        | AlreadyExists
        | NoSuchDocument

    let initial = { Document = None }

    // Decide: a pure function from command and current state to one event action.
    let decide (command: Command<Command>) state =
        match command.CommandDetails, state with
        | Create document, { Document = None } -> Created document |> PersistEvent // stored and published
        | Create _, { Document = Some _ } -> AlreadyExists |> DeferEvent // reply only, nothing stored
        | Edit(id, content), { Document = Some document } when document.Id = id ->
            Edited(id, content) |> PersistEvent
        | Edit _, _ -> NoSuchDocument |> DeferEvent

    // Fold: rebuilds state one event at a time; it also runs during replay after a restart.
    let fold (event: Event<Event>) state =
        match event.EventDetails with
        | Created document -> { Document = Some document }
        | Edited(id, content) ->
            match state.Document with
            | Some document when document.Id = id ->
                { Document = Some { document with Content = content } }
            | _ -> state
        | AlreadyExists
        | NoSuchDocument -> state // deferred replies never change persisted state

(**
`PersistEvent` appends the event to the journal, folds it into state, and publishes it. `DeferEvent`
publishes and folds a reply without storing it or changing the persisted aggregate version. The
deferred `AlreadyExists` and `NoSuchDocument` cases above deliberately leave state unchanged in
`fold`; otherwise their state change would disappear on recovery. The decision and fold are pure
functions, so they can be tested without Akka.NET or a database.

<div class="cs-alt"></div>

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using FCQRS;
using static FCQRS.Common;
using static FCQRS.CSharp;

public record Document(string Id, string Title, string Content);

public union DocumentCommand(DocumentCommand.Create, DocumentCommand.Edit)
{
    public record Create(Document Document);
    public record Edit(string Id, string Content);
}

public union DocumentEvent(
    DocumentEvent.Created, DocumentEvent.Edited,
    DocumentEvent.AlreadyExists, DocumentEvent.NoSuchDocument)
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

public sealed class DocumentAggregate
    : Aggregate<DocumentState, DocumentCommand, DocumentEvent>
{
    public override DocumentState InitialState => DocumentState.Initial;
    public override string EntityName => "Document";

    // Decide: a pure function from command and current state to one event action.
    public override EventAction<DocumentEvent> HandleCommand(
        Command<DocumentCommand> command, DocumentState state) =>
        (command.CommandDetails, state.Document) switch
        {
            (DocumentCommand.Create create, null) =>
                // Stored and published.
                EventActions.Persist<DocumentEvent>(new DocumentEvent.Created(create.Document)),
            (DocumentCommand.Create, _) =>
                // Reply only, nothing stored.
                EventActions.Defer<DocumentEvent>(new DocumentEvent.AlreadyExists()),
            (DocumentCommand.Edit edit, { } document) when document.Id == edit.Id =>
                EventActions.Persist<DocumentEvent>(new DocumentEvent.Edited(edit.Id, edit.Content)),
            _ => EventActions.Defer<DocumentEvent>(new DocumentEvent.NoSuchDocument())
        };

    // Fold: rebuilds state one event at a time; it also runs during replay after a restart.
    public override DocumentState ApplyEvent(
        Event<DocumentEvent> eventEnvelope, DocumentState state) =>
        eventEnvelope.EventDetails switch
        {
            DocumentEvent.Created created => state with { Document = created.Document },
            DocumentEvent.Edited edited when state.Document is { } document && document.Id == edited.Id =>
                state with { Document = document with { Content = edited.Content } },
            // Deferred replies never change persisted state.
            _ => state
        };
}
```

See [Aggregates and the write side](concepts/aggregates.html) for the model behind these functions.

## 2. Build a read model

The query side below is a concurrent dictionary indexed by document id. The projection receives each
stored event in order and updates the dictionary. Throwing on `Edited` without a preceding `Created`
makes a broken event history visible instead of silently producing an incorrect view.
*)

let readModel = ConcurrentDictionary<string, Document.Root>()

let handleProjection (_offset: int64) (message: obj) =
    match message with
    | :? Event<Document.Event> as event ->
        match event.EventDetails with
        | Document.Created document -> readModel[document.Id] <- document
        | Document.Edited(id, content) ->
            match readModel.TryGetValue id with
            | true, document -> readModel[id] <- { document with Content = content }
            | false, _ -> failwith $"Projection received Edited before Created for {id}"
        | Document.AlreadyExists
        | Document.NoSuchDocument -> () // deferred replies carry nothing to project
    | _ -> ()

(**
<div class="cs-alt"></div>

```csharp
var readModel = new ConcurrentDictionary<string, Document>();

void HandleProjection(long _offset, object message)
{
    if (message is not Event<DocumentEvent> eventEnvelope)
        return;

    switch (eventEnvelope.EventDetails)
    {
        case DocumentEvent.Created created:
            readModel[created.Document.Id] = created.Document;
            break;
        case DocumentEvent.Edited edited
            when readModel.TryGetValue(edited.Id, out var document):
            readModel[edited.Id] = document with { Content = edited.Content };
            break;
        case DocumentEvent.Edited edited:
            throw new InvalidOperationException(
                $"Projection received Edited before Created for {edited.Id}");
        // AlreadyExists and NoSuchDocument fall through: deferred replies carry nothing to project.
    }
}
```

This projection starts at offset zero, so it rebuilds the dictionary from all stored events on every
run. A durable read model stores its last offset with each update and resumes from there.

## 3. Create the actor system

`Fcqrs.actor` combines the embedded Akka.NET defaults with the supplied configuration and database
connection. SQLite stores the journal and snapshots in `getstarted.db`.
*)

let buildApi () : IActor =
    let config = ConfigurationBuilder().Build()
    let loggerFactory = LoggerFactory.Create(fun _ -> ())
    let connection =
        Fcqrs.connect FCQRS.Actor.DBType.Sqlite "Data Source=getstarted.db;"

    Fcqrs.actor config loggerFactory (Some connection) "getstarted"

(**
<div class="cs-alt"></div>

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddFcqrs("Data Source=getstarted.db;", "getstarted")
    .AddAggregate<DocumentAggregate>()
    .AddProjection(HandleProjection, lastOffset: 0);
```

## 4. Send, wait, and query

The aggregate and projection are registered before commands are sent. Subscribe to the correlation id
before sending, then wait for the projection after the aggregate returns its stored event. The new
aggregate id in this example guarantees that `Create` stores an event rather than returning a deferred
`AlreadyExists` reply.
*)

let run () =
    async {
        let api = buildApi ()

        let documents =
            Fcqrs.aggregate api
                { Name = "Document"
                  Initial = Document.initial
                  Decide = Document.decide
                  Fold = Document.fold
                  Snapshots = Default }

        Fcqrs.wireSagaStarters api []
        let subscriptions = Fcqrs.projection api (Projection.single 0 handleProjection)

        let documentId = Guid.NewGuid().ToString("N")
        let aggregateId = Fcqrs.aggregateId documentId
        let correlationId = Fcqrs.newCid ()
        let document: Document.Root =
            { Id = documentId
              Title = "FCQRS notes"
              Content = "first event" }

        // Subscribe before sending so the projection cannot publish first.
        use projectedEvent = subscriptions.Subscribe(correlationId, 1)

        let! stored =
            documents.Send
                correlationId
                aggregateId
                (Document.Create document)
                // The filter picks which aggregate reply completes this send.
                (function
                    | Document.Created _ -> true
                    | _ -> false)

        // Read-your-writes: wait until the projection has handled this correlation id.
        do! projectedEvent.Task |> Async.AwaitTask
        let projected = readModel[documentId]
        printfn "stored version %A; query returned '%s'" stored.Version projected.Content
    }

(**
<div class="cs-alt"></div>

```csharp
using var host = builder.Build();
await host.StartAsync();

var documents = host.Services
    .GetRequiredService<Handler<DocumentCommand, DocumentEvent>>();
var subscriptions = host.Services
    .GetRequiredService<FCQRS.Query.ISubscribe>();

var documentId = Guid.NewGuid().ToString("N");
var aggregateId = Values.CreateAggregateId(documentId);
var correlationId = Values.NewCID();
var document = new Document(documentId, "FCQRS notes", "first event");

// Subscribe before sending so the projection cannot publish first.
using var projectedEvent = subscriptions.SubscribeForFirst(correlationId);

var stored = await documents(
    // The filter picks which aggregate reply completes this send.
    outcome => outcome is DocumentEvent.Created,
    correlationId,
    aggregateId,
    new DocumentCommand.Create(document));

// Read-your-writes: wait until the projection has handled this correlation id.
await projectedEvent.Task;
var projected = readModel[documentId];
Console.WriteLine(
    $"stored version {stored.Version}; query returned '{projected.Content}'");

await host.StopAsync();
```

Add the entry point and run the program:

```fsharp
[<EntryPoint>]
let main _ =
    run () |> Async.RunSynchronously
    0
```

<div class="cs-alt"></div>

```csharp
// Nothing to add: the top-level statements above are the entry point,
// and await host.StartAsync() already runs the program.
```

```text
dotnet run
stored version 1; query returned 'first event'
```

The aggregate id is new on each run, so each command creates a different document. The projection still
replays previous documents before handling the new event.

## Continue the learning path

You have seen the entire route once: command, aggregate decision, stored event, projection, then query.
The next chapter slows down at the first step and explains how to design that decision correctly.

Continue to [1. The aggregate](tutorial/1-the-aggregate.html).

After chapter 1 introduces the domain model, its optional deep dives point to the relevant Understand
and Apply pages. You do not need those pages before continuing.
*)
