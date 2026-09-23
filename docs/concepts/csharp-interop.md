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

The tutorial's account supports three requests and four outcomes:

```text
commands: Open | Deposit | Withdraw
events:   Opened | Deposited | Withdrawn | Rejected
```

Those are closed sets. The aggregate must handle each possible command and event case. Adding a case
should produce a compiler-visible place to update decisions, folds, tests, and serialization.

F# discriminated unions express the model directly. C# 15 has two ways to express a closed set, and
C# on .NET 10 has none.

## Path 1: C# union types

The [tutorial](../tutorial/open-an-account.html) declares each set with the C# `union` keyword:

```csharp
public union AccountCommand(Open, Deposit, Withdraw);
public sealed record Open(string Owner);
public sealed record Deposit(decimal Amount);
public sealed record Withdraw(decimal Amount);

public union AccountEvent(Opened, Deposited, Withdrawn, Rejected);
public sealed record Opened(string Owner);
public sealed record Deposited(decimal Amount);
public sealed record Withdrawn(decimal Amount);
public sealed record Rejected(string Reason);
```

A `switch` over `AccountEvent` that misses a case gets warning CS8509. FCQRS serializes a union with
its own converter, so the cases need no serialization attributes.

The `union` keyword is part of C# 15. It needs the .NET 11 SDK and a `net11.0` target. FCQRS targets
`net10.0` and can be referenced by a newer host. The [C# how-to](../how-to/use-from-csharp.html) tracks
the exact compiler setup used by these examples.

## Path 2: closed record hierarchies

Derived records can also represent the cases. The deposit and withdrawal events share a base record:

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$case")]
[JsonDerivedType(typeof(Deposited), "deposited")]
[JsonDerivedType(typeof(Withdrawn), "withdrawn")]
public closed record AccountEvent;
public sealed record Deposited(decimal Amount) : AccountEvent;
public sealed record Withdrawn(decimal Amount) : AccountEvent;
```

This excerpt needs `System.Text.Json.Serialization`. A hierarchy must register each derived event
case: without the attributes, System.Text.Json writes each event as `{}` and cannot read it back. The
discriminator names are serialized contracts. Keep each case registered and test serialization through
the base type, which is the type the journal envelope carries.

`closed` is also new in C# 15. Only the project that declares a closed record can derive from it, so
the compiler knows every case and a `switch` that misses one gets warning CS8509.

## On .NET 10

C# on .NET 10 has neither keyword. An `abstract` base record works with the same attributes, but C#
does not enforce an exhaustive set, so a new case needs explicit decision, fold, projection, and test
updates. Alternatively, use one concrete command type and one concrete event type, as
[`samples/registration-csharp`](https://github.com/OnurGumus/FCQRS/tree/main/samples/registration-csharp)
does, or place the closed domain model in a small F# class library while keeping the host, endpoints,
and infrastructure in C#.

## The C# aggregate preserves decide and fold

The interop API uses virtual methods instead of curried F# functions:

```csharp
public sealed class Account
    : Aggregate<AccountState, AccountCommand, AccountEvent>
{
    public override string EntityName => "Account";
    public override AccountState InitialState => new();

    public override EventAction<AccountEvent> HandleCommand(
        Command<AccountCommand> command,
        AccountState state) => /* decide */;

    public override AccountState ApplyEvent(
        Event<AccountEvent> stored,
        AccountState state) => /* fold */;
}
```

`EventActions` constructs persist, defer, ignore, and batch actions. Hosting extensions register
aggregates, sagas, the saga starter, projections, and the runtime in dependency injection order. The
surface is idiomatic C#, while the recovery and consistency model remains the same.
[Withdraw money](../tutorial/withdraw-money.html) shows both methods in full.

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
{ "$case": "Withdrawn", "$value": { "Amount": 60 } }
```

The discriminator is the case type's full name: `Withdrawn` for a case declared at the top level, as in
the tutorial, or `AccountEvent+Withdrawn` for a case nested in the union declaration. Renaming or moving
a case type changes it.

The case discriminator matters. `Deposited` and `Withdrawn` both carry an amount, but equal field
shapes do not give them equal domain meaning.

Do not casually rename persisted event cases, change their meaning, remove required fields, or replace
the serializer with a representation that cannot distinguish cases. New application code must still
read every retained journal event needed for recovery and rebuilds.

`WithEventUpcaster<OldPayload, NewPayload>` adapts an already-readable payload on FCQRS journal reads.
Its source type must match the envelope's declared payload type, including a base record or union
when the event is stored through that type. The converter can feed another converter in a chain.
[Evolve persisted events](../how-to/evolve-events.html) covers registration, live-message limits, and
snapshot compatibility.

## Keep mixed-language boundaries boring

A practical mixed solution can use:

- an F# project for domain values, unions, decisions, and folds;
- a C# host for dependency injection, HTTP endpoints, database access, and projections;
- shared event contracts referenced by both.

Keep F#-specific composition behind small functions or interfaces rather than exposing complex
curried functions to every C# caller. FCQRS's C# builders and base classes already provide this adapter
for the runtime.

## Choose based on team and contract needs

On C# 15, use C# unions, as the tutorial does, or `closed` record hierarchies when each stored case
needs a name that does not depend on its type name. Both give exhaustive `switch` checks. On .NET 10,
use concrete C# types when they express the current message set clearly. Use an F# domain library when
discriminated unions and functional composition are valuable but the surrounding application belongs
in C#.

The architecture and persistence responsibilities are identical in every option. The choice is
about source representation, not a different FCQRS runtime.

Run the [tutorial's C# sample](../tutorial/open-an-account.html#Run-it), then follow
[Use FCQRS from C#](../how-to/use-from-csharp.html) for aggregates, hosting, commands, and isolated
tests. Read [Evolve persisted events](../how-to/evolve-events.html) before changing a deployed message
contract.
