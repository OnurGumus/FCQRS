---
title: Write a saga
category: Apply
categoryindex: 4
index: 5
---

# Write a saga

This recipe coordinates publication across two aggregates. A document asks to publish under a slug;
the aggregate identified by that slug owns the uniqueness rule. The saga reserves the slug, then
reports one publication result to the originating document.

Read [Sagas](../concepts/sagas.html) first if you need the ground-up explanation of transitions,
`SagaStartingEvent`, the starter handshake, and recovery re-drive.

> **Motivation:** Use a saga here because the publication rule crosses two independent owners and the
> conversation must survive a process restart.

## Write the state table first

| Current state | Incoming event | Next state | Command issued |
|---|---|---|---|
| not started | `PublicationRequested` | `ReservingSlug` | `Reserve` to slug |
| `ReservingSlug` | `SlugReserved` | `ReportingResult Published` | `FinishPublication Published` to document |
| `ReservingSlug` | `SlugUnavailable` | `ReportingResult Rejected` | `FinishPublication Rejected` to document |
| `ReportingResult result` | `PublicationFinished result` | `Done` | none; stop saga |

The implementation has one function for the first three columns and another for the last column.

> **Motivation:** Writing the table first exposes missing outcomes and accidental loops before routing,
> persistence, or language syntax can hide them.

## Map incoming events to persisted states

`handleEvent` receives events from every participant as `obj`. Match the typed envelope and current
saga state together. The state is `None` when the starting event first reaches user code.

```fsharp
open FCQRS.Common
open FCQRS.FSharp

type State =
    | ReservingSlug of DocumentId * string
    | ReportingResult of Document.PublicationResult
    | Done

let private (|DocumentEvent|_|) (message: obj) =
    match message with
    | :? Event<Document.Event> as event -> Some event.EventDetails
    | _ -> None

let private (|SlugEvent|_|) (message: obj) =
    match message with
    | :? Event<Slug.Event> as event -> Some event.EventDetails
    | _ -> None

let private handleEvent message sagaState =
    match message, sagaState.State with
    | DocumentEvent(Document.PublicationRequested(docId, slug)), None ->
        ReservingSlug(docId, slug) |> StateChangedEvent
    | SlugEvent(Slug.SlugReserved _), Some(ReservingSlug _) ->
        ReportingResult Document.Published |> StateChangedEvent
    | SlugEvent(Slug.SlugUnavailable _), Some(ReservingSlug _) ->
        ReportingResult Document.Rejected |> StateChangedEvent
    | DocumentEvent(Document.PublicationFinished(_, _, result)),
        Some(ReportingResult expected) when result = expected ->
        Done |> StateChangedEvent
    | _ -> UnhandledEvent
```

`StateChangedEvent next` stores the next saga state. `UnhandledEvent` rejects an event that does not
belong in the current state. Do not issue commands from this function; state persistence must complete
first.

## Map persisted states to commands

`applySideEffects` runs after the state is stored and again after recovery. Return commands plus the
transition FCQRS should make after issuing them.

```fsharp
let private applySideEffects documentFactory slugFactory sagaState _recovering =
    match sagaState.State with
    | ReservingSlug(docId, slug) ->
        Stay, [ toAggregate slugFactory slug (Slug.Reserve docId) ]
    | ReportingResult result ->
        Stay, [ toOriginator documentFactory (Document.FinishPublication result) ]
    | Done ->
        StopSaga, []
```

The command helpers select a target:

- `toOriginator factory command`: the exact aggregate instance whose event started the saga;
- `toAggregate factory id command`: another aggregate instance selected by id;
- `toActor actorRef command`: an arbitrary actor reference;
- `toSelf command`: a message back to the saga itself (it lands in `handleEvent`);
- `toOriginatorAfter factory delayMs taskName command` / `toAggregateAfter` / `toSelfAfter`: delayed
  variants for timeout or retry behaviour. A `toSelfAfter` reminder is the idiomatic saga timeout:
  enter a state, schedule a wake-up, and let `handleEvent` decide whether it still matters.

The returned saga transition means:

- `Stay`: keep waiting in the current state after sending commands;
- `NextState next`: persist another state immediately and run its side effects;
- `StopSaga`: send any returned commands, then complete and passivate. Delayed commands returned with
  `StopSaga` are still delivered — except `toSelfAfter` ones, which are cancelled with a warning so a
  completed saga cannot resurrect itself.

## Declare the start event

`StartOn` answers “which originator event creates one new instance of this saga?” Match only the event
that begins the workflow.

```fsharp
let private startsOn (event: Event<Document.Event>) =
    match event.EventDetails with
    | Document.PublicationRequested _ -> true
    | _ -> false

let definition documentFactory slugFactory =
    { Name = "PublicationSaga"
      InitialData = ()
      Originator = documentFactory
      HandleEvent = handleEvent
      ApplySideEffects = applySideEffects documentFactory slugFactory
      StartOn = startsOn
      Snapshots = Default }
```

`Originator` supplies the aggregate factory used by the starting handshake and by `toOriginator`.
`InitialData` supplies fixed data available to the saga functions. Current workflow progress belongs in
the state-machine cases; use `unit` when no additional fixed data is needed.

Do not construct `SagaStartingEvent` yourself. FCQRS creates and stores that runtime envelope from the
event accepted by `StartOn`.

> **Motivation:** The start rule lets FCQRS subscribe the saga before the originator publishes the one
> event that begins the workflow. Without that handshake, the new saga could miss its first event.

## Register the saga and starter rules

Register participant aggregates before constructing the saga, then wire every saga start rule once:

```fsharp
let documents =
    Fcqrs.aggregate api
        { Name = "Document"; Initial = Document.initial
          Decide = Document.decide; Fold = Document.fold; Snapshots = Default }

let slugs =
    Fcqrs.aggregate api
        { Name = "Slug"; Initial = Slug.initial
          Decide = Slug.decide; Fold = Slug.fold; Snapshots = Default }

let publication = Fcqrs.saga api (definition documents.Factory slugs.Factory)
Fcqrs.wireSagaStarters api [ publication ]
```

`wireSagaStarters` is not optional. It installs the predicates and the safe-start handshake that
subscribes a new saga before the originator publishes its starting event.

## C# equivalent

Derive from `Saga<TOriginatorEvent,TData,TState>`. `HandleEvent` returns persisted state actions;
`ApplySideEffects` returns commands and a saga transition. The `startOn` predicate belongs in
registration rather than on the class. `HandleEvent` takes `object` deliberately: a saga also
receives other aggregates' reply events and `ToSelf` timeout payloads. The typed
`SagaApi.InitSimple` shortcut only delivers the originator's events, so it cannot express timeouts
or multi-aggregate coordination; those sagas belong on this base class.

```csharp
public abstract record PublicationState
{
    public sealed record ReservingSlug(DocumentId DocumentId, string Slug) : PublicationState;
    public sealed record ReportingResult(PublicationResult Result) : PublicationState;
    public sealed record Done : PublicationState;
}

public sealed record PublicationData;

public sealed class PublicationSaga
    : Saga<DocumentEvent, PublicationData, PublicationState>
{
    private readonly Func<string, IEntityRef<object>> _documents;
    private readonly Func<string, IEntityRef<object>> _slugs;

    public PublicationSaga(
        Func<string, IEntityRef<object>> documents,
        Func<string, IEntityRef<object>> slugs)
    {
        _documents = documents;
        _slugs = slugs;
    }

    public override PublicationData InitialData => new();
    public override string SagaName => "PublicationSaga";
    public override Func<string, IEntityRef<object>> Originator => _documents;

    public override EventAction<PublicationState> HandleEvent(
        object message,
        SagaState<PublicationData, FSharpOption<PublicationState>> sagaState) =>
        (message, sagaState.State?.Value) switch
        {
            (Event<DocumentEvent>
                { EventDetails: DocumentEvent.PublicationRequested requested }, null) =>
                StateChanged(new PublicationState.ReservingSlug(
                    requested.DocumentId, requested.Slug)),

            (Event<SlugEvent>
                { EventDetails: SlugEvent.SlugReserved reserved },
                PublicationState.ReservingSlug expected)
                when reserved.DocumentId == expected.DocumentId =>
                StateChanged(new PublicationState.ReportingResult(
                    PublicationResult.Published)),

            (Event<SlugEvent>
                { EventDetails: SlugEvent.SlugUnavailable unavailable },
                PublicationState.ReservingSlug expected)
                when unavailable.DocumentId == expected.DocumentId =>
                StateChanged(new PublicationState.ReportingResult(
                    PublicationResult.Rejected)),

            (Event<DocumentEvent>
                { EventDetails: DocumentEvent.PublicationFinished finished },
                PublicationState.ReportingResult reporting)
                when finished.Result == reporting.Result =>
                StateChanged(new PublicationState.Done()),

            _ => Unhandled()
        };

    public override SagaSideEffectResult<PublicationState> ApplySideEffects(
        SagaState<PublicationData, PublicationState> sagaState,
        bool recovering) =>
        sagaState.State switch
        {
            PublicationState.ReservingSlug s => new()
            {
                Transition = Stay(),
                Commands = [SagaCommands.ToAggregate(
                    _slugs, s.Slug, new SlugCommand.Reserve(s.DocumentId))]
            },
            PublicationState.ReportingResult s => new()
            {
                Transition = Stay(),
                Commands = [SagaCommands.ToOriginator(
                    _documents, new DocumentCommand.FinishPublication(s.Result))]
            },
            PublicationState.Done _ => new()
            {
                Transition = StopSaga(),
                Commands = []
            },
            _ => new() { Transition = Stay(), Commands = [] }
        };
}
```

Register it with both factories and the safe start predicate:

```csharp
services
    .AddFcqrs(connectionString, "Documents")
    .AddAggregate<PublicationDocumentAggregate>()
    .AddAggregate<SlugAggregate>()
    .AddSaga<PublicationSaga, DocumentEvent, PublicationData, PublicationState>(
        create: sp => new PublicationSaga(
            sp.AggregateFactory<PublicationDocumentAggregate>(),
            sp.AggregateFactory<SlugAggregate>()),
        startOn: e => e is Event<DocumentEvent>
            { EventDetails: DocumentEvent.PublicationRequested });
```

The host builder wires the saga-starter automatically from all registered sagas at startup — there is
no C# counterpart of `wireSagaStarters` to call. Note also the signature asymmetry: `HandleEvent`
receives the saga state as an `FSharpOption` (`None` before the first user state exists), while
`ApplySideEffects` runs only once a user state exists and so receives it directly.

## Make recovery commands safe

After reconstructing saga state, FCQRS invokes `applySideEffects` with `recovering = true`. Delivery of
the previous command is uncertain, so each waiting state must do one of the following:

- resend an idempotent command;
- query an external operation by a stable idempotency key;
- issue a recovery-specific reconciliation command;
- move to an explicit failed or manual-resolution path.

> **Motivation:** Recovery repeats the next intended action because the journal can prove the stored
> state, but it cannot prove whether an outgoing message crossed the process boundary before failure.

In this example, reserving the same slug for the same document returns the existing reservation, and
repeating `FinishPublication` returns the existing result without storing a duplicate. The normal
commands are therefore safe to issue again.

Do not return no command merely because `recovering` is true. If the process stopped before delivery,
that leaves the workflow waiting forever. Add a timeout for every event that may never arrive.

The complete runnable version is chapter 3 of the
[tutorial](../tutorial/3-adding-a-saga.html). Use [Test your domain](test-your-domain.html) to test the
event-to-state and state-to-command functions independently.
