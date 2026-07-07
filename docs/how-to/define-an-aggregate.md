---
title: Define an aggregate
category: How-to
categoryindex: 5
index: 2
---

# Define an aggregate

An aggregate is a couple of types and two pure functions, bound to an actor with one call through the
`FCQRS.FSharp` facade.

```fsharp
open FCQRS.Common
open FCQRS.FSharp

type State = { Document: Root option; Version: int64 }
let initial = { Document = None; Version = 0L }

type Command =
    | CreateOrUpdate of Root
    | Approve

type Event =
    | Updated of Root
    | ApprovedEvt
    | Errored

// decide (handleCommand): command + state -> action
let decide (cmd: Command<Command>) state =
    match cmd.CommandDetails, state.Document with
    | CreateOrUpdate doc, _ -> Updated doc |> PersistEvent
    | Approve, Some _ -> ApprovedEvt |> PersistEvent
    | Approve, None -> Errored |> DeferEvent

// fold (applyEvent): event -> new state
let fold (event: Event<Event>) state =
    match event.EventDetails with
    | Updated doc -> { state with Document = Some doc; Version = state.Version + 1L }
    | _ -> state

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

public union DocumentCommand(DocumentCommand.CreateOrUpdate, DocumentCommand.Approve)
{
    public record CreateOrUpdate(Root Document);
    public record Approve;
}

public union DocumentEvent(DocumentEvent.Updated, DocumentEvent.ApprovedEvt, DocumentEvent.Errored)
{
    public record Updated(Root Document);
    public record ApprovedEvt;
    public record Errored;
}

public record DocumentState(Root? Document = null, long Version = 0)
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
            (DocumentCommand.Approve, { } _) =>
                EventActions.Persist<DocumentEvent>(new DocumentEvent.ApprovedEvt()),
            (DocumentCommand.Approve, null) =>
                EventActions.Defer<DocumentEvent>(new DocumentEvent.Errored()),
            _ => EventActions.Ignore<DocumentEvent>()
        };

    // fold
    public override DocumentState ApplyEvent(Event<DocumentEvent> evt, DocumentState state) =>
        evt.EventDetails switch
        {
            DocumentEvent.Updated e => state with { Document = e.Document, Version = state.Version + 1 },
            _ => state
        };
}

// Register it through the DI host-builder (FCQRS owns startup ordering):
services
    .AddFcqrs(connString, "MyCluster")
    .AddAggregate<DocumentAggregate, DocumentState, DocumentCommand, DocumentEvent>();
```

`Fcqrs.aggregate` **is** the registration — calling it initializes the sharding region and hands back
an `AggregateHandle` with two members:

- **`.Send cid id command filter`** — send a command and await the first matching event
  (read-your-writes). Returns `Async<Event<'Event>>`.
- **`.Factory`** — an entity-ref factory you hand to a [saga](write-a-saga.html) so it can target this
  aggregate.

**Return the right action.** `PersistEvent` stores, applies, and publishes the event (and bumps the
version). `DeferEvent` publishes a rejection without storing it — use it for "no" answers (like
`Errored`) that shouldn't enter history. `IgnoreEvent` does nothing; `UnhandledEvent` rejects the
message.

**Keep both functions pure.** No I/O, no clock, no side effects — they run on the happy path *and*
during recovery replay, and must produce identical results. Side effects belong in a
[saga](write-a-saga.html).

See [Aggregates and the write side](../concepts/aggregates.html) for the reasoning, and
[Test your domain](test-your-domain.html) to test these two functions directly.
