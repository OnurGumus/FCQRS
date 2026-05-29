---
title: C# interop and serialization
category: Concepts
categoryindex: 4
index: 7
---

# C# interop and serialization

FCQRS is written in F#, but the model it asks you to build — closed sets of commands and events, an
immutable state folded from events — is expressible just as cleanly in modern C#. This page explains
how, and how messages are serialized underneath.

## C# 15 discriminated unions as messages

The natural way to model a command or event type is a *closed set of alternatives*: a `Register` or a
`Login`, never a third unnamed thing. F# has discriminated unions for exactly this. C# 15 adds the
`union` keyword, and FCQRS treats those unions as first-class message types:

```csharp
public union UserCommand(UserCommand.Register, UserCommand.Login)
{
    public record Register(string Username, string Password);
    public record Login(string Password);
}

public union UserEvent(UserEvent.Registered, UserEvent.LoginSucceeded, UserEvent.LoginFailed)
{
    public record Registered(string Username, string Password);
    public record LoginSucceeded;
    public record LoginFailed;
}
```

Your aggregate is then a `handleCommand` that switches on `(command, state)` and an `applyEvent` that
switches on the event — the same two pure functions as in F#, written as C# `switch` expressions. The
state is an ordinary `record`, and the interop layer (`EventActions.Persist/Defer/Ignore`,
`IActorExtensions.InitActor`, `SagaBuilderCSharp`, `QueryApi`, `ISubscribe`) gives you typed,
idiomatic entry points so you never touch an F# function value directly. The
[how-to guide](../how-to/use-from-csharp.html) shows the full surface, and the `focument-csharp`
sample app is a complete, real example.

A nested case record **implicitly converts to its union**, and the framework relies on this in a few
places (for instance, when a saga issues a command typed as the union). Keep that in mind when a
method wants the union type rather than the bare case.

## How messages are serialized

Commands and events are persisted to the journal and sent across the cluster, so they must serialize.
FCQRS registers a System.Text.Json–based serializer for any message implementing its internal
serializable marker, alongside Akka's default for everything else. For F# records and unions this is
handled by the F#-aware JSON support; for **C# 15 unions** the framework includes a dedicated
converter that round-trips them as `{ "$case": …, "$value": … }`, because System.Text.Json cannot
otherwise pick a case on read.

You rarely interact with serialization directly. The two things worth knowing: message types should
be stable, serializable shapes (records and unions are ideal), and the serializer is configured for
you when you create the actor system — there is nothing to wire up by hand.

## When to reach for C#

Use whichever language fits your team. The F# side is the most concise, and the [tutorial](../tutorial/index.html)
and most [concepts](index.html) examples are in F#. The C# side exists so a C#-first team can adopt
FCQRS without leaving their language, and so a mixed solution can share a domain. The
[get-started](../get-started.html) page and tutorial focus on F#; the C# how-to translates each step.
