---
title: Write a saga
category: How-to
categoryindex: 5
index: 4
---

# Write a saga

A saga reacts to events and issues commands. With the `FCQRS.FSharp` facade you write two functions, a
`StartOn` predicate, and bundle them into a `Saga` record. This example mirrors the quota saga from the
[tutorial](../tutorial/3-adding-a-saga.html): it starts from a `Document` request, asks a `User`
aggregate to consume a quota slot, then tells the document to approve or hold.

```fsharp
open FCQRS.Common
open FCQRS.FSharp

type State =
    | CheckingQuota of Username * DocumentId
    | Approving of DocumentId
    | Holding of DocumentId
    | Done

// A saga sees events as obj — they come from BOTH aggregates. Active patterns
// recover the typed payload so the handler matches event + state in one pass.
let private (|DocEvent|_|) (o: obj) =
    match o with
    | :? (Event<Document.Event>) as e -> Some e.EventDetails
    | _ -> None

let private (|UserEvent|_|) (o: obj) =
    match o with
    | :? (Event<User.Event>) as e -> Some e.EventDetails
    | _ -> None

// 1. react to events -> next state
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

// 2. on entering a state, choose a transition and issue commands
let private applySideEffects documentFactory userFactory sagaState _recovering =
    match sagaState.State with
    // cross-aggregate command: a specific User instance, by id
    | CheckingQuota(owner, docId) ->
        Stay, [ toAggregate userFactory owner.Value (User.ConsumeQuota docId) ]
    // command back to the originating Document
    | Approving _ -> Stay, [ toOriginator documentFactory Document.Approve ]
    | Holding _ -> Stay, [ toOriginator documentFactory Document.Hold ]
    | Done -> StopSaga, []

// Which originator events spawn an instance. Typed to the originator's event, so
// the originator-event type is inferred — there is no type argument to get wrong.
let startsOn (e: Event<Document.Event>) =
    match e.EventDetails with
    | Document.CreateOrUpdateRequested _ -> true
    | _ -> false

let definition documentFactory userFactory =
    { Name = "QuotaSaga"
      InitialData = ()               // no cross-step data: progress lives in State
      Originator = documentFactory
      HandleEvent = handleEvent
      ApplySideEffects = applySideEffects documentFactory userFactory
      StartOn = startsOn }
```

<div class="cs-alt"></div>

```csharp
// In C# a saga is a class deriving Saga<OriginatorEvent, Data, State>. It sees
// events as object (from BOTH aggregates), so HandleEvent matches the event type;
// ApplySideEffects returns the transition plus the commands to dispatch.
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

    // 1. react to events -> next state (state is None until the first transition)
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

    // 2. on entering a state -> transition + commands (ToAggregate / ToOriginator)
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

Register it from your composition root, after the aggregates it references — this is what fires the
safe [start-up handshake](../concepts/sagas.html):

```fsharp
let documents = Fcqrs.aggregate api { Name = "Document"; Initial = Document.initial; Decide = Document.decide; Fold = Document.fold }
let users     = Fcqrs.aggregate api { Name = "User";     Initial = User.initial;     Decide = User.decide;     Fold = User.fold }

let quota = Fcqrs.saga api (definition documents.Factory users.Factory)
Fcqrs.wireSagaStarters api [ quota ]
```

<div class="cs-alt"></div>

```csharp
// C#: register both aggregates and the saga via the DI host-builder. AddSaga takes
// a factory (it receives the aggregates' already-registered factories) and the
// predicate for which originator event starts it.
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

**Command builders.** The facade turns side-effect commands into one-liners instead of hand-rolled
`TargetActor` records:

- `toOriginator factory cmd` — to the aggregate that started the saga.
- `toAggregate factory id cmd` — to a specific aggregate instance, by id (cross-aggregate).
- `toActor actorRef cmd` — to an arbitrary actor ref.
- `toOriginatorAfter factory delayMs taskName cmd` — a delayed command, which is how you build
  retry-with-backoff.

**Recovery:** use the `recovering` flag (the last argument to `applySideEffects`) to avoid re-issuing
real-world effects when the saga replays after a restart. A complete runnable example is
[`focument_fsharp`](https://github.com/OnurGumus/focument_fsharp). Concept: [Sagas](../concepts/sagas.html).
