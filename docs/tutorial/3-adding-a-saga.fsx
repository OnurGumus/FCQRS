(**
---
title: 3. Adding a saga
category: Learn FCQRS
categoryindex: 2
index: 5
---
*)

(*** hide ***)
#r "nuget: FCQRS, 6.0.0-rc1"
open System
open FCQRS.Common
open FCQRS.Model.Data
open FCQRS.FSharp

(**
# 3. Adding a saga

The document application can now store and project documents. This chapter adds one cross-aggregate
rule: a document may be published under a URL slug only when the aggregate identified by that slug
reserves it for that document.

This example is intentionally small. The only new problem is durable coordination, so the saga
mechanics remain visible.

> **Course position:** chapter 2 completed work owned by one aggregate and projected its event. This
> chapter handles one rule with two independent owners. By the end you will be able to derive saga
> state from events, issue commands only after progress is stored, and explain safe startup and
> resumption.

## The rule belongs to two owners

The document aggregate owns whether one document is ready to publish. A slug aggregate owns whether
one URL slug is available. Neither aggregate can make both decisions from its own state.

The saga coordinates this conversation:

<pre>
Document                          Publication saga              Slug[guides/fcqrs]
   |                                      |                                       |
   |-- PublicationRequested ------------->|                                       |
   |                                      |-- Reserve(documentId) --------------->|
   |                                      |<-- SlugReserved / Unavailable --------|
   |<-- FinishPublication(result) --------|                                       |
   |                                      |                                       |
   |-- PublicationFinished -------------->|  StopSaga                             |
</pre>

The target aggregates still own every business decision. The saga owns only the progress of this
conversation.

> **Motivation:** Keeping each rule with its owner prevents the saga from becoming a second, stale copy
> of document and slug state. The saga coordinates answers; it does not invent them.

## Keep the domain types small

Chapter 1 already covered document content. This chapter models only the publication status, because
that is the part involved in the cross-aggregate conversation. Validate the slug at the application
boundary before sending `Publish`.
*)

module Values =
    type DocumentId =
        | DocumentId of Guid
        static member OfGuid value = DocumentId value
        member this.Value = let (DocumentId value) = this in value
        override this.ToString() = let (DocumentId value) = this in value.ToString()

(**
<div class="cs-alt"></div>

```csharp
public readonly record struct DocumentId(Guid Value)
{
    public static DocumentId OfGuid(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}
```

## Step 1: let the document request publication

The document stores `PublicationRequested` before any reservation begins. Its state records the
document and slug involved, so the eventual result remains valid after recovery.
*)

module Document =
    open Values

    type PublicationResult =
        | Published
        | Rejected

    type State =
        | Draft
        | WaitingForSlug of DocumentId * slug: string
        | Finished of DocumentId * slug: string * PublicationResult

    let initial = Draft

    type Command =
        | Publish of DocumentId * slug: string
        | FinishPublication of PublicationResult

    type Event =
        | PublicationRequested of DocumentId * slug: string
        | PublicationFinished of DocumentId * slug: string * PublicationResult

    let decide (cmd: Command<Command>) state =
        match cmd.CommandDetails, state with
        | Publish(documentId, slug), Draft ->
            PublicationRequested(documentId, slug) |> PersistEvent
        | Publish(documentId, slug), WaitingForSlug(currentId, currentSlug)
            when documentId = currentId && slug = currentSlug ->
            PublicationRequested(documentId, slug) |> DeferEvent
        | FinishPublication result, WaitingForSlug(documentId, slug) ->
            PublicationFinished(documentId, slug, result) |> PersistEvent
        | FinishPublication result, Finished(documentId, slug, current) when result = current ->
            PublicationFinished(documentId, slug, result) |> DeferEvent
        | _ -> UnhandledEvent

    let fold (event: Event<Event>) state =
        match event.EventDetails with
        | PublicationRequested(documentId, slug) -> WaitingForSlug(documentId, slug)
        | PublicationFinished(documentId, slug, result) -> Finished(documentId, slug, result)

(**
`FinishPublication` carries either `Published` or `Rejected`. Repeating the same result returns the
same `PublicationFinished` outcome with `DeferEvent`, so recovery can reissue one saga command without
storing another domain event. Keeping the result as data removes duplicate success and failure
branches from the aggregate.

The four decision cases form two pairs: the first request is stored and its retry is deferred; the
first result is stored and its retry is deferred. The final wildcard rejects every command and state
combination that does not belong to this workflow.

<div class="cs-alt"></div>

```csharp
public enum PublicationResult { Published, Rejected }

public abstract record PublicationDocumentState
{
    public sealed record Draft : PublicationDocumentState;
    public sealed record WaitingForSlug(DocumentId DocumentId, string Slug)
        : PublicationDocumentState;
    public sealed record Finished(
        DocumentId DocumentId, string Slug, PublicationResult Result)
        : PublicationDocumentState;

    public static readonly PublicationDocumentState Initial = new Draft();
}

public union DocumentCommand(DocumentCommand.Publish, DocumentCommand.FinishPublication)
{
    public record Publish(DocumentId DocumentId, string Slug);
    public record FinishPublication(PublicationResult Result);
}

public union DocumentEvent(DocumentEvent.PublicationRequested, DocumentEvent.PublicationFinished)
{
    public record PublicationRequested(DocumentId DocumentId, string Slug);
    public record PublicationFinished(
        DocumentId DocumentId, string Slug, PublicationResult Result);
}

public sealed class PublicationDocumentAggregate
    : Aggregate<PublicationDocumentState, DocumentCommand, DocumentEvent>
{
    public override PublicationDocumentState InitialState => PublicationDocumentState.Initial;
    public override string EntityName => "Document";

    public override EventAction<DocumentEvent> HandleCommand(
        Command<DocumentCommand> command, PublicationDocumentState state) =>
        (command.CommandDetails, state) switch
        {
            (DocumentCommand.Publish request, PublicationDocumentState.Draft) =>
                EventActions.Persist<DocumentEvent>(
                    new DocumentEvent.PublicationRequested(request.DocumentId, request.Slug)),
            (DocumentCommand.Publish request, PublicationDocumentState.WaitingForSlug waiting)
                when request.DocumentId == waiting.DocumentId && request.Slug == waiting.Slug =>
                EventActions.Defer<DocumentEvent>(
                    new DocumentEvent.PublicationRequested(request.DocumentId, request.Slug)),
            (DocumentCommand.FinishPublication finish,
                PublicationDocumentState.WaitingForSlug waiting) =>
                EventActions.Persist<DocumentEvent>(new DocumentEvent.PublicationFinished(
                    waiting.DocumentId, waiting.Slug, finish.Result)),
            (DocumentCommand.FinishPublication finish,
                PublicationDocumentState.Finished current) when finish.Result == current.Result =>
                EventActions.Defer<DocumentEvent>(new DocumentEvent.PublicationFinished(
                    current.DocumentId, current.Slug, current.Result)),
            _ => EventActions.Ignore<DocumentEvent>()
        };

    public override PublicationDocumentState ApplyEvent(
        Event<DocumentEvent> stored, PublicationDocumentState state) =>
        stored.EventDetails switch
        {
            DocumentEvent.PublicationRequested requested =>
                new PublicationDocumentState.WaitingForSlug(
                    requested.DocumentId, requested.Slug),
            DocumentEvent.PublicationFinished finished =>
                new PublicationDocumentState.Finished(
                    finished.DocumentId, finished.Slug, finished.Result),
            _ => state
        };
}
```

## Step 2: let each slug protect its own uniqueness

The slug aggregate is addressed by the slug text. Its entire state is the document that owns the
reservation, if any.
*)

module Slug =
    open Values

    type State = { ReservedFor: DocumentId option }
    let initial = { ReservedFor = None }

    type Command = Reserve of DocumentId

    type Event =
        | SlugReserved of DocumentId
        | SlugUnavailable of DocumentId

    let decide (cmd: Command<Command>) state =
        match cmd.CommandDetails, state.ReservedFor with
        | Reserve documentId, None ->
            SlugReserved documentId |> PersistEvent
        | Reserve documentId, Some current when current = documentId ->
            SlugReserved documentId |> DeferEvent
        | Reserve documentId, Some _ ->
            SlugUnavailable documentId |> DeferEvent

    let fold (event: Event<Event>) state =
        match event.EventDetails with
        | SlugReserved documentId -> { ReservedFor = Some documentId }
        | SlugUnavailable _ -> state

(**
The first reservation becomes durable. Repeating it for the same document returns the existing result.
A different document receives `SlugUnavailable` without changing the owner. Both repeated paths are
safe when a saga resumes after uncertain delivery.

<div class="cs-alt"></div>

```csharp
public sealed record SlugState(DocumentId? ReservedFor = null)
{
    public static readonly SlugState Initial = new();
}

public union SlugCommand(SlugCommand.Reserve)
{
    public record Reserve(DocumentId DocumentId);
}

public union SlugEvent(SlugEvent.SlugReserved, SlugEvent.SlugUnavailable)
{
    public record SlugReserved(DocumentId DocumentId);
    public record SlugUnavailable(DocumentId DocumentId);
}

public sealed class SlugAggregate : Aggregate<SlugState, SlugCommand, SlugEvent>
{
    public override SlugState InitialState => SlugState.Initial;
    public override string EntityName => "Slug";

    public override EventAction<SlugEvent> HandleCommand(
        Command<SlugCommand> command, SlugState state) =>
        (command.CommandDetails, state.ReservedFor) switch
        {
            (SlugCommand.Reserve reserve, null) =>
                EventActions.Persist<SlugEvent>(new SlugEvent.SlugReserved(reserve.DocumentId)),
            (SlugCommand.Reserve reserve, DocumentId owner) when owner == reserve.DocumentId =>
                EventActions.Defer<SlugEvent>(new SlugEvent.SlugReserved(reserve.DocumentId)),
            (SlugCommand.Reserve reserve, _) =>
                EventActions.Defer<SlugEvent>(new SlugEvent.SlugUnavailable(reserve.DocumentId)),
            _ => EventActions.Ignore<SlugEvent>()
        };

    public override SlugState ApplyEvent(Event<SlugEvent> stored, SlugState state) =>
        stored.EventDetails is SlugEvent.SlugReserved reserved
            ? new SlugState(reserved.DocumentId)
            : state;
}
```

## Step 3: write the saga as a table

Before writing the saga functions, write every accepted event and resulting command:

| Current state | Incoming event | Stored next state | Command after storage |
|---|---|---|---|
| not started | `PublicationRequested` | `ReservingSlug` | `Reserve` to slug |
| `ReservingSlug` | `SlugReserved` | `ReportingResult Published` | `FinishPublication Published` to document |
| `ReservingSlug` | `SlugUnavailable` | `ReportingResult Rejected` | `FinishPublication Rejected` to document |
| `ReportingResult result` | `PublicationFinished result` | `Done` | stop |

This table separates two questions:

1. Given an event and current saga state, what state should be stored?
2. Once that state is durable, which command should be sent?

> **Motivation:** The split ensures FCQRS can store what the saga intends to do before it sends a
> command that may be delivered just as the process fails.
*)

module PublicationSaga =
    open Values

    type State =
        | ReservingSlug of DocumentId * string
        | ReportingResult of Document.PublicationResult
        | Done

(**
<div class="cs-alt"></div>

```csharp
public abstract record PublicationState
{
    public sealed record ReservingSlug(DocumentId DocumentId, string Slug) : PublicationState;
    public sealed record ReportingResult(PublicationResult Result) : PublicationState;
    public sealed record Done : PublicationState;
}

public sealed record PublicationData;
```

### Function 1: event plus state becomes persisted progress

Saga events arrive as `obj` because several aggregate event types share the same workflow. Active
patterns recover their typed envelopes. The first domain event reaches user code with
`sagaState.State = None`; no user-defined state exists until that event is accepted.
*)

    let private (|DocumentEvent|_|) (message: obj) =
        match message with
        | :? Event<Document.Event> as event -> Some event.EventDetails
        | _ -> None

    let private (|SlugEvent|_|) (message: obj) =
        match message with
        | :? Event<Slug.Event> as event -> Some event.EventDetails
        | _ -> None

    let handleEvent message sagaState =
        match message, sagaState.State with
        | DocumentEvent(Document.PublicationRequested(documentId, slug)), None ->
            ReservingSlug(documentId, slug) |> StateChangedEvent
        | SlugEvent(Slug.SlugReserved documentId), Some(ReservingSlug(expected, _))
            when documentId = expected ->
            ReportingResult Document.Published |> StateChangedEvent
        | SlugEvent(Slug.SlugUnavailable documentId), Some(ReservingSlug(expected, _))
            when documentId = expected ->
            ReportingResult Document.Rejected |> StateChangedEvent
        | DocumentEvent(Document.PublicationFinished(_, _, result)), Some(ReportingResult expected)
            when result = expected ->
            Done |> StateChangedEvent
        | _ -> UnhandledEvent

(**
<div class="cs-alt"></div>

```csharp
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
```

`StateChangedEvent next` is the saga counterpart to a persisted aggregate outcome: FCQRS stores the
new workflow state. `UnhandledEvent` means the incoming event is not valid for the current state.

### Function 2: persisted state becomes commands

FCQRS calls `applySideEffects` only after a state change is stored. It calls the same function after
recovery, with `recovering = true`.
*)

    let applySideEffects documentFactory slugFactory sagaState _recovering =
        match sagaState.State with
        | ReservingSlug(documentId, slug) ->
            Stay, [ toAggregate slugFactory slug (Slug.Reserve documentId) ]
        | ReportingResult result ->
            Stay, [ toOriginator documentFactory (Document.FinishPublication result) ]
        | Done ->
            StopSaga, []

(**
<div class="cs-alt"></div>

```csharp
public override SagaSideEffectResult<PublicationState> ApplySideEffects(
    SagaState<PublicationData, PublicationState> sagaState,
    bool _recovering) =>
    sagaState.State switch
    {
        PublicationState.ReservingSlug state => new()
        {
            Transition = Stay(),
            Commands = [SagaCommands.ToAggregate(
                _slugs, state.Slug, new SlugCommand.Reserve(state.DocumentId))]
        },
        PublicationState.ReportingResult state => new()
        {
            Transition = Stay(),
            Commands = [SagaCommands.ToOriginator(
                _documents, new DocumentCommand.FinishPublication(state.Result))]
        },
        PublicationState.Done => new()
        {
            Transition = StopSaga(),
            Commands = []
        },
        _ => new() { Transition = Stay(), Commands = [] }
    };
```

The C# constructor in Step 4 supplies `_documents` and `_slugs`. As in F#, the method receives the
recovery flag even though this retry-safe workflow sends the same command during live processing and
recovery.

`Stay` keeps the stored state while the saga waits for a reply. `StopSaga` completes and passivates the
saga. `NextState next` is the third available transition; it persists another state immediately when a
step should advance without waiting for an event. This workflow does not need it.

The state is always stored before its commands are issued:

<pre>
incoming event
    -> handleEvent
    -> persist StateChangedEvent
    -> applySideEffects
    -> send commands
</pre>

That ordering is what makes the next action recoverable.

## Step 4: declare how the saga starts

`StartOn` selects the originator event that creates one workflow instance. `Originator` identifies the
aggregate family that produced it and lets `toOriginator` route back to the exact document.

> **Motivation:** A saga cannot subscribe before it exists. Declaring the exact start event gives FCQRS
> a safe point to create and subscribe the saga before that event is released.
*)

    let startsOn (event: Event<Document.Event>) =
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

(**
<div class="cs-alt"></div>

```csharp
public sealed class PublicationSaga
    : Saga<DocumentEvent, PublicationData, PublicationState>
{
    readonly Func<string, IEntityRef<object>> _documents;
    readonly Func<string, IEntityRef<object>> _slugs;

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

    public static bool StartsOn(object message) =>
        message is Event<DocumentEvent>
            { EventDetails: DocumentEvent.PublicationRequested };

    // HandleEvent and ApplySideEffects are the methods shown in Step 3.
}
```

The domain publishes `PublicationRequested`; application code does not create `SagaStartingEvent`.
FCQRS wraps the matched event internally so the new saga remembers its originator, starting version,
correlation context, and the event that created it.

The safe-start sequence is:

1. the document chooses `PublicationRequested`;
2. `StartOn` says the event requires `PublicationSaga`;
3. FCQRS creates and subscribes the saga;
4. the saga stores its starting envelope;
5. the document event is persisted and published;
6. `handleEvent` stores `ReservingSlug`;
7. only then does `applySideEffects` send `Reserve`.

Without this handshake, the first event could be published before the new saga was listening.

The C# API uses the same two-function split shown beside the F# code. `HandleEvent` stores progress;
`ApplySideEffects` returns commands and `Stay`, `NextState`, or `StopSaga`. The document and slug
aggregates use the `HandleCommand` and `ApplyEvent` pattern taught in chapter 1.

## Wire the participants and starter

Register both aggregates first, then construct the saga from their factories. The final call installs
the start predicate and handshake.
*)

let wire (api: IActor) =
    let documents =
        Fcqrs.aggregate api
            { Name = "Document"
              Initial = Document.initial
              Decide = Document.decide
              Fold = Document.fold
              Snapshots = Default }

    let slugs =
        Fcqrs.aggregate api
            { Name = "Slug"
              Initial = Slug.initial
              Decide = Slug.decide
              Fold = Slug.fold
              Snapshots = Default }

    let publication =
        Fcqrs.saga api (PublicationSaga.definition documents.Factory slugs.Factory)

    Fcqrs.wireSagaStarters api [ publication ]
    documents

(**
<div class="cs-alt"></div>

```csharp
services
    .AddFcqrs(connectionString, "Documents")
    .AddAggregate<PublicationDocumentAggregate>()
    .AddAggregate<SlugAggregate>()
    .AddSaga<PublicationSaga, DocumentEvent, PublicationData, PublicationState>(
        create: sp => new PublicationSaga(
            sp.AggregateFactory<PublicationDocumentAggregate>(),
            sp.AggregateFactory<SlugAggregate>()),
        startOn: PublicationSaga.StartsOn);
```

## How resumption works

Assume the saga has stored `ReportingResult Published` and sent `FinishPublication Published`, then the process
stops. On restart FCQRS:

1. loads the saga snapshot when one exists;
2. replays later state changes;
3. restores the starting-event context;
4. subscribes the saga again;
5. calls `applySideEffects` for `ReportingResult Published` with `recovering = true`.

The saga cannot know whether the earlier result reached the document. It sends the command again. The
document's idempotent decision returns the same `PublicationFinished` event without storing a
duplicate if the first command already succeeded.

This is resumability: recover durable progress and re-drive the next safe action. It is not rewinding
the other aggregate, and it is not exactly-once delivery.

> **Motivation:** Restarting the workflow from its first command could repeat already completed work.
> Re-driving only the action implied by the last stored state limits repetition to one retry-safe step.

## Run both outcomes

Publish two documents under the same slug:

<pre>
document A + guides/fcqrs
  -> SlugReserved
  -> PublicationFinished Published

document B + guides/fcqrs
  -> SlugUnavailable
  -> PublicationFinished Rejected
</pre>

The slug aggregate serializes both reservations and accepts only the first owner. Each publication
saga stores its own progress and safely completes the matching document.

## Common mistakes

- **Changing state inside `applySideEffects` without returning `NextState`.** Persist progress through
  `StateChangedEvent` or `NextState`; mutable local state disappears on recovery.
- **Sending commands from `handleEvent`.** Let FCQRS store the next state before side effects run.
- **Matching too many events in `StartOn`.** Only the domain event that begins a new workflow should
  create a saga instance.
- **Constructing `SagaStartingEvent` in application code.** Declare `StartOn`; FCQRS owns the runtime
  envelope and handshake.
- **Suppressing commands whenever `recovering = true`.** Re-drive an idempotent command or reconcile
  uncertain external work, otherwise the saga can remain stuck.
- **Assuming `StopSaga` deletes history.** It completes and passivates the actor; persisted progress
  remains available for diagnostics and storage policy.

## Continue the learning path

Next, [test the state machine and evolve its events](4-testing-and-evolution.html). Chapter 4 turns the
decisions, folds, retries, and stored contracts from the first three chapters into executable checks.

After chapter 4, use [Sagas](../concepts/sagas.html) for deeper treatment of uncertain delivery and
compensation, or [Write a saga](../how-to/write-a-saga.html) as the compact implementation recipe.
*)
