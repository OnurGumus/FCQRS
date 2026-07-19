---
title: Define an aggregate
category: Apply
categoryindex: 4
index: 2
---

# Define an aggregate

An aggregate owns the rules and state required to decide commands for one entity. Choose the boundary
before writing the types: every rule that must be decided atomically needs to fit inside the state of
one aggregate instance. FCQRS processes its commands sequentially, eliminating races within that
boundary.

> **Motivation:** Choose the boundary before the code because sequential handling can protect only the
> facts inside it. No handler implementation can make an invariant atomic after its required state has
> been split across independent aggregates.

The implementation has three domain types and two pure functions:

```fsharp
open FCQRS.Common
open FCQRS.FSharp

type State = { Document: Root option }
let initial = { Document = None }

type Command =
    | Create of Root
    | Edit of DocumentId * Content

type Event =
    | Created of Root
    | Edited of DocumentId * Content
    | DocumentNotFound of DocumentId

// decide (handleCommand): command + state -> action
let decide (cmd: Command<Command>) state =
    match cmd.CommandDetails, state.Document with
    | Create doc, None -> Created doc |> PersistEvent
    | Create doc, Some current when current.Id = doc.Id -> Created current |> DeferEvent
    | Edit(id, content), Some current when current.Id = id -> Edited(id, content) |> PersistEvent
    | Edit(id, _), _ -> DocumentNotFound id |> DeferEvent
    | _ -> UnhandledEvent

// fold (applyEvent): event -> new state
let fold (event: Event<Event>) state =
    match event.EventDetails with
    | Created doc -> { Document = Some doc }
    | Edited(id, content) ->
        match state.Document with
        | Some current when current.Id = id -> { Document = Some { current with Content = content } }
        | _ -> state
    | DocumentNotFound _ -> state

// Registering the aggregate returns its typed handle.
let register (api: IActor) =
    Fcqrs.aggregate api
        { Name = "Document"; Initial = initial; Decide = decide; Fold = fold
          Snapshots = Default }        // snapshot cadence: Default | NoSnapshots | Every n
```

<div class="cs-alt"></div>

```csharp
// In C# the same aggregate is a class deriving Aggregate<>; commands/events are
// C# 15 unions, and it's wired via the DI host-builder.
using static FCQRS.Common;     // Command<>, Event<>, EventAction<>
using static FCQRS.CSharp;      // Aggregate<>, EventActions
using Microsoft.Extensions.DependencyInjection;

public union DocumentCommand(DocumentCommand.Create, DocumentCommand.Edit)
{
    public record Create(Root Document);
    public record Edit(DocumentId Id, Content Content);
}

public union DocumentEvent(DocumentEvent.Created, DocumentEvent.Edited, DocumentEvent.DocumentNotFound)
{
    public record Created(Root Document);
    public record Edited(DocumentId Id, Content Content);
    public record DocumentNotFound(DocumentId Id);
}

public record DocumentState(Root? Document = null)
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
        (cmd.CommandDetails, state.Document) switch
        {
            (DocumentCommand.Create c, null) =>
                EventActions.Persist<DocumentEvent>(new DocumentEvent.Created(c.Document)),
            (DocumentCommand.Create c, { } current) when current.Id == c.Document.Id =>
                EventActions.Defer<DocumentEvent>(new DocumentEvent.Created(current)),
            (DocumentCommand.Edit e, { } current) when current.Id == e.Id =>
                EventActions.Persist<DocumentEvent>(new DocumentEvent.Edited(e.Id, e.Content)),
            (DocumentCommand.Edit e, _) =>
                EventActions.Defer<DocumentEvent>(new DocumentEvent.DocumentNotFound(e.Id)),
            _ => EventActions.Ignore<DocumentEvent>()
        };

    // fold
    public override DocumentState ApplyEvent(Event<DocumentEvent> evt, DocumentState state) =>
        evt.EventDetails switch
        {
            DocumentEvent.Created e => state with { Document = e.Document },
            DocumentEvent.Edited e when state.Document is { } current && current.Id == e.Id =>
                state with { Document = current with { Content = e.Content } },
            _ => state
        };
}

// Register it through the DI host-builder (FCQRS owns startup ordering):
services
    .AddFcqrs(connString, "MyCluster")
    .AddAggregate<DocumentAggregate>();
```

`Fcqrs.aggregate` registers the sharding region and returns an `AggregateHandle` with two members:

- **`.Send cid id command filter`:** send a command and await the first matching aggregate reply. This
  does not wait for a projection; use [Read your writes](read-your-writes.html) for that.
- **`.Factory`:** an entity-ref factory passed to a [saga](write-a-saga.html) so it can target this
  aggregate.

## Choose the action

| Action | Stored | Folded into state | Returned to caller | Sent to projections |
|---|---:|---:|---:|---:|
| `PersistEvent event` | yes | yes | yes | yes |
| `DeferEvent reply` | no | yes, in memory | yes | no |
| `IgnoreEvent` | no | no | no | no |
| `UnhandledEvent` | no | no | handled as unhandled | no |

Persist a fact required to recover the aggregate. Defer a rejection or repeated verdict whose fold
leaves the current state unchanged. FCQRS folds the deferred event in the live actor, but recovery
cannot replay it. A state change caused only by a deferred event therefore disappears after restart.
A deferred reply never wakes a journal projection subscription.

[Deferring, snapshots, and passivation](../concepts/aggregate-lifecycle.html) explains why these
choices remain correct after the actor leaves memory and later recovers.

## Keep replay deterministic

`fold` runs both after persistence and during recovery. It must not read the clock, generate ids, call
services, or write to another store. Capture changing values before persistence and put them in the
event.

`decide` should also remain a deterministic domain function. It may read values already carried by the
command envelope, including `CreationDate`, but should not perform I/O. Use a
[saga](write-a-saga.html) for durable cross-boundary work or an
[async effect](dispatch-async-effects.html) for best-effort work that may be lost on restart.

## Keep identities stable

The aggregate `Name` identifies its sharding and persistence type. Keep it stable after events have
been written. Each entity id identifies one aggregate instance, so route every command for the same
business entity with the same id.

See [Aggregates and the write side](../concepts/aggregates.html) for the reasoning, and
[Test your domain](test-your-domain.html) to test these two functions directly.
