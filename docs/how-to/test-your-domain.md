---
title: Test your domain
category: Apply
categoryindex: 4
index: 6
---

# Test your domain

Test the domain at four levels: individual decisions, individual folds, replayed histories, and repeated
commands. These tests call pure functions directly and need no actor system or database.

Use fixed envelope values so failures are reproducible:

```fsharp
open FCQRS.Common
open FCQRS.Model.Data
open FCQRS.FSharp

let fixedTime = System.DateTime(2026, 1, 1, 12, 0, 0, System.DateTimeKind.Utc)

let command details : Command<_> =
    { CommandDetails = details
      CreationDate = fixedTime
      Id = None
      Sender = None
      CorrelationId = Fcqrs.newCid ()
      Metadata = Map.empty }

let event version details : Event<_> =
    { EventDetails = details
      CreationDate = fixedTime
      Id = None
      Sender = None
      CorrelationId = Fcqrs.newCid ()
      Version = version |> ValueLens.TryCreate |> Result.value
      Metadata = Map.empty }
```

<div class="cs-alt"></div>

```csharp
using Microsoft.Extensions.Time.Testing;
using static FCQRS.CSharp;

var fixedTime = new FakeTimeProvider(
    new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

Command<T> MakeCommand<T>(T details) =>
    TestEnvelope.Command(details, fixedTime);

Event<T> MakeEvent<T>(long version, T details) where T : notnull =>
    TestEnvelope.Event(details, version, fixedTime);
```

`FakeTimeProvider` comes from the `Microsoft.Extensions.TimeProvider.Testing` package. Use
`TimeProvider.System` when the rule does not inspect the envelope time and a fixed clock adds no value.

The examples below use `Expect.equal` from Expecto. Use the equivalent equality assertion in another
test framework.

## Test the decision table

These use the `Document` from the [tutorial](../tutorial/1-the-aggregate.html). `CreateOrUpdate`
produces `Updated`.

```fsharp
let doc =
    Document.Root.TryCreate(System.Guid.NewGuid(), "Spec", "draft") |> Result.value

// a write persists Updated
let action = Document.decide (command (Document.CreateOrUpdate doc)) Document.initial

Expect.equal
    action
    (PersistEvent (Document.Updated doc))
    "creating a document stores Updated"
```

<div class="cs-alt"></div>

```csharp
Document.TryCreate(Guid.NewGuid(), "Spec", "draft", out var doc, out _);
var aggregate = new DocumentAggregate();

var action = aggregate.HandleCommand(
    MakeCommand<DocumentCommand>(new DocumentCommand.CreateOrUpdate(doc!)),
    DocumentState.Initial);

Assert.Equal(
    EventActions.Persist<DocumentEvent>(new DocumentEvent.Updated(doc!)),
    action);
```

For an idempotent verdict, such as publication confirmation in
[chapter 3](../tutorial/3-adding-a-saga.html), assert the deferred event the same way:

```fsharp
let publishedState =
    Document.Finished(doc.Id, "guides/fcqrs", Document.Published)

// reporting the same result again does not add another journal entry
let action2 =
    Document.decide
        (command (Document.FinishPublication Document.Published))
        publishedState

Expect.equal
    action2
    (DeferEvent(
        Document.PublicationFinished(doc.Id, "guides/fcqrs", Document.Published)))
    "repeating the result defers the existing publication outcome"
```

<div class="cs-alt"></div>

```csharp
var publication = new PublicationDocumentAggregate();
var publishedState = new PublicationDocumentState.Finished(
    doc!.Id, "guides/fcqrs", PublicationResult.Published);

var action2 = publication.HandleCommand(
    MakeCommand<DocumentCommand>(
        new DocumentCommand.FinishPublication(PublicationResult.Published)),
    publishedState);

Assert.Equal(
    EventActions.Defer<DocumentEvent>(new DocumentEvent.PublicationFinished(
        doc.Id, "guides/fcqrs", PublicationResult.Published)),
    action2);
```

Write one case for every meaningful command and state combination, including commands that should be
ignored or unhandled.

## Test one fold

`fold` takes an event envelope and produces the next state:

```fsharp
let state = Document.fold (event 1L (Document.Updated doc)) Document.initial
Expect.equal state.Document (Some doc) "Updated becomes the current document"
```

<div class="cs-alt"></div>

```csharp
var state = aggregate.ApplyEvent(
    MakeEvent<DocumentEvent>(1, new DocumentEvent.Updated(doc!)),
    DocumentState.Initial);

Assert.Equal(doc, state.Document);
```

The envelope version is maintained by FCQRS. Do not duplicate it in domain state unless the domain has
a separate version concept with different meaning.

## Test replay

Fold a complete history to verify recovery:

```fsharp
let edited = { doc with Content = newContent }

let recovered =
    [ event 1L (Document.Updated doc)
      event 2L (Document.Updated edited) ]
    |> List.fold (fun state stored -> Document.fold stored state) Document.initial

Expect.equal recovered.Document (Some edited) "replay recovers the latest document"
```

<div class="cs-alt"></div>

```csharp
var edited = doc! with { Content = newContent };
var history = new DocumentEvent[]
{
    new DocumentEvent.Updated(doc),
    new DocumentEvent.Updated(edited)
};

var recovered = history
    .Select((details, index) => MakeEvent<DocumentEvent>(index + 1, details))
    .Aggregate(DocumentState.Initial,
        (state, stored) => aggregate.ApplyEvent(stored, state));

Assert.Equal(edited, recovered.Document);
```

Use fixed events captured from an older release as compatibility fixtures. A replay test should fail if
a changed fold can no longer reproduce the historical state.

## Test retry behaviour

Call the same command against the state produced by its first event. A repeated command should not
repeat a business effect. For the chapter 3 document, confirming an already published document returns
a deferred `Published` reply instead of persisting another publication.

Also test boundary times and generated ids. Put the chosen value in the command or event; never let a
fold read the live clock or random generator.

In C#, `decide` and `fold` are the aggregate's `HandleCommand` and `ApplyEvent` methods. The paired
examples use `TestEnvelope` and a `FakeTimeProvider`; no actor system or dependency-injection container
is started.

## Test sagas in two layers

Test `handleEvent` by asserting the next saga state for each event and current state. Test
`applySideEffects` separately by asserting its transition, target aggregate id, command payload, and
delay. Include recovery cases for branches that treat `recovering = true` differently.

Use integration tests for actor routing, persistence plugins, projection transactions, and recovery
across an actual process restart. Pure domain tests do not prove those infrastructure paths.

See [Aggregates](../concepts/aggregates.html) and
[Testing and evolution](../tutorial/4-testing-and-evolution.html).
