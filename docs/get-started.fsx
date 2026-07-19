(**
---
title: Get started
category: Get started
categoryindex: 2
index: 1
---
*)

(*** hide ***)
#r "nuget: FCQRS, 6.0.0-rc1"

(**
# Get started

This page builds one complete FCQRS path in a single file. A command creates a document, the aggregate
stores an event, a projection updates a read model, and the program queries that model after receiving
the projection's confirmation.

The read model is kept in memory so the example needs only the `FCQRS` package. It is rebuilt by
replaying the journal from offset zero whenever the program starts. A production projection stores its
data and offset transactionally; [Add a projection](how-to/add-a-projection.html) shows that version.

Read [Concepts](concepts/index.html) first if CQRS and event sourcing are new to you. Follow the
[Tutorial](tutorial/index.html) for the complete course from domain modelling through production.

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
dotnet new console -lang F# -n MyApp
cd MyApp
dotnet add package FCQRS --prerelease
```

Replace `Program.fs` with the code from the following sections.
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

    let decide (command: Command<Command>) state =
        match command.CommandDetails, state with
        | Create document, { Document = None } -> Created document |> PersistEvent
        | Create _, { Document = Some _ } -> AlreadyExists |> DeferEvent
        | Edit(id, content), { Document = Some document } when document.Id = id ->
            Edited(id, content) |> PersistEvent
        | Edit _, _ -> NoSuchDocument |> DeferEvent

    let fold (event: Event<Event>) state =
        match event.EventDetails with
        | Created document -> { Document = Some document }
        | Edited(id, content) ->
            match state.Document with
            | Some document when document.Id = id ->
                { Document = Some { document with Content = content } }
            | _ -> state
        | AlreadyExists
        | NoSuchDocument -> state

(**
`PersistEvent` appends the event to the journal, folds it into state, and publishes it. `DeferEvent`
publishes and folds a reply without storing it or changing the persisted aggregate version. The
deferred `AlreadyExists` and `NoSuchDocument` cases above deliberately leave state unchanged in
`fold`; otherwise their state change would disappear on recovery. The decision and fold are pure
functions, so they can be tested without Akka.NET or a database.

<div class="cs-alt"></div>

```csharp
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

    public override EventAction<DocumentEvent> HandleCommand(
        Command<DocumentCommand> command, DocumentState state) =>
        (command.CommandDetails, state.Document) switch
        {
            (DocumentCommand.Create create, null) =>
                EventActions.Persist<DocumentEvent>(new DocumentEvent.Created(create.Document)),
            (DocumentCommand.Create, _) =>
                EventActions.Defer<DocumentEvent>(new DocumentEvent.AlreadyExists()),
            (DocumentCommand.Edit edit, { } document) when document.Id == edit.Id =>
                EventActions.Persist<DocumentEvent>(new DocumentEvent.Edited(edit.Id, edit.Content)),
            _ => EventActions.Defer<DocumentEvent>(new DocumentEvent.NoSuchDocument())
        };

    public override DocumentState ApplyEvent(
        Event<DocumentEvent> eventEnvelope, DocumentState state) =>
        eventEnvelope.EventDetails switch
        {
            DocumentEvent.Created created => state with { Document = created.Document },
            DocumentEvent.Edited edited when state.Document is { } document && document.Id == edited.Id =>
                state with { Document = document with { Content = edited.Content } },
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
        | Document.NoSuchDocument -> ()
    | _ -> ()

(**
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
var readModel = new ConcurrentDictionary<string, Document>();

void HandleProjection(long offset, object message)
{
    if (message is not Event<DocumentEvent> eventEnvelope) return;

    switch (eventEnvelope.EventDetails)
    {
        case DocumentEvent.Created created:
            readModel[created.Document.Id] = created.Document;
            break;
        case DocumentEvent.Edited edited when readModel.TryGetValue(edited.Id, out var document):
            readModel[edited.Id] = document with { Content = edited.Content };
            break;
    }
}

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

        use projectedEvent = subscriptions.Subscribe(correlationId, 1)

        let! stored =
            documents.Send
                correlationId
                aggregateId
                (Document.Create document)
                (function
                    | Document.Created _ -> true
                    | _ -> false)

        do! projectedEvent.Task |> Async.AwaitTask
        let projected = readModel[documentId]
        printfn "stored version %A; query returned '%s'" stored.Version projected.Content
    }

(**
Add the entry point and run the program:

```fsharp
[<EntryPoint>]
let main _ =
    run () |> Async.RunSynchronously
    0
```

```text
dotnet run
stored version 1; query returned 'first event'
```

The aggregate id is new on each run, so each command creates a different document. The projection still
replays previous documents before handling the new event.

## What to learn next

- Follow the [Tutorial](tutorial/index.html) to build the model one decision at a time and continue
  through sagas, testing, event evolution, and production.
- Read [Concepts](concepts/index.html) for the reasoning behind each part.
- Use the [How-to guides](how-to/index.html) while implementing a specific task.
- See [Use FCQRS from C#](how-to/use-from-csharp.html) for the complete C# host setup.
*)
