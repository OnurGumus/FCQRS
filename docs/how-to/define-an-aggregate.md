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

The implementation has three domain types and two pure functions. This example extends the tutorial's
document aggregate with a `Delete` command so all three common actions appear:

```fsharp
open FCQRS.Common
open FCQRS.FSharp

type State = { Document: Root option }
let initial = { Document = None }

type Command =
    | CreateOrUpdate of Root
    | Delete

type Event =
    | Updated of Root
    | Deleted
    | DocumentNotFound

// decide (handleCommand): command + state -> action
let decide (cmd: Command<Command>) state =
    match cmd.CommandDetails, state.Document with
    | CreateOrUpdate doc, _ -> Updated doc |> PersistEvent
    | Delete, Some _ -> Deleted |> PersistEvent
    | Delete, None -> DocumentNotFound |> DeferEvent

// fold (applyEvent): event -> new state
let fold (event: Event<Event>) state =
    match event.EventDetails with
    | Updated doc -> { Document = Some doc }
    | Deleted -> { Document = None }
    | DocumentNotFound -> state

// Registering the aggregate returns its typed handle.
let register (api: IActor) =
    Fcqrs.aggregate api
        { Name = "Document"; Initial = initial; Decide = decide; Fold = fold
          Snapshots = Default }        // snapshot cadence: Default | NoSnapshots | Every n
```

Every `Command` case is covered above, so no catch-all is needed. When you do add one, choose
`UnhandledEvent` for "this command is not valid here" (the caller's wait times out with a
`TimeoutException`) and `IgnoreEvent` when producing no reply is the intended outcome.

<div class="cs-alt"></div>

```csharp
// In C# the same aggregate is a class deriving Aggregate<>; commands/events are
// C# 15 unions (the preview-union setup in C# interop and serialization), and
// it's wired via the DI host-builder.
using static FCQRS.Common;     // Command<>, Event<>, EventAction<>
using static FCQRS.CSharp;      // Aggregate<>, EventActions
using Microsoft.Extensions.DependencyInjection;

public union DocumentCommand(DocumentCommand.CreateOrUpdate, DocumentCommand.Delete)
{
    public record CreateOrUpdate(Root Document);
    public record Delete;
}

public union DocumentEvent(DocumentEvent.Updated, DocumentEvent.Deleted, DocumentEvent.DocumentNotFound)
{
    public record Updated(Root Document);
    public record Deleted;
    public record DocumentNotFound;
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
            (DocumentCommand.CreateOrUpdate c, _) =>
                EventActions.Persist<DocumentEvent>(new DocumentEvent.Updated(c.Document)),
            (DocumentCommand.Delete, { }) =>
                EventActions.Persist<DocumentEvent>(new DocumentEvent.Deleted()),
            (DocumentCommand.Delete, null) =>
                EventActions.Defer<DocumentEvent>(new DocumentEvent.DocumentNotFound()),
            _ => EventActions.Ignore<DocumentEvent>()
        };

    // fold
    public override DocumentState ApplyEvent(Event<DocumentEvent> evt, DocumentState state) =>
        evt.EventDetails switch
        {
            DocumentEvent.Updated e => state with { Document = e.Document },
            DocumentEvent.Deleted => state with { Document = null },
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
