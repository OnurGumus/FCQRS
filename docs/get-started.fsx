(**
---
title: Get started
category: Get started
categoryindex: 2
index: 1
---
*)

(*** hide ***)
#r "nuget: FCQRS, 6.0.0-preview14"

(**
# Get started

This page builds a complete FCQRS write-and-read loop — a `Document` you can create and edit — in a
single file, using only the **`FCQRS`** package and its idiomatic-F# facade, `FCQRS.FSharp`. No HOCON
file, no configuration ceremony: the framework ships with sensible Akka.NET defaults, and you tell it
just one thing — which database to use.

Want the *why* behind each piece first? Read [Concepts](concepts/index.html). Want to build it up
gradually, all the way to a quota saga, with explanation at each step? Follow the
[Tutorial](tutorial/index.html). This page is the five-minute version; the full worked application is
[`focument_fsharp`](https://github.com/OnurGumus/focument_fsharp).

Every code block below is compiled against the pinned `FCQRS` package as part of building this site,
so it cannot quietly drift out of date.

## Install

```
dotnet new console -lang F# -n MyApp
cd MyApp
dotnet add package FCQRS --prerelease
```
*)

(*** hide ***)
open System
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Common
open FCQRS.Model.Data
open FCQRS.FSharp

(**
## 1. The aggregate

An aggregate takes **commands** and emits **events**; its state is folded from those events and is
never stored directly. The whole `Document` is two types, a piece of state, and two pure functions —
`decide` (the `handleCommand`) and `fold` (the `applyEvent`):
*)

module Document =
    type State = { Title: string option; Content: string option; Version: int64 }

    type Command =
        | Create of title: string * content: string
        | Edit of content: string

    type Event =
        | Created of string * string
        | Edited of string
        | AlreadyExists
        | NoSuchDocument

    let initial = { Title = None; Content = None; Version = 0L }

    /// decide: command + current state -> what happened
    let decide (cmd: Command<Command>) state =
        match cmd.CommandDetails, state with
        | Create(t, c), { Title = None } -> Created(t, c) |> PersistEvent
        | Create _, { Title = Some _ } -> AlreadyExists |> DeferEvent
        | Edit c, { Title = Some _ } -> Edited c |> PersistEvent
        | Edit _, { Title = None } -> NoSuchDocument |> DeferEvent

    /// fold: apply one event to the state
    let fold (event: Event<Event>) state =
        match event.EventDetails with
        | Created(t, c) -> { state with Title = Some t; Content = Some c; Version = state.Version + 1L }
        | Edited c -> { state with Content = Some c; Version = state.Version + 1L }
        | AlreadyExists
        | NoSuchDocument -> state

(**
<div class="cs-alt"></div>

```csharp
// The same aggregate in C#: commands/events are C# 15 unions, state is a record,
// and the two functions are switch expressions on an Aggregate<> subclass.
using static FCQRS.Common;   // Command<>, Event<>, EventAction<>
using static FCQRS.CSharp;    // Aggregate<>, EventActions

public union DocumentCommand(DocumentCommand.Create, DocumentCommand.Edit)
{
    public record Create(string Title, string Content);
    public record Edit(string Content);
}

public union DocumentEvent(
    DocumentEvent.Created, DocumentEvent.Edited,
    DocumentEvent.AlreadyExists, DocumentEvent.NoSuchDocument)
{
    public record Created(string Title, string Content);
    public record Edited(string Content);
    public record AlreadyExists;
    public record NoSuchDocument;
}

public record DocumentState(string? Title = null, string? Content = null, long Version = 0)
{
    public static readonly DocumentState Initial = new();
}

public sealed class DocumentAggregate : Aggregate<DocumentState, DocumentCommand, DocumentEvent>
{
    public override DocumentState InitialState => DocumentState.Initial;
    public override string EntityName => "Document";

    // decide
    public override EventAction<DocumentEvent> HandleCommand(
        Command<DocumentCommand> cmd, DocumentState state) =>
        (cmd.CommandDetails, state) switch
        {
            (DocumentCommand.Create c, { Title: null }) =>
                EventActions.Persist<DocumentEvent>(new DocumentEvent.Created(c.Title, c.Content)),
            (DocumentCommand.Create, _) =>
                EventActions.Defer<DocumentEvent>(new DocumentEvent.AlreadyExists()),
            (DocumentCommand.Edit e, { Title: not null }) =>
                EventActions.Persist<DocumentEvent>(new DocumentEvent.Edited(e.Content)),
            _ => EventActions.Defer<DocumentEvent>(new DocumentEvent.NoSuchDocument())
        };

    // fold
    public override DocumentState ApplyEvent(Event<DocumentEvent> evt, DocumentState state) =>
        evt.EventDetails switch
        {
            DocumentEvent.Created e => state with { Title = e.Title, Content = e.Content, Version = state.Version + 1 },
            DocumentEvent.Edited e => state with { Content = e.Content, Version = state.Version + 1 },
            _ => state
        };
}
```
*)

(**
`PersistEvent` stores the event, applies it, and publishes it. `DeferEvent` publishes a rejection
*without* storing it. Both functions are pure and Akka-free, so they are trivially testable. (Concept:
[Aggregates and the write side](concepts/aggregates.html).)

## 2. Wiring — no HOCON required

`Fcqrs.actor` builds the actor system. You only supply a database `Connection` (here via
`Fcqrs.connect`); the rest of the Akka configuration comes from built-in defaults, and an empty
`IConfiguration` is fine. A `.hocon` file is *optional* — see [Configuration](configuration.html).
*)

let buildApi () : IActor =
    let config = ConfigurationBuilder().Build()
    // No logging providers, to keep this to the FCQRS package alone. For
    // console logs add the Microsoft.Extensions.Logging.Console package.
    let loggerFactory = LoggerFactory.Create(fun _ -> ())

    let connection =
        Fcqrs.connect FCQRS.Actor.DBType.Sqlite "Data Source=getstarted.db;"

    Fcqrs.actor config loggerFactory (Some connection) "getstarted"

(**
<div class="cs-alt"></div>

```csharp
// C# declares the system and aggregate through the DI host-builder; FCQRS owns
// the startup ordering. (Aggregates are registered as the classes above.)
var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddFcqrs("Data Source=getstarted.db;", "getstarted")
    .AddAggregate<DocumentAggregate, DocumentState, DocumentCommand, DocumentEvent>();
```
*)

(**
## 3. A minimal read side

A projection is called once per event, in order. This one just forwards each `Document` event to
subscribers so a caller can be told when the read side has caught up. (Concept:
[The read side](concepts/read-models.html).)
*)

let handle (_offset: int64) (event: obj) : IMessageWithCID list =
    match event with
    | :? Event<Document.Event> as e -> [ e :> IMessageWithCID ]
    | _ -> []

(**
<div class="cs-alt"></div>

```csharp
// C#: the same projection function, registered with .AddProjection(...).
public static IList<IMessageWithCID> Handle(long offset, object ev) =>
    ev is Event<DocumentEvent> e
        ? new List<IMessageWithCID> { e }
        : new List<IMessageWithCID>();

builder.Services.AddProjection(handler: _ => Handle, lastOffset: _ => 0);
```
*)

(**
## 4. Send a command and read your write

Registering an aggregate with `Fcqrs.aggregate` returns a typed handle with a `.Send` that mints
nothing for you to remember: pass a correlation id, the aggregate id, the command, and a predicate
that says which event you are waiting for. **Subscribe to the correlation id before sending**, and by
the time the wait returns the read side has processed the event. (Concept:
[Consistency and recovery](concepts/consistency-and-recovery.html).)
*)

let run () =
    async {
        let api = buildApi ()

        // Registering the aggregate IS the registration — it returns the handle.
        let documents =
            Fcqrs.aggregate api
                { Name = "Document"
                  Initial = Document.initial
                  Decide = Document.decide
                  Fold = Document.fold }

        // No sagas yet, but the saga-starter still has to be wired (with none).
        Fcqrs.wireSagaStarters api []

        let subs = Fcqrs.projection api { LastOffset = 0; Handle = handle }

        let cid = Fcqrs.newCid ()
        let id = Fcqrs.aggregateId "readme"

        // Subscribe to this CID *before* sending, so it can't be missed.
        use awaiter = subs.Subscribe(cid, 1)

        let! event =
            documents.Send cid id (Document.Create("README", "hello"))
                (fun e ->
                    match e with
                    | Document.Created _
                    | Document.AlreadyExists -> true
                    | _ -> false)

        do! awaiter.Task |> Async.AwaitTask // read side is now up to date
        printfn "saved %A at version %A" event.EventDetails event.Version
    }

(**
<div class="cs-alt"></div>

```csharp
// C#: resolve the command handler + subscription from DI, then the same
// subscribe-before-send, read-your-writes flow.
var app = builder.Build();
var documents = app.Services.GetRequiredService<Handler<DocumentCommand, DocumentEvent>>();
var subs = app.Services.GetRequiredService<ISubscribe>();

var cid = Values.NewCID();
var id = Values.CreateAggregateId("readme");

using var awaiter = subs.SubscribeForFirst(cid);   // subscribe BEFORE sending
var ev = await documents(
    e => e is DocumentEvent.Created or DocumentEvent.AlreadyExists,
    cid, id, new DocumentCommand.Create("README", "hello"));
await awaiter.Task;                                // read side is now up to date
Console.WriteLine($"saved {ev.EventDetails} at version {ev.Version}");
```
*)

(**
Call it from your program's entry point:

```
[<EntryPoint>]
let main _ =
    run () |> Async.RunSynchronously
    0
```

## Next steps

- **Build it up with explanation** — the [Tutorial](tutorial/index.html) takes this `Document` all the
  way to a cross-aggregate quota saga.
- **Understand the model** — [Concepts](concepts/index.html).
- **Do specific tasks** — the [How-to guides](how-to/index.html).
- **From C#** — [Use FCQRS from C#](how-to/use-from-csharp.html).
*)
