(**
---
title: 1. The aggregate
category: Learn FCQRS
categoryindex: 2
index: 3
---
*)

(*** hide ***)
#r "nuget: FCQRS, 6.0.0"
open System
open FCQRS.Common
open FCQRS.Model.Data
open FCQRS.FSharp

(**
# 1. The aggregate

Most applications store the current document. Saving a new body replaces the old one. An event-sourced
document stores facts such as `DocumentCreated` and `ContentEdited`, then derives the current document
by applying those facts in order.

This chapter models that write side. It uses no actor system or database yet. You will define the
domain values, separate requests from recorded outcomes, and write the two pure functions FCQRS runs
inside an aggregate.

> **Course position:** the quickstart showed the whole request path. This chapter isolates its first
> part, the aggregate decision. By the end you will be able to explain and test command, event, state,
> `decide`, and `fold` without running Akka.NET.

## Commands and events are not the same thing

A **command** asks the system to do something, such as `CreateOrUpdate`. The aggregate may accept or
reject it. An **event** records an outcome, such as `Updated` or `Rejected`. A stored event is already a
fact and is not edited when a later command arrives.

Commands and events are different types because one request can have several outcomes. Keeping them
separate also lets the publication workflow in chapter 3 add a new outcome without pretending that
every command succeeds.

> **Motivation:** A command records intent; an event records what the domain decided. Keeping those
> moments separate makes rejection explicit and prevents an unaccepted request from entering history
> as though it were a fact.

An **aggregate** owns the state and rules needed to make decisions about one entity. FCQRS runs each
aggregate as an actor that handles one command at a time. Sequential handling eliminates races within
that aggregate. Rules spanning several aggregates require coordination, which chapter 3 introduces.

Open `Program.fs` in the project from the [tutorial intro](index.html) and follow along. The opens
first:

```fsharp
open System
open FCQRS.Common
open FCQRS.Model.Data
open FCQRS.FSharp
```

<div class="cs-alt"></div>

```csharp
using System;
using System.Diagnostics.CodeAnalysis;
using FCQRS;
using static FCQRS.Common;
using static FCQRS.CSharp;
using static FCQRS.Model.CSharp;
using static FCQRS.Model.Data;
```

## Make the illegal values impossible to type

A title and document body have different meaning even though both arrive as strings. Separate domain
types prevent them from being swapped and provide one place to reject invalid input. FCQRS provides
validated `ShortString` and `LongString` values through `ValueLens`; the document wraps them as `Title`
and `Content`.
*)

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

(**
<div class="cs-alt"></div>

```csharp
// C#: validated value objects wrap FCQRS's ShortString / LongString.
public readonly record struct DocumentId(Guid Value)
{
    public static DocumentId OfGuid(Guid g) => new(g);
    public override string ToString() => Value.ToString();
}

public readonly record struct Title(ShortString Value)
{
    public static bool TryCreate(string s, [NotNullWhen(true)] out Title result)
    {
        if (StringTypes.TryCreateShortString(s, out var v)) { result = new Title(v); return true; }
        result = default; return false;
    }
}

public readonly record struct Content(LongString Value)
{
    public static bool TryCreate(string s, [NotNullWhen(true)] out Content result)
    {
        if (StringTypes.TryCreateLongString(s, out var v)) { result = new Content(v); return true; }
        result = default; return false;
    }
}
```
*)

(**
`TryCreate` returns a `Result`, so invalid input is handled where raw strings enter the application.
After construction, `Title` and `Content` carry validated values and the aggregate does not repeat the
same validation.

## State, command, event

`Root.TryCreate` validates the title and content together and returns either a complete document or one
error. Commands therefore carry a complete `Root`, not a mixture of raw and validated fields.
*)

module Document =
    open Values

    type Root =
        { Id: DocumentId; Title: Title; Content: Content }

        static member TryCreate(guid, title, content) =
            match Title.TryCreate title, Content.TryCreate content with
            | Ok t, Ok c -> Ok { Id = DocumentId.OfGuid guid; Title = t; Content = c }
            | Error e, _ -> Error e
            | _, Error e -> Error e

(**
<div class="cs-alt"></div>

```csharp
public sealed record Document(DocumentId Id, Title Title, Content Content)
{
    public static bool TryCreate(
        Guid id,
        string title,
        string content,
        [NotNullWhen(true)] out Document? result,
        [NotNullWhen(false)] out string? error)
    {
        if (!Title.TryCreate(title, out var validTitle))
        {
            result = null;
            error = "Invalid title";
            return false;
        }

        if (!Content.TryCreate(content, out var validContent))
        {
            result = null;
            error = "Invalid content";
            return false;
        }

        result = new Document(DocumentId.OfGuid(id), validTitle, validContent);
        error = null;
        return true;
    }
}
```

`State` is the value FCQRS keeps in the actor and rebuilds during recovery. Before any event has been
stored, the document is absent:
*)

    type State = { Document: Root option }
    let initial = { Document = None }

(**
<div class="cs-alt"></div>

```csharp
public sealed record DocumentState(Document? Document = null)
{
    public static readonly DocumentState Initial = new();
}
```

The first model has one command and one event. Chapter 3 adds a separate `Publish` request with several
possible outcomes. Defining commands and events separately now leaves room for that growth.
*)

    type Command = CreateOrUpdate of Root

    type Event = Updated of Root

(**
<div class="cs-alt"></div>

```csharp
// C#: commands and events are separate C# union types.
public union DocumentCommand(DocumentCommand.CreateOrUpdate)
{
    public record CreateOrUpdate(Document Document);
}

public union DocumentEvent(DocumentEvent.Updated)
{
    public record Updated(Document Document);
}
```
*)

(**
## Decide what the command means

`decide` receives a command envelope and the current state. The envelope carries the command payload,
creation time, correlation id, and metadata. The function returns an `EventAction` describing what
FCQRS should do. It performs no mutation or I/O itself.
*)

    let decide (cmd: Command<Command>) state =
        match cmd.CommandDetails with
        | CreateOrUpdate doc -> Updated doc |> PersistEvent

(**
<div class="cs-alt"></div>

```csharp
// C#: decide is the aggregate's HandleCommand method (a switch expression).
public override EventAction<DocumentEvent> HandleCommand(
    Command<DocumentCommand> cmd, DocumentState state) =>
    cmd.CommandDetails switch
    {
        DocumentCommand.CreateOrUpdate c =>
            EventActions.Persist<DocumentEvent>(new DocumentEvent.Updated(c.Document)),
        _ => EventActions.Ignore<DocumentEvent>()
    };
```
*)

(**
The common actions are:

- `PersistEvent event`: append the event, increment the aggregate version, fold it into state, and
  publish it.
- `DeferEvent reply`: publish and fold a reply without storing it or incrementing the persisted
  version. Use this for a rejection or idempotent response whose fold leaves state unchanged. Any
  state change made only by a deferred event disappears on recovery.
- `IgnoreEvent`: produce no reply or state change.
- `UnhandledEvent`: report that the command is not valid for this handler or state.

Persist only facts needed to reconstruct the aggregate. Operational auditing of rejected attempts
belongs in logs or a separate audit model unless the rejection itself changes the domain.

## `fold`: rebuild the present from the past

FCQRS calls `fold` after persisting a new event and again when replaying stored events during recovery.
The same event sequence must produce the same state in both cases.
*)

    let fold (event: Event<Event>) state =
        match event.EventDetails with
        | Updated doc -> { Document = Some doc }

(**
<div class="cs-alt"></div>

```csharp
// C#: fold is the aggregate's ApplyEvent method.
public override DocumentState ApplyEvent(Event<DocumentEvent> evt, DocumentState state) =>
    evt.EventDetails switch
    {
        DocumentEvent.Updated e => state with { Document = e.Document },
        _ => state
    };
```
*)

(**
`fold` must not read the clock, generate random values, or perform I/O. If a decision needs the current
time, read the creation time from the command and include the relevant value in the event. Recovery then
uses the value that was recorded when the decision was made.

## Bind the functions to an actor

`Fcqrs.aggregate` registers the functions with the actor system and returns a typed handle used to send
commands. The actor lifecycle, sharding, persistence, and recovery stay outside the domain functions.
*)

    let register (api: IActor) =
        Fcqrs.aggregate api
            { Name = "Document"
              Initial = initial
              Decide = decide
              Fold = fold
              Snapshots = Default }        // snapshot cadence: Default | NoSnapshots | Every n

(**
<div class="cs-alt"></div>

```csharp
// C#: HandleCommand/ApplyEvent live on a class deriving Aggregate<>, registered
// through the DI host-builder (the C# counterpart of Fcqrs.aggregate).
public sealed class DocumentAggregate : Aggregate<DocumentState, DocumentCommand, DocumentEvent>
{
    public override DocumentState InitialState => DocumentState.Initial;
    public override string EntityName => "Document";
    // HandleCommand and ApplyEvent as shown above.
}

services
    .AddFcqrs("Data Source=tutorial.db;", "tutorial")
    .AddAggregate<DocumentAggregate, DocumentState, DocumentCommand, DocumentEvent>();
```
*)

(**
`Name`, `Initial`, `Decide`, `Fold`, and `Snapshots` form the aggregate definition. The domain functions
do not depend on the actor implementation.

## What you now understand

The write model now has four distinct parts: a command requests a change, `decide` chooses an action, a
persisted event records the result, and `fold` derives state from stored events. You can test each
decision with ordinary function calls:

```fsharp
let doc = Document.Root.TryCreate(System.Guid.NewGuid(), "Spec", "draft") |> Result.value
// decide returns an action, so the test asserts on that value.
let action = Document.decide (cmd (Document.CreateOrUpdate doc)) Document.initial
// => PersistEvent (Updated doc)
```

<div class="cs-alt"></div>

```csharp
Document.TryCreate(Guid.NewGuid(), "Spec", "draft", out var doc, out _);
var aggregate = new DocumentAggregate();

var action = aggregate.HandleCommand(
    TestEnvelope.Command<DocumentCommand>(
        new DocumentCommand.CreateOrUpdate(doc!)),
    DocumentState.Initial);

Assert.Equal(
    EventActions.Persist<DocumentEvent>(new DocumentEvent.Updated(doc!)),
    action);
```

## Common mistakes

- **Reading changing values in `fold`.** Capture time and generated ids before persistence and carry
  them in the event.
- **Persisting rejections.** Use `DeferEvent` when the reply does not represent a state change.
- **Passing raw strings through the domain.** Parse them into domain values at the application edge.
- **Treating aggregate state as stored data.** It is derived from the event history during recovery.

## Continue the learning path

Next, [run the aggregate and project its events](2-running-it.html). Chapter 2 uses the domain model
you just built and introduces the runtime, journal, projection, and query path.

After completing chapter 2, use [Aggregates and the write side](../concepts/aggregates.html) for a
deeper boundary-design discussion or [Define an aggregate](../how-to/define-an-aggregate.html) as the
short implementation recipe. Neither is required before continuing.
*)
