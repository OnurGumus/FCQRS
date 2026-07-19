---
title: Write a saga
category: How-to
categoryindex: 5
index: 8
---

# Write a saga

A saga coordinates work that crosses aggregate boundaries and keeps its progress across restarts. This
example starts when a document is requested, asks the owning user aggregate for a quota slot, and sends
the result back to the document.

Write the workflow as a state table before writing code:

| Current state | Incoming event | Next state | Command issued |
|---|---|---|---|
| not started | `CreateOrUpdateRequested` | `CheckingQuota` | `ConsumeQuota` to user |
| `CheckingQuota` | `QuotaApproved` | `Approving` | `Approve` to document |
| `CheckingQuota` | `QuotaRejected` | `Holding` | `Hold` to document |
| `Approving` | `ApprovedEvt` | `Done` | none; stop saga |
| `Holding` | `HeldForApproval` | `Done` | none; stop saga |

The code has one function for the first three columns and one for the last column.

## Handle events and change state

```fsharp
open FCQRS.Common
open FCQRS.FSharp

type State =
    | CheckingQuota of Username * DocumentId
    | Approving of DocumentId
    | Holding of DocumentId
    | Done

// A saga sees events as obj because they come from both aggregates. Active patterns
// recover the typed payload so the handler matches event + state in one pass.
let private (|DocEvent|_|) (o: obj) =
    match o with
    | :? (Event<Document.Event>) as e -> Some e.EventDetails
    | _ -> None

let private (|UserEvent|_|) (o: obj) =
    match o with
    | :? (Event<User.Event>) as e -> Some e.EventDetails
    | _ -> None

// Map an event and current state to the next stored saga state.
let private handleEvent evt sagaState =
    match evt, sagaState.State with
    | DocEvent(Document.CreateOrUpdateRequested(doc, owner)), None ->
        CheckingQuota(owner, doc.Id) |> StateChangedEvent
    | UserEvent(User.QuotaApproved _), Some(CheckingQuota(_, docId)) ->
        Approving docId |> StateChangedEvent
    | UserEvent User.QuotaRejected, Some(CheckingQuota(_, docId)) ->
        Holding docId |> StateChangedEvent
    | DocEvent(Document.ApprovedEvt _), Some(Approving _) -> Done |> StateChangedEvent
    | DocEvent(Document.HeldForApproval _), Some(Holding _) -> Done |> StateChangedEvent
    | _ -> UnhandledEvent

// Map the current state to a transition and commands.
let private applySideEffects documentFactory userFactory sagaState _recovering =
    match sagaState.State with
    // cross-aggregate command: a specific User instance, by id
    | CheckingQuota(owner, docId) ->
        Stay, [ toAggregate userFactory owner.Value (User.ConsumeQuota docId) ]
    // command back to the originating Document
    | Approving _ -> Stay, [ toOriginator documentFactory Document.Approve ]
    | Holding _ -> Stay, [ toOriginator documentFactory Document.Hold ]
    | Done -> StopSaga, []

// Select the originator events that create a saga instance.
let startsOn (e: Event<Document.Event>) =
    match e.EventDetails with
    | Document.CreateOrUpdateRequested _ -> true
    | _ -> false

let definition documentFactory userFactory =
    { Name = "QuotaSaga"
      InitialData = ()
      Originator = documentFactory
      HandleEvent = handleEvent
      ApplySideEffects = applySideEffects documentFactory userFactory
      StartOn = startsOn
      Snapshots = Default }
```

<div class="cs-alt"></div>

```csharp
// C#: HandleEvent selects a state; ApplySideEffects selects commands.
using Akkling.Cluster.Sharding;
using Microsoft.FSharp.Core;
using static FCQRS.Common;
using static FCQRS.CSharp;

public sealed class QuotaSaga : Saga<DocumentEvent, QuotaSagaData, QuotaState>
{
    private readonly Func<string, IEntityRef<object>> _documents;
    private readonly Func<string, IEntityRef<object>> _users;

    public QuotaSaga(
        Func<string, IEntityRef<object>> documents,
        Func<string, IEntityRef<object>> users)
    {
        _documents = documents;
        _users = users;
    }

    public override QuotaSagaData InitialData => new();
    public override string SagaName => "QuotaSaga";
    public override Func<string, IEntityRef<object>> Originator => _documents;

    // State is None until the first transition.
    public override EventAction<QuotaState> HandleEvent(
        object evt, SagaState<QuotaSagaData, FSharpOption<QuotaState>> sagaState) =>
        (evt, sagaState.State?.Value) switch
        {
            (Event<DocumentEvent> { EventDetails: DocumentEvent.CreateOrUpdateRequested co }, null) =>
                StateChanged(new QuotaState.CheckingQuota(co.Owner, co.Document.Id)),
            (Event<UserEvent> { EventDetails: UserEvent.QuotaApproved }, QuotaState.CheckingQuota s) =>
                StateChanged(new QuotaState.Approving(s.DocId)),
            (Event<UserEvent> { EventDetails: UserEvent.QuotaRejected }, QuotaState.CheckingQuota s) =>
                StateChanged(new QuotaState.Holding(s.DocId)),
            (Event<DocumentEvent> { EventDetails: DocumentEvent.Approved }, QuotaState.Approving) =>
                StateChanged(new QuotaState.Done()),
            (Event<DocumentEvent> { EventDetails: DocumentEvent.HeldForApproval }, QuotaState.Holding) =>
                StateChanged(new QuotaState.Done()),
            _ => Unhandled()
        };

    public override SagaSideEffectResult<QuotaState> ApplySideEffects(
        SagaState<QuotaSagaData, QuotaState> sagaState, bool recovering) =>
        sagaState.State switch
        {
            QuotaState.CheckingQuota s => new()
            {
                Transition = Stay(),
                Commands = [SagaCommands.ToAggregate(_users, s.Owner.ToString(), new UserCommand.ConsumeQuota(s.DocId))]
            },
            QuotaState.Approving => new()
            {
                Transition = Stay(),
                Commands = [SagaCommands.ToOriginator(_documents, new DocumentCommand.Approve())]
            },
            QuotaState.Holding => new()
            {
                Transition = Stay(),
                Commands = [SagaCommands.ToOriginator(_documents, new DocumentCommand.Hold())]
            },
            QuotaState.Done => new() { Transition = StopSaga(), Commands = [] },
            _ => new() { Transition = Stay(), Commands = [] }
        };
}
```

## Register the saga

Register the aggregates first so the saga factory can address them. `wireSagaStarters` combines the
start predicates and enables the [startup handshake](../concepts/sagas.html):

```fsharp
let documents = Fcqrs.aggregate api { Name = "Document"; Initial = Document.initial; Decide = Document.decide; Fold = Document.fold; Snapshots = Default }
let users     = Fcqrs.aggregate api { Name = "User";     Initial = User.initial;     Decide = User.decide;     Fold = User.fold; Snapshots = Default }

let quota = Fcqrs.saga api (definition documents.Factory users.Factory)
Fcqrs.wireSagaStarters api [ quota ]
```

<div class="cs-alt"></div>

```csharp
// C#: create the saga with registered aggregate factories and select its start event.
services
    .AddFcqrs(connString, "FocumentCluster")
    .AddAggregate<DocumentAggregate, DocumentState, DocumentCommand, DocumentEvent>()
    .AddAggregate<UserAggregate, UserState, UserCommand, UserEvent>()
    .AddSaga<QuotaSaga, DocumentEvent, QuotaSagaData, QuotaState>(
        create: sp => new QuotaSaga(
            sp.AggregateFactory<DocumentAggregate>(),
            sp.AggregateFactory<UserAggregate>()),
        startOn: e => e is Event<DocumentEvent> { EventDetails: DocumentEvent.CreateOrUpdateRequested });
```

## Choose command targets

- `toOriginator factory command`: the aggregate that started the saga.
- `toAggregate factory id command`: a specific aggregate instance.
- `toActor actorRef command`: an arbitrary actor reference.
- `toOriginatorAfter factory delayMs taskName command`: a delayed command used for
  retry-with-backoff.

## Make recovery commands retry-safe

After replaying saga state, FCQRS invokes `applySideEffects` with `recovering = true`. The process may
have stopped just before or just after the previous command was delivered. Return an idempotent command
that safely re-drives the state, or use the flag to return a recovery-specific status check. Returning
no commands for every recovered state can strand a workflow whose original command was never delivered.

Aggregate targets should return the existing business result when they receive a repeated command.
External targets should accept an idempotency key and expose a way to check an uncertain result. Add a
timeout for every event the saga waits to receive.

Delayed commands implement retry backoff, but the saga must also have a terminal state for exhausted
retries and any required human intervention.

A complete runnable example is
[`focument_fsharp`](https://github.com/OnurGumus/focument_fsharp). Background: [Sagas](../concepts/sagas.html).
