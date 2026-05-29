---
title: 3. Adding a saga
category: Tutorial
categoryindex: 3
index: 4
---

# 3. Adding a saga

Our [aggregate](1-the-aggregate.html) is forbidden from doing anything but decide and emit events.
So when we want *something to happen* after a registration — send a welcome, kick off verification,
notify another service — that work belongs to a [saga](../concepts/sagas.html): a process manager
that reacts to events and issues commands.

A saga is three functions plus a one-line registration. We'll react to `RegisterSucceeded` and send a
welcome notification.

## The saga's states and data

A saga is a small state machine. Ours has two user-defined states; the framework supplies the
`NotStarted`/`Started` preamble itself.

```fsharp
type WelcomeState =
    | SendingWelcome
    | Completed

type SagaData = { Attempts: int }
let initialData = { Attempts = 0 }
```

## Reacting to events: `handleEvent`

`handleEvent` maps an incoming event to the next state. It sees the current state as optional —
`None` before any user state has been entered. We start the workflow when the user registers, and
finish when our notifier reports back.

```fsharp
let handleEvent (event: obj) (sagaState: SagaState<SagaData, WelcomeState option>) : EventAction<WelcomeState> =
    match event, sagaState.State with
    | :? (Event<User.Event>) as e, None ->
        match e.EventDetails with
        | User.RegisterSucceeded _ -> SendingWelcome |> StateChangedEvent
        | _ -> UnhandledEvent
    | :? string as msg, Some SendingWelcome when msg = "sent" ->
        Completed |> StateChangedEvent
    | _ -> UnhandledEvent
```

## Issuing commands: `applySideEffects`

On entering a state, `applySideEffects` decides the **transition** (`Stay`, `NextState`, `StopSaga`)
and the **commands** to issue. Here, entering `SendingWelcome` issues a command to a notifier actor;
`Completed` stops the saga. The `recovering` flag lets us avoid re-issuing the command if the saga is
being rebuilt after a restart.

```fsharp
let applySideEffects (notifier: unit -> IActorRef<obj>)
                     (sagaState: SagaState<SagaData, WelcomeState>) (recovering: bool) =
    match sagaState.State with
    | SendingWelcome ->
        if recovering then Stay, []
        else
            Stay, [ { TargetActor = ActorRef(notifier ())
                      Command = box "welcome alice"
                      DelayInMs = None } ]
    | Completed ->
        StopSaga, []
```

A command can target the originating aggregate (`FactoryAndName { Factory = userFactory; Name =
Originator }`), a named aggregate, an arbitrary `ActorRef` as here, or carry a `DelayInMs` to be
scheduled later — which is how you build retry-with-backoff.

## Folding data: `apply`

The small third function carries cross-step data forward. We bump an attempt counter each time we try
to send.

```fsharp
let apply (sagaState: SagaState<SagaData, WelcomeState>) =
    match sagaState.State with
    | SendingWelcome -> { sagaState with Data = { sagaState.Data with Attempts = sagaState.Data.Attempts + 1 } }
    | _ -> sagaState
```

## Wiring it up

`SagaBuilder.initSimple` binds the three functions, and `InitializeSagaStarter` declares which event
starts the saga — the rule that triggers the [safe start-up handshake](../concepts/sagas.html).

```fsharp
let init (actorApi: IActor) =
    let userFactory = User.factory actorApi
    let notifier () = spawnAnonymous actorApi.System (props notifierBehavior) |> retype
    SagaBuilder.initSimple<SagaData, WelcomeState, User.Event>
        actorApi initialData handleEvent (applySideEffects notifier) apply userFactory "WelcomeSaga"

// in your bootstrap, alongside the aggregate:
actorApi.InitializeSagaStarter (fun evt ->
    match evt with
    | :? (Event<User.Event>) as e ->
        match e.EventDetails with
        | User.RegisterSucceeded _ -> [ fun id -> (init actorApi).RefFor DEFAULT_SHARD id ]
        | _ -> []
    | _ -> [])
```

That is the whole shape: react to an event, issue commands, stop. The
[`saga_sample`](https://github.com/onurgumus/FCQRS/tree/main/saga_sample) project is a complete,
runnable saga (a user-verification flow that sends a real e-mail) you can clone and run.

## You've built the whole loop

Across three chapters you defined an aggregate, wired and ran it with read-your-writes, and reacted to
its events with a saga — the entire FCQRS model. From here, the [How-to guides](../how-to/index.html)
are task-focused recipes, and [Concepts](../concepts/index.html) explains anything you want to go
deeper on.
