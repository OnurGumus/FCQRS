---
title: Test your domain
category: How-to
categoryindex: 5
index: 6
---

# Test your domain

The valuable logic — `decide` and `fold` — is pure and has no dependency on Akka.NET, so you test it
with plain function calls. No actor system, no database, no async, no facade.

You only need to wrap your command payload in the `Command<_>` envelope the handler expects. A couple
of tiny helpers keep tests readable; `Fcqrs.newCid` mints the correlation id for you.

```fsharp
open FCQRS.Common
open FCQRS.Model.Data
open FCQRS.FSharp

let cmd details : Command<_> =
    { CommandDetails = details
      CreationDate = System.DateTime.UtcNow
      Id = None
      Sender = None
      CorrelationId = Fcqrs.newCid ()
      Metadata = Map.empty }
```

## Test a decision

These use the `Document` from the [tutorial](../tutorial/1-the-aggregate.html) — `CreateOrUpdate`
produces `Updated`.

```fsharp
let doc =
    Document.Root.TryCreate(System.Guid.NewGuid(), "Spec", "draft") |> Result.value

// a write persists Updated
let action = Document.decide (cmd (Document.CreateOrUpdate doc)) Document.initial
test <@ action = PersistEvent (Document.Updated doc) @>
```

For an aggregate with rejections — like the extended `Document` from
[chapter 3](../tutorial/3-adding-a-saga.html), which adds `Approve`/`Errored` — assert the deferred
event the same way:

```fsharp
// approving a document that doesn't exist yet is a deferred rejection
let action2 = Document.decide (cmd Document.Approve) Document.initial
test <@ action2 = DeferEvent (Document.Errored Document.DocumentNotFound) @>
```

## Test the fold

`fold` takes the `Event<_>` envelope; build one (or fold a list to assert the end state):

```fsharp
let evt details : Event<_> =
    { EventDetails = details
      CreationDate = System.DateTime.UtcNow
      Id = None
      Sender = None
      CorrelationId = Fcqrs.newCid ()
      Version = 1L |> ValueLens.TryCreate |> Result.value
      Metadata = Map.empty }

let state = Document.fold (evt (Document.Updated doc)) Document.initial
test <@ state.Document = Some doc @>
test <@ state.Version = 1L @>
```

In C#, `decide`/`fold` are the aggregate's `HandleCommand`/`ApplyEvent` methods, and `TestEnvelope`
wraps the payload (pass a `FakeTimeProvider` to test time-dependent logic deterministically):

<div class="cs-alt"></div>

```csharp
// No actor system, no DI — construct the aggregate and call the methods directly.
using static FCQRS.CSharp;   // TestEnvelope, EventActions
using Xunit;

var agg = new DocumentAggregate();

// a write persists Updated
var cmd = TestEnvelope.Command(new DocumentCommand.CreateOrUpdate(doc), TimeProvider.System);
var action = agg.HandleCommand(cmd, DocumentState.Initial);
Assert.Equal(EventActions.Persist<DocumentEvent>(new DocumentEvent.Updated(doc)), action);

// the fold advances the version
var evt = TestEnvelope.Event(new DocumentEvent.Updated(doc), version: 1, TimeProvider.System);
var state = agg.ApplyEvent(evt, DocumentState.Initial);
Assert.Equal(1, state.Version);
```

That is the payoff of keeping the write side pure (see
[Aggregates](../concepts/aggregates.html)): your core business rules are testable in isolation, fast,
and deterministic. Saga `handleEvent`/`applySideEffects` functions are pure in the same way and test
the same way.
