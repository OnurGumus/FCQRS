(**
---
title: 2. Wiring and running it
category: Learn FCQRS
categoryindex: 2
index: 4
---
*)

(*** hide ***)
#r "nuget: FCQRS, 6.0.0"
open System
open System.Collections.Concurrent
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Common
open FCQRS.Model.Data
open FCQRS.FSharp

module Values =
    type DocumentId =
        | DocumentId of Guid
        static member OfGuid g = DocumentId g
        member this.Value = let (DocumentId g) = this in g
        override this.ToString() = let (DocumentId g) = this in g.ToString()
    type Title =
        | Title of ShortString
        static member TryCreate s =
            match ValueLens.TryCreate s with
            | Ok ss -> Ok(Title ss)
            | Error _ -> Error "Invalid title"
        member this.Value = let (Title s) = this in ValueLens.Value s
    type Content =
        | Content of LongString
        static member TryCreate s =
            match ValueLens.TryCreate s with
            | Ok ss -> Ok(Content ss)
            | Error _ -> Error "Invalid content"
        member this.Value = let (Content s) = this in ValueLens.Value s

module Document =
    open Values
    type Root =
        { Id: DocumentId; Title: Title; Content: Content }
        static member TryCreate(guid, title, content) =
            match Title.TryCreate title, Content.TryCreate content with
            | Ok t, Ok c -> Ok { Id = DocumentId.OfGuid guid; Title = t; Content = c }
            | Error e, _ -> Error e
            | _, Error e -> Error e
    type State = { Document: Root option }
    let initial = { Document = None }
    type Command = CreateOrUpdate of Root
    type Event = Updated of Root
    let decide (cmd: Command<Command>) state =
        match cmd.CommandDetails with
        | CreateOrUpdate doc -> Updated doc |> PersistEvent
    let fold (event: Event<Event>) state =
        match event.EventDetails with
        | Updated doc -> { Document = Some doc }

(**
# 2. Wiring and running it

Chapter 1 produced a document aggregate as pure functions. This chapter registers those functions with
FCQRS, stores their events in SQLite, projects the events into query data, and sends a command through
the complete path. Continue in the same `Program.fs`.

> **Course position:** chapter 1 defined a pure aggregate. This chapter runs it and follows one stored
> event into a query model. By the end you will understand the journal, projection offset,
> correlation id, and subscribe-before-send ordering from one working request.

## Two paths, on purpose: writing and reading

The application has two paths:

<pre>
                decide / fold
  command  ---------------------->  JOURNAL   (append-only events; the truth)
                                       |
                                       v
                                   projection
                                       |
                                       v
                                  read model   (a derived view; disposable)
</pre>

Commands flow through the aggregate and append events to the journal. Projections consume those events
and update read models. Application queries use the read model, not the aggregate state or journal.

> **Motivation:** Two paths let query shapes evolve without widening the aggregate, and let business
> rules evolve without redesigning every query. The stored events are the stable handoff between them.

## Create the actor system

`Fcqrs.actor` builds the Akka.NET system. The connection configures the journal, query journal, and
snapshot store. The embedded defaults configure serialization, sharding, and a one-node cluster.
*)

let buildApi () : IActor =
    let config = ConfigurationBuilder().Build()
    let loggerFactory = LoggerFactory.Create(fun _ -> ())
    let connection =
        Fcqrs.connect FCQRS.Actor.DBType.Sqlite "Data Source=tutorial.db;"
    Fcqrs.actor config loggerFactory (Some connection) "tutorial"

(**
<div class="cs-alt"></div>

```csharp
// C#: the DI host-builder creates the actor system and registers the aggregate;
// config and logger come from the container, so there's no explicit buildApi.
var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddFcqrs("Data Source=tutorial.db;", "tutorial")
    .AddAggregate<DocumentAggregate, DocumentState, DocumentCommand, DocumentEvent>();
```
*)

(**
The empty `IConfiguration` accepts every embedded default. User configuration is merged over those
defaults when an application needs different Akka.NET settings. `tutorial.db` outlives the process, so
the next run can recover the aggregate and replay the projection. See
[Configure the database](../how-to/configure-the-database.html) for other providers.

## Build the query side

This tutorial uses an in-memory dictionary for query data. The projection starts at offset zero and
replays all stored `Updated` events on every run. A production projection persists its read model and
offset in one transaction; the in-memory version keeps the event-to-query transformation visible.
*)

let readModel = ConcurrentDictionary<string, Document.Root>()

let handle (_offset: int64) (message: obj) =
    match message with
    | :? Event<Document.Event> as event ->
        match event.EventDetails with
        | Document.Updated document -> readModel[document.Id.ToString()] <- document
    | _ -> ()

(**
<div class="cs-alt"></div>

```csharp
// C#: the same in-memory read model and projection.
var readModel = new ConcurrentDictionary<string, Document>();

void Handle(long offset, object message)
{
    if (message is Event<DocumentEvent> { EventDetails: DocumentEvent.Updated updated })
        readModel[updated.Document.Id.ToString()] = updated.Document;
}

builder.Services.AddProjection((offset, ev) => Handle(offset, ev));
```
*)

(**
`Projection.single` publishes an aggregate event to subscribers after `handle` returns. A caller can
therefore wait until this projection has applied a specific command's event. See
[Add a projection](../how-to/add-a-projection.html) for transactional offset storage.

## Send a command and read your own write

The aggregate acknowledges its stored event before the projection necessarily updates the read model.
A query sent immediately after the command can therefore return the previous view. FCQRS carries a
**correlation id (CID)** through the command, event, and projection notification:

<pre>
  mint a CID
      |
      v
  SUBSCRIBE to that CID    &lt;-- before sending, so the answer can't slip past
      |
      v
  send command  -->  aggregate  -->  journal  -->  projection re-publishes  -->  your wait wakes
</pre>

Subscribe before sending. Subscribing afterward creates a race in which the projection can publish the
notification before the subscription exists. `.Send` accepts the CID, aggregate id, command, and a
predicate selecting the aggregate reply.
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

        let subs = Fcqrs.projection api (Projection.single 0 handle)

        let cid = Fcqrs.newCid ()
        let id = Fcqrs.aggregateId "11111111-1111-1111-1111-111111111111"

        // Subscribe to this CID *before* sending so the confirmation can't be missed.
        use awaiter = subs.Subscribe(cid, 1)

        match Document.Root.TryCreate(Guid "11111111-1111-1111-1111-111111111111", "Welcome", "draft") with
        | Error e -> printfn "rejected: %s" e
        | Ok doc ->
            let! event =
                documents.Send cid id (Document.CreateOrUpdate doc)
                    (fun e ->
                        match e with
                        | Document.Updated _ -> true)

            do! awaiter.Task |> Async.AwaitTask
            let projected = readModel[doc.Id.ToString()]
            printfn "saved version %A; query returned '%s'" event.Version projected.Title.Value
    }

(**
<div class="cs-alt"></div>

```csharp
// C#: resolve the command handler + subscription from DI, then the same
// subscribe-before-send, read-your-writes flow.
using var app = builder.Build();
await app.StartAsync();
var documents = app.Services.GetRequiredService<Handler<DocumentCommand, DocumentEvent>>();
var subs = app.Services.GetRequiredService<ISubscribe>();

var cid = Values.NewCID();
var id = Values.CreateAggregateId("11111111-1111-1111-1111-111111111111");

using var awaiter = subs.SubscribeForFirst(cid);   // subscribe BEFORE sending
if (Document.TryCreate(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Welcome", "draft", out var doc, out var err))
{
    var ev = await documents(
        e => e is DocumentEvent.Updated,
        cid, id, new DocumentCommand.CreateOrUpdate(doc));
    await awaiter.Task;
    Console.WriteLine($"saved version {ev.Version}; query returned '{readModel[doc.Id.ToString()].Title}'");
}

await app.StopAsync();
```
*)

(**
Wire it to your entry point:

```fsharp
[<EntryPoint>]
let main _ =
    run () |> Async.RunSynchronously
    0
```

The C# version uses top-level statements; the `StartAsync` and `StopAsync` calls shown above form its
entry-point lifecycle.

## Run it, then run it again

Run the program twice without deleting `tutorial.db`:

```bash
dotnet run
# saved version 1; query returned 'Welcome'

dotnet run
# saved version 2; query returned 'Welcome'
```

The second process starts with empty memory. FCQRS replays the first `Updated` event through `fold`, then
handles the new command and stores version 2. The projection also starts with an empty dictionary and
replays from offset zero before applying the new event.

Delete `tutorial.db*` and run again to start a new event history at version 1. In production, deleting a
read model and resetting its offset is a rebuild operation; deleting the journal discards the source of
truth.

## What you now understand

A command produced a stored event. The aggregate recovered its state by replaying stored events, and a
separate projection rebuilt query data from the same history. Subscribing to the CID before sending
coordinated the query with that projection.

## Common mistakes

- **Querying without waiting for the required projection.** The read model may still contain the old
  view.
- **Subscribing after sending.** The notification can pass before the subscription exists.
- **Starting a durable projection at offset zero on every run.** Persist the offset with the read-model
  update instead.
- **Forgetting `Fcqrs.wireSagaStarters`.** Wire an empty list when the application has no sagas.

## Continue the learning path

Next, add a [saga](3-adding-a-saga.html) for a publication rule that spans a document and its URL slug.
The next chapter assumes you understand the two paths and the projection timing shown here.

For optional depth after chapter 3, [The read side](../concepts/read-models.html) explains transactional
offsets and rebuilds, while [Add a projection](../how-to/add-a-projection.html) is the short production
recipe.
*)
