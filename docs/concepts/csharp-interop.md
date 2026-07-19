---
title: C# interop and serialization
category: Concepts
categoryindex: 4
index: 7
---

# C# interop and serialization

FCQRS represents commands and events as closed sets of values and rebuilds immutable state by folding
events. F# provides records and discriminated unions for this model. C# 15 provides union types, and
FCQRS exposes C# APIs for aggregates, sagas, projections, and subscriptions.

## C# 15 discriminated unions as messages

`UserCommand` may contain `Register` or `Login` and no unnamed third case. C# 15 expresses that closed
set with the `union` keyword. FCQRS treats the cases as first-class message types.

The compiler support is currently a preview feature. Use a .NET 11 preview SDK with
`<LangVersion>preview</LangVersion>`. FCQRS itself ships `net10.0` assets that run on a .NET 11 host.
Teams that cannot enable a preview compiler can place the domain types in a small F# project and keep
the host, endpoints, and projections in C#. [Use FCQRS from C#](../how-to/use-from-csharp.html) lists
the current compiler requirements.

```csharp
public union UserCommand(UserCommand.Register, UserCommand.Login)
{
    public record Register(string Username, string Password);
    public record Login(string Password);
}

public union UserEvent(
    UserEvent.Registered, UserEvent.LoginSucceeded, UserEvent.LoginFailed)
{
    public record Registered(string Username, string Password);
    public record LoginSucceeded;
    public record LoginFailed;
}
```

An aggregate switches on the command and current state to choose an `EventAction`, then switches on a
stored event to produce the next state. The state can be an ordinary C# `record`. The interop layer
provides `EventActions`, aggregate initialization, saga builders, projection APIs, and subscriptions,
so application code does not need to construct F# function values. The
[C# how-to](../how-to/use-from-csharp.html) shows the complete API, and the
[`focument-csharp`](https://github.com/onurgumus/focument) sample contains a running application.

A nested case record implicitly converts to its union. Methods that accept the union can therefore
receive a case value directly.

## How messages are serialized

Commands and events are stored in the journal and sent between cluster nodes, so their serialized
shape becomes part of the application's persistent data. FCQRS registers a System.Text.Json serializer
for its messages. F# support handles F# records and unions. A dedicated C# union converter writes
`{ "$case": ..., "$value": ... }`, preserving the case name even when two cases have the same value
shape.

Keep persisted message types stable and plan explicit migrations for incompatible changes. Serializer
registration happens when the actor system is created.

## When to reach for C#

The [get-started](../get-started.html) page and tutorial use F#. C# applications use the same domain
model and runtime through the interop APIs. A mixed solution can also define the domain in F# and use
it from a C# host.
