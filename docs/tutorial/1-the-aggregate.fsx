(**
---
title: 1. The aggregate
category: Tutorial
categoryindex: 3
index: 2
---
*)

(*** hide ***)
#r "nuget: FCQRS, 6.0.0-rc1"
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

> **Learn alongside this chapter:** read [CQRS and event sourcing](../concepts/cqrs-and-event-sourcing.html)
> for the ground-up mental model. When this chapter introduces deferred replies and snapshot policy,
> use [Deferring, snapshots, and passivation](../concepts/aggregate-lifecycle.html), then keep
> [Define an aggregate](../how-to/define-an-aggregate.html) nearby as the shorter implementation recipe.

## Commands and events are not the same thing

A **command** asks the system to do something, such as `CreateOrUpdate`. The aggregate may accept or
reject it. An **event** records an outcome, such as `Updated` or `Rejected`. A stored event is already a
fact and is not edited when a later command arrives.

Commands and events are different types because one request can have several outcomes. Keeping them
separate also lets a future rule add a new rejection without pretending that every command succeeds.

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
using static FCQRS.Model.CSharp;   // StringTypes
using static FCQRS.Model.Data;      // ShortString, LongString
using System.Diagnostics.CodeAnalysis;

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
`State` is the value FCQRS keeps in the actor and rebuilds during recovery. Before any event has been
stored, the document is absent:
*)

    type State = { Document: Root option }
    let initial = { Document = None }

(**
The first model has one command and one event. In chapter 3, the same `CreateOrUpdate` request can
produce several outcomes after a quota check. Defining separate types now leaves room for that change.
*)

    type Command = CreateOrUpdate of Root

    type Event = Updated of Root

(**
<div class="cs-alt"></div>

```csharp
// C#: the root + state are records; command and event are C# 15 unions.
public sealed record Document(DocumentId Id, Title Title, Content Content)
{
    public static bool TryCreate(Guid id, string title, string content,
        [NotNullWhen(true)] out Document? result, [NotNullWhen(false)] out string? error)
    {
        if (!Title.TryCreate(title, out var t)) { result = null; error = "Invalid title"; return false; }
        if (!Content.TryCreate(content, out var c)) { result = null; error = "Invalid content"; return false; }
        result = new Document(DocumentId.OfGuid(id), t, c); error = null; return true;
    }
}

public record DocumentState(Document? Document = null)
{
    public static readonly DocumentState Initial = new();
}

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

## Common mistakes

- **Reading changing values in `fold`.** Capture time and generated ids before persistence and carry
  them in the event.
- **Persisting rejections.** Use `DeferEvent` when the reply does not represent a state change.
- **Passing raw strings through the domain.** Parse them into domain values at the application edge.
- **Treating aggregate state as stored data.** It is derived from the event history during recovery.

## Understand it and use it

- **Understand:** [CQRS and event sourcing](../concepts/cqrs-and-event-sourcing.html) explains why the
  write and query models separate. [Aggregates and the write side](../concepts/aggregates.html) develops
  the consistency boundary, decision, and fold. [Deferring, snapshots, and
  passivation](../concepts/aggregate-lifecycle.html) develops the recovery lifecycle.
- **Apply:** [Define an aggregate](../how-to/define-an-aggregate.html) is the focused recipe.
  [Test your domain](../how-to/test-your-domain.html) provides command and event envelope helpers and
  replay-test patterns.

Next, [run the aggregate and project its events](2-running-it.html).
*)
