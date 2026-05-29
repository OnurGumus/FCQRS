---
title: Test your domain
category: How-to
categoryindex: 5
index: 6
---

# Test your domain

The valuable logic — `handleCommand` and `applyEvent` — is pure and has no dependency on Akka.NET, so
you test it with plain function calls. No actor system, no database, no async.

You only need to wrap your command payload in the `Command<_>` envelope the handler expects. A couple
of tiny helpers keep tests readable:

```fsharp
open FCQRS.Common
open FCQRS.Model.Data

let newId () =
    System.Guid.NewGuid().ToString()
    |> ValueLens.CreateAsResult |> Result.value

let cmd details : Command<_> =
    { CommandDetails = details
      CreationDate = System.DateTime.UtcNow
      Id = newId ()
      Sender = None
      CorrelationId = newId ()
      Metadata = Map.empty }
```

## Test a decision

```fsharp
// registering a fresh user persists RegisterSucceeded
let fresh = cmd (User.Register("alice", "pw"))
let action = User.handleCommand fresh User.initialState
test <@ action = PersistEvent (User.RegisterSucceeded("alice", "pw")) @>

// registering an existing user is a deferred rejection
let taken = { User.initialState with Username = Some "alice" }
let action2 = User.handleCommand fresh taken
test <@ action2 = DeferEvent User.AlreadyRegistered @>
```

## Test the fold

`applyEvent` takes the `Event<_>` envelope; build one (or fold a list to assert the end state):

```fsharp
let evt details : Event<_> =
    { EventDetails = details
      CreationDate = System.DateTime.UtcNow
      Id = newId ()
      Sender = None
      CorrelationId = newId ()
      Version = 1L |> ValueLens.TryCreate |> Result.value
      Metadata = Map.empty }

let registered = evt (User.RegisterSucceeded("alice", "pw"))
let state = User.applyEvent registered User.initialState
test <@ state.Username = Some "alice" @>
```

That is the payoff of keeping the write side pure (see
[Aggregates](../concepts/aggregates.html)): your core business rules are testable in isolation, fast,
and deterministic. Saga `handleEvent`/`applySideEffects` functions are pure in the same way and test
the same way.
