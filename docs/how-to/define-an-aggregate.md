---
title: Define an aggregate
category: How-to
categoryindex: 5
index: 2
---

# Define an aggregate

An aggregate is three types and two pure functions, bound to an actor with one call.

```fsharp
open FCQRS.Common

type State = { Username: string option; Password: string option }

type Command =
    | Register of string * string
    | Login of string

type Event =
    | RegisterSucceeded of string * string
    | AlreadyRegistered
    | LoginSucceeded
    | LoginFailed

// decide: command + state -> action
let handleCommand (cmd: Command<Command>) state =
    match cmd.CommandDetails, state with
    | Register(u, p), { Username = None } ->
        RegisterSucceeded(u, p) |> PersistEvent
    | Register _, { Username = Some _ } ->
        AlreadyRegistered |> DeferEvent
    | Login p, { Username = Some _; Password = Some s } when p = s ->
        LoginSucceeded |> PersistEvent
    | Login _, _ -> LoginFailed |> DeferEvent

// fold: event -> new state
let applyEvent (event: Event<Event>) state =
    match event.EventDetails with
    | RegisterSucceeded(u, p) ->
        { state with Username = Some u; Password = Some p }
    | _ -> state

let init (actorApi: IActor) =
    actorApi.InitializeActor
        { Username = None; Password = None } "User" handleCommand applyEvent

let factory actorApi entityId =
    (init actorApi).RefFor DEFAULT_SHARD entityId
```

**Return the right action.** `PersistEvent` stores, applies, and publishes the event (and bumps the
version). `DeferEvent` publishes a rejection without storing it — use it for "no" answers like
`AlreadyRegistered` that shouldn't enter history. `IgnoreEvent` does nothing.

**Keep both functions pure.** No I/O, no clock, no side effects — they run on the happy path *and*
during recovery replay, and must produce identical results. Side effects belong in a
[saga](write-a-saga.html).

See [Aggregates and the write side](../concepts/aggregates.html) for the reasoning, and
[Test your domain](test-your-domain.html) to test these two functions directly.
