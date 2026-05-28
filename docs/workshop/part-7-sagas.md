---
title: Part 7 · Sagas
category: Workshop
categoryindex: 4
index: 8
---

# Part 7 — Sagas: making side effects reliable

We have been strict about one rule since Part 2: an aggregate decides and emits events, but it never
*does* anything to the outside world. No e-mail, no HTTP, no follow-up commands. That restraint is
what keeps the write side replayable. But real systems must eventually act — send the verification
mail, request the approval, charge the card. The component that is allowed to act is the **saga**,
and this part is about how it does so reliably.

A saga is the mirror image of an aggregate. An aggregate turns commands into events; a saga turns
events into commands. It is a small, durable state machine: it wakes on an event, walks through a
sequence of states, and on the way issues commands to other actors. Because its output is *commands*
— which flow through the same persist-and-recover machinery as everything else — its steps inherit
the system's reliability rather than being fire-and-forget side calls.

The simplest saga in our materials is `saga_sample`'s verification flow: a user registers, the saga
generates a code, asks a mail actor to send it, and stops. We will teach with Focument's slightly
richer **approval saga**, because it has enough states to show every moving part. Its lifecycle is
exactly the state machine below.

![A saga is a small state machine](../img/saga-lifecycle.svg)

## Three functions, three jobs

Where an aggregate had two functions (`handleCommand`, `applyEvent`), a saga has three. They map
cleanly onto the diagram: the left edge, the right edge, and the data that rides along.

**`handleEvent`** is the left edge: it receives an incoming event and decides which state to move to.
It returns a `StateChangedEvent` carrying the next state, or `UnhandledEvent` if the event does not
apply here. Note that it sees the current state as *optional* — `None` before the saga has entered
any user-defined state, `Some s` afterwards — because the framework manages a `NotStarted`/`Started`
preamble for you.

```fsharp
// focument/src/Server/DocumentSaga.fs
let handleEvent (event: obj) sagaState =
    match event, sagaState.State with
    | :? Event<Event> as e, _ ->
        match e.EventDetails, sagaState.State with
        | CreatedOrUpdated _, None              -> GeneratingCode |> StateChangedEvent
        | ApprovalCodeSet code, Some GeneratingCode -> SendingNotification code |> StateChangedEvent
        | Event.Approved _, _                   -> State.Approved |> StateChangedEvent
        | Event.Rejected _, _                   -> State.Rejected |> StateChangedEvent
        | _ -> UnhandledEvent
    | _ -> UnhandledEvent
```

```csharp
// focument-csharp/src/Server/DocumentApprovalSaga.cs
public EventAction<ApprovalState> HandleEvent(
    Event<DocumentEvent> evt, ApprovalSagaData data, ApprovalState currentState) =>
    (evt.EventDetails, currentState) switch
    {
        (DocumentEvent.CreatedOrUpdated, null) =>
            StateChanged(new ApprovalState.GeneratingCode()),
        (DocumentEvent.ApprovalCodeSet codeSet, ApprovalState.GeneratingCode) =>
            StateChanged(new ApprovalState.SendingNotification(codeSet.Code)),
        (DocumentEvent.Approved, _) => StateChanged(new ApprovalState.Approved()),
        (DocumentEvent.Rejected, _) => StateChanged(new ApprovalState.Rejected()),
        _ => Unhandled()
    };
```

**`applySideEffects`** is the right edge: given the state the saga is now in, it decides two things —
which **transition** to make (`Stay`, `NextState`, or `StopSaga`) and which **commands** to issue.
This is where the saga reaches out to other actors.

```fsharp
// focument/src/Server/DocumentSaga.fs
let applySideEffects originatorFactory sagaState (recovering: bool) =
    let originator = FactoryAndName { Factory = originatorFactory; Name = Originator }
    match sagaState.State with
    | GeneratingCode ->
        let code = System.Random.Shared.Next(100000, 999999).ToString()
        Stay, [ { TargetActor = originator; Command = Command.SetApprovalCode code; DelayInMs = None } ]
    | SendingNotification code ->
        if recovering then Stay, []
        else NextState (WaitingForApproval code), []
    | WaitingForApproval _ ->
        Stay, [ { TargetActor = originator; Command = Command.Approve; DelayInMs = None } ]
    | State.Approved | State.Rejected -> StopSaga, []
```

```csharp
// focument-csharp/src/Server/DocumentApprovalSaga.cs
SagaSideEffectResult<ApprovalState> ApplySideEffects(
    ApprovalSagaData data, ApprovalState state, bool recovering) =>
    state switch
    {
        ApprovalState.GeneratingCode => new() {
            Transition = Stay(),
            Commands = [ToDocument(new DocumentCommand.SetApprovalCode(GenerateApprovalCode()))] },
        ApprovalState.SendingNotification sending => new() {
            Transition = NextState(new ApprovalState.WaitingForApproval(sending.ApprovalCode)),
            Commands = [] },
        ApprovalState.WaitingForApproval => new() {
            Transition = Stay(),
            Commands = [ToDocument(new DocumentCommand.Approve())] },
        ApprovalState.Approved or ApprovalState.Rejected => new() {
            Transition = StopSaga(), Commands = [] },
        _ => new() { Transition = Stay(), Commands = [] }
    };
```

**`apply`** is the quiet third function: it folds the saga's *data* (the `ApprovalCode`, here)
forward as states change, in just the way `applyEvent` folded the aggregate's state. It is where a
saga remembers things across steps without those things being part of the state-machine label.

## Reading the loop

Trace the diagram against the two functions. A `CreatedOrUpdated` event arrives; `handleEvent` moves
the saga to `GeneratingCode`. Entering that state, `applySideEffects` mints a code and issues a
`SetApprovalCode` command back to the originating document — `Stay`, because it is waiting for the
result. The document persists `ApprovalCodeSet`, which the saga hears; `handleEvent` moves it to
`SendingNotification`. That state issues no command — it just advances to `WaitingForApproval` — and
`WaitingForApproval` issues `Approve`. The document persists `Approved`, the saga moves to its
`Approved` state, and `applySideEffects` returns `StopSaga`, which passivates the saga for good.

Notice how the commands all target `Originator` — the very aggregate whose event started the saga.
A saga discovers its originator from its own name (recall the `originatorId~Saga~correlationId`
convention from the architecture notes), so "send a command back to the document I am shepherding" is
a first-class, one-line operation. Commands can also target a named aggregate, an arbitrary actor (as
`saga_sample` does to reach its mail actor), or carry a `DelayInMs` to be scheduled for later — which
is how you build retry-with-backoff out of the same primitive.

## The `recovering` flag, and why it earns its place

`applySideEffects` is handed a `recovering` boolean, and it is the saga's answer to a sharp question:
*what should happen to side effects when a saga is rebuilt from its events after a restart?*

Remember from Part 4 that state — including saga state — is reconstructed by replaying events. If
replaying a saga naïvely re-ran `applySideEffects` for every state it passed through, a restart would
fire every command again: a second approval, a duplicate e-mail. The `recovering` flag lets the saga
suppress exactly those re-emissions. During recovery you generally return no commands, because the
real commands were already issued in the saga's first life.

The transient `SendingNotification` state is where this gets genuinely subtle, and the two Focument
ports make *different* choices about it — which is itself the lesson. That state issues no command;
its only job is to advance to `WaitingForApproval`. The C# port advances unconditionally, on the
grounds that a state whose sole purpose is to move on must move on even during recovery, or a restart
that lands here would strand the saga forever. The F# port instead `Stay`s while recovering. Both are
defensible, and that they differ tells you this is a real design decision, not boilerplate to copy
without thinking. The framework gives you the flag; what you do with it is domain judgement.

This is the same consistency concern the version number guarded back in Part 4. While a saga and its
aggregate are mid-handshake, FCQRS compares versions to detect that the aggregate restarted
underneath the saga, and aborts the saga rather than letting it act on a stale event. Between that
version check and your use of `recovering`, restarts stop being a source of duplicate or
contradictory side effects.

## Starting and wiring a saga

A saga is registered with a single call that hands the framework its three functions, its initial
data, the originator's factory, and a name.

```fsharp
// focument/src/Server/DocumentSaga.fs
let init actorApi originatorFactory =
    SagaBuilder.initSimple<SagaData, State, Event>
        actorApi { ApprovalCode = None } handleEvent
        (applySideEffects originatorFactory) apply originatorFactory "DocumentSaga"
```

```csharp
// focument-csharp/src/Server/DocumentApprovalSaga.cs
return SagaBuilderCSharp.InitSimple<DocumentEvent, ApprovalSagaData, ApprovalState>(
    actorApi, InitialData, HandleEvent, ApplySideEffects, Apply,
    originatorFactory, "DocumentApprovalSaga");
```

The one piece not shown here is the declaration that connects an event to a saga — "when a
`CreatedOrUpdated` event occurs, start the `DocumentSaga`." That registration, made once at startup,
is what triggers the safe start-up handshake we walked through in Part 2:

![How a saga starts — the coordination handshake](../img/saga-starter.svg)

You write the one-line "this event starts that saga" rule; the framework performs steps 2–5 — pausing
the aggregate, ensuring the saga exists and is subscribed — so the saga never misses the event that
brings it to life. We will see that registration in its natural place next.

In [Part 8](part-8-focument-end-to-end.md) every piece from Parts 3 through 7 is assembled into one
running application, and we follow a single `POST /api/document` from the browser to the database and
back.
