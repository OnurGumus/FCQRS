---
title: Test your domain
category: How-to
categoryindex: 5
index: 6
---

# Test your domain

The valuable logic — `handleCommand` and `applyEvent` — is pure and has no dependency on Akka.NET, so
you test it with plain function calls. No actor system, no database, no async.

You only need to wrap your command payload in the `Command<_>` envelope the handler expects. A tiny
helper keeps tests readable:

```fsharp
open FCQRS.Common
open FCQRS.Model.Data

let cmd details : Command<_> =
    { CommandDetails = details
      CreationDate = System.DateTime.UtcNow
      Id = System.Guid.NewGuid().ToString() |> ValueLens.CreateAsResult |> Result.value
      Sender = None
      CorrelationId = System.Guid.NewGuid().ToString() |> ValueLens.CreateAsResult |> Result.value
      Metadata = Map.empty }
```

## Test a decision

```fsharp
// registering a fresh user persists RegisterSucceeded
let action = User.handleCommand (cmd (User.Register("alice", "pw"))) User.initialState
test <@ action = PersistEvent (User.RegisterSucceeded("alice", "pw")) @>

// registering an existing user is a deferred rejection
let taken = { User.initialState with Username = Some "alice" }
let action2 = User.handleCommand (cmd (User.Register("alice", "pw"))) taken
test <@ action2 = DeferEvent User.AlreadyRegistered @>
```

## Test the fold

`applyEvent` takes the `Event<_>` envelope; build one (or fold a list to assert the end state):

```fsharp
let evt details : Event<_> =
    { EventDetails = details; CreationDate = System.DateTime.UtcNow
      Id = System.Guid.NewGuid().ToString() |> ValueLens.CreateAsResult |> Result.value
      Sender = None
      CorrelationId = System.Guid.NewGuid().ToString() |> ValueLens.CreateAsResult |> Result.value
      Version = 1L |> ValueLens.TryCreate |> Result.value
      Metadata = Map.empty }

let state = User.applyEvent (evt (User.RegisterSucceeded("alice", "pw"))) User.initialState
test <@ state.Username = Some "alice" @>
```

That is the payoff of keeping the write side pure (see
[Aggregates](../concepts/aggregates.html)): your core business rules are testable in isolation, fast,
and deterministic. Saga `handleEvent`/`applySideEffects` functions are pure in the same way and test
the same way.
