---
title: C# interop and serialization
category: Understand
categoryindex: 3
index: 9
---

# C# interop and serialization

FCQRS is implemented in F#, but its architecture is not specific to F# syntax. A C# application still
has commands, events, immutable state, a decision function, a fold, projections, and sagas.

The important interop question is not “how do I call an F# method?” It is “how do I preserve the same
closed message model and durable serialized contracts in C#?”

> **Motivation:** Language interop should change the surface syntax, not weaken the domain model. Closed
> cases and stable event representations protect the same decisions and histories in either language.

## Start from the language-independent model

Suppose a document supports two requests and four outcomes:

```text
commands: Create | Edit
events:   Created | Edited | AlreadyExists | NoSuchDocument
```

Those are closed sets. The aggregate must handle each possible command and event case. Adding a case
should produce a compiler-visible place to update decisions, folds, tests, and serialization.

F# discriminated unions express the model directly. Current C# compilers have two practical paths.

## Path 1: stable C# with concrete message types

The getting-started C# sample uses ordinary records and one concrete command and event type. This runs
on stable .NET 10 and is a good way to learn the runtime without preview syntax.

```csharp
public sealed record CreateDocument(string Id, string Title, string Content);
public sealed record DocumentCreated(string Id, string Title, string Content);
public sealed record DocumentState(string? Id = null, string? Title = null);
```

An aggregate class derives from `Aggregate<TState,TCommand,TEvent>`, implements `HandleCommand`, and
implements `ApplyEvent`. This path is simplest when an aggregate has one command family represented by
a conventional class hierarchy or when a team prefers stable compiler features.

The runnable project is [`samples/getting-started-csharp`](https://github.com/OnurGumus/FCQRS/tree/main/samples/getting-started-csharp).

## Path 2: C# union types for closed cases

The fuller documentation examples use C# union syntax:

```csharp
public union DocumentCommand(DocumentCommand.Create, DocumentCommand.Edit)
{
    public record Create(Document Document);
    public record Edit(string Id, string Content);
}

public union DocumentEvent(
    DocumentEvent.Created,
    DocumentEvent.Edited,
    DocumentEvent.AlreadyExists,
    DocumentEvent.NoSuchDocument)
{
    public record Created(Document Document);
    public record Edited(string Id, string Content);
    public record AlreadyExists;
    public record NoSuchDocument;
}
```

At the time of writing, the `union` keyword requires a .NET 11 preview SDK and
`<LangVersion>preview</LangVersion>`. FCQRS targets `net10.0` and can be referenced by a newer host.
The [C# how-to](../how-to/use-from-csharp.html) tracks the exact compiler setup used by these examples.

If preview language features are not acceptable, use concrete C# message types or place the closed
domain model in a small F# class library while keeping the host, endpoints, and infrastructure in C#.

## The C# aggregate preserves decide and fold

The interop API uses virtual methods instead of curried F# functions:

```csharp
public sealed class DocumentAggregate
    : Aggregate<DocumentState, DocumentCommand, DocumentEvent>
{
    public override DocumentState InitialState => DocumentState.Initial;
    public override string EntityName => "Document";

    public override EventAction<DocumentEvent> HandleCommand(
        Command<DocumentCommand> command,
        DocumentState state) => /* decide */;

    public override DocumentState ApplyEvent(
        Event<DocumentEvent> stored,
        DocumentState state) => /* fold */;
}
```

`EventActions` constructs persist, defer, ignore, and batch actions. Hosting extensions register
aggregates, sagas, the saga starter, projections, and the runtime in dependency injection order. The
surface is idiomatic C#, while the recovery and consistency model remains the same.

## Envelopes carry framework context

Application payloads are wrapped in `Command<T>` and `Event<T>`. The envelopes carry identity and
coordination data such as message id, aggregate id, correlation id, creation time, version, and
metadata.

Domain code should switch on `CommandDetails` or `EventDetails` and read envelope values only when the
decision genuinely needs them. Tests can create envelopes with the C# test helpers instead of starting
an actor system.

## Serialized events outlive the code that wrote them

Commands travel between nodes, and persisted events remain in the journal across deployments. Their
serialized form is therefore part of the system's durable contract.

FCQRS registers System.Text.Json support for F# records and unions. Its C# union converter writes an
explicit representation like:

```json
{ "$case": "Created", "$value": { "document": { "id": "doc-1" } } }
```

The case discriminator matters. `Approved` and `Rejected` might both carry a string, but equal field
shapes do not give them equal domain meaning.

Do not casually rename persisted event cases, change their meaning, remove required fields, or replace
the serializer with a representation that cannot distinguish cases. New application code must still
read every retained journal event needed for recovery and rebuilds.

## Keep mixed-language boundaries boring

A practical mixed solution can use:

- an F# project for domain values, unions, decisions, and folds;
- a C# host for dependency injection, HTTP endpoints, database access, and projections;
- shared event contracts referenced by both.

Keep F#-specific composition behind small functions or interfaces rather than exposing complex
curried functions to every C# caller. FCQRS's C# builders and base classes already provide this adapter
for the runtime.

## Choose based on team and contract needs

Use stable concrete C# types when they express the current message set clearly. Use C# union syntax
when the preview compiler is acceptable and exhaustive closed cases improve the model. Use an F#
domain library when discriminated unions and functional composition are valuable but the surrounding
application belongs in C#.

The architecture and persistence responsibilities are identical in all three options. The choice is
about source representation, not a different FCQRS runtime.

Run the [C# getting-started project](../get-started.html#Run-a-complete-sample), then follow
[Use FCQRS from C#](../how-to/use-from-csharp.html) for aggregates, hosting, commands, and isolated
tests. Read [Evolve persisted events](../how-to/evolve-events.html) before changing a deployed message
contract.
