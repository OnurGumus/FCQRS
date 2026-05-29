---
title: Write a saga
category: How-to
categoryindex: 5
index: 4
---

# Write a saga

A saga reacts to events and issues commands. Write three functions and register which event starts it.

```fsharp
open FCQRS.Common

type State = GeneratingCode | SendingMail of string | Completed
type SagaData = { RetryCount: int }

// 1. react to events -> next state
let handleEvent (event: obj) (sagaState: SagaState<SagaData, State option>) : EventAction<State> =
    match event, sagaState.State with
    | :? (Event<User.Event>) as e, None ->
        match e.EventDetails with
        | User.VerificationRequested _ -> GeneratingCode |> StateChangedEvent
        | _ -> UnhandledEvent
    | :? string as m, Some (SendingMail _) when m = "sent" -> Completed |> StateChangedEvent
    | _ -> UnhandledEvent

// 2. on entering a state, choose a transition and issue commands
let applySideEffects (userFactory: string -> Akkling.Cluster.Sharding.IEntityRef<obj>)
                     (mail: unit -> IActorRef<obj>)
                     (sagaState: SagaState<SagaData, State>) (recovering: bool) =
    match sagaState.State with
    | GeneratingCode ->
        let code = System.Random.Shared.Next(100000, 999999).ToString()
        // command back to the originating aggregate
        Stay, [ { TargetActor = FactoryAndName { Factory = userFactory; Name = Originator }
                  Command = User.SetVerificationCode code
                  DelayInMs = None } ]
    | SendingMail body ->
        if recovering then Stay, []                       // don't re-send on recovery
        else Stay, [ { TargetActor = ActorRef(mail ()); Command = box body; DelayInMs = None } ]
    | Completed -> StopSaga, []

// 3. fold cross-step data
let apply (sagaState: SagaState<SagaData, State>) = sagaState

let init actorApi =
    let userFactory = User.factory actorApi
    let mail () = spawnAnonymous actorApi.System (props mailBehavior) |> retype
    SagaBuilder.initSimple<SagaData, State, User.Event>
        actorApi { RetryCount = 0 } handleEvent (applySideEffects userFactory mail) apply userFactory "UserSaga"
```

Register the trigger in your bootstrap — this is what fires the safe
[start-up handshake](../concepts/sagas.html):

```fsharp
actorApi.InitializeSagaStarter (fun evt ->
    match evt with
    | :? (Event<User.Event>) as e ->
        match e.EventDetails with
        | User.VerificationRequested _ -> [ init actorApi |> fun fac id -> fac.RefFor DEFAULT_SHARD id ]
        | _ -> []
    | _ -> [])
```

**Targets:** a command can go to the `Originator` (the aggregate that started the saga), a named
aggregate, an arbitrary `ActorRef`, or carry `DelayInMs` to be scheduled later (retry/backoff).
**Recovery:** use the `recovering` flag to avoid re-issuing real-world effects when the saga replays.
A complete runnable example is the
[`saga_sample`](https://github.com/onurgumus/FCQRS/tree/main/saga_sample) project. Concept:
[Sagas](../concepts/sagas.html).
