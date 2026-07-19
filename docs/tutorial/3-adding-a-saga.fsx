(**
---
title: 3. Adding a saga
category: Tutorial
categoryindex: 3
index: 4
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

> **Learn alongside this chapter:** read [Sagas](../concepts/sagas.html) for transitions,
> `SagaStartingEvent`, safe startup, and resumption. Use [Write a
> saga](../how-to/write-a-saga.html) as the compact API reference.

## The rule belongs to two owners

The document aggregate owns whether one document is ready to publish. A slug aggregate owns whether
one URL slug is available. Neither aggregate can make both decisions from its own state.

The saga coordinates this conversation:

<pre>
Document                  Publication saga                 Slug[guides/fcqrs]
   |                              |                                |
   |-- PublicationRequested ---->|                                |
   |                              |-- Reserve(documentId) -------->|
   |                              |<-- SlugReserved / Unavailable --|
   |<-- ConfirmPublication -------|                                |
   |          or                  |                                |
   |<-- RejectPublication --------|                                |
   |                              |                                |
   |-- Published / Rejected ----->|-- stop                         |
</pre>

The target aggregates still own every business decision. The saga owns only the progress of this
conversation.

> **Motivation:** Keeping each rule with its owner prevents the saga from becoming a second, stale copy
> of document and slug state. The saga coordinates answers; it does not invent them.

## Keep the domain types small

Chapter 1 covered validated titles and content. This chapter uses strings for those already-understood
fields so the workflow code is easier to see. Validate the slug at the application boundary before
sending `Publish`.
*)

module Values =
    type DocumentId =
        | DocumentId of Guid
        static member OfGuid value = DocumentId value
        member this.Value = let (DocumentId value) = this in value
        override this.ToString() = let (DocumentId value) = this in value.ToString()

(**
## Step 1: let the document request publication

The document stores `PublicationRequested` before any reservation begins. Its status records the slug
being reserved, so confirmation and rejection remain valid after recovery.
*)

module Document =
    open Values

    type Root =
        { Id: DocumentId
          Title: string
          Content: string }

    type PublicationStatus =
        | Draft
        | ReservingSlug of string
        | PublishedAs of string
        | RejectedFor of string

    type State =
        { Document: Root option
          Publication: PublicationStatus }

    let initial = { Document = None; Publication = Draft }

    type Command =
        | CreateOrUpdate of Root
        | Publish of slug: string
        | ConfirmPublication
        | RejectPublication

    type Event =
        | Updated of Root
        | PublicationRequested of DocumentId * slug: string
        | Published of DocumentId * slug: string
        | PublicationRejected of DocumentId * slug: string

    let decide (cmd: Command<Command>) state =
        match cmd.CommandDetails, state.Document, state.Publication with
        | CreateOrUpdate document, _, _ ->
            Updated document |> PersistEvent
        | Publish slug, Some document, Draft
        | Publish slug, Some document, RejectedFor _ ->
            PublicationRequested(document.Id, slug) |> PersistEvent
        | Publish slug, Some document, ReservingSlug current when current = slug ->
            PublicationRequested(document.Id, slug) |> DeferEvent
        | ConfirmPublication, Some document, ReservingSlug slug ->
            Published(document.Id, slug) |> PersistEvent
        | ConfirmPublication, Some document, PublishedAs slug ->
            Published(document.Id, slug) |> DeferEvent
        | RejectPublication, Some document, ReservingSlug slug ->
            PublicationRejected(document.Id, slug) |> PersistEvent
        | RejectPublication, Some document, RejectedFor slug ->
            PublicationRejected(document.Id, slug) |> DeferEvent
        | _ -> UnhandledEvent

    let fold (event: Event<Event>) state =
        match event.EventDetails with
        | Updated document -> { state with Document = Some document }
        | PublicationRequested(_, slug) -> { state with Publication = ReservingSlug slug }
        | Published(_, slug) -> { state with Publication = PublishedAs slug }
        | PublicationRejected(_, slug) -> { state with Publication = RejectedFor slug }

(**
`ConfirmPublication` and `RejectPublication` are retry-safe. Repeating the verdict returns the same
outcome with `DeferEvent`, so recovery can reissue the saga command without storing another domain
event.

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

## Step 3: write the saga as a table

Before writing the saga functions, write every accepted event and resulting command:

| Current state | Incoming event | Stored next state | Command after storage |
|---|---|---|---|
| not started | `PublicationRequested` | `ReservingSlug` | `Reserve` to slug |
| `ReservingSlug` | `SlugReserved` | `ConfirmingPublication` | `ConfirmPublication` to document |
| `ReservingSlug` | `SlugUnavailable` | `RejectingPublication` | `RejectPublication` to document |
| `ConfirmingPublication` | `Published` | `Done` | stop |
| `RejectingPublication` | `PublicationRejected` | `Done` | stop |

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
        | ConfirmingPublication
        | RejectingPublication
        | Done

(**
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
            ConfirmingPublication |> StateChangedEvent
        | SlugEvent(Slug.SlugUnavailable documentId), Some(ReservingSlug(expected, _))
            when documentId = expected ->
            RejectingPublication |> StateChangedEvent
        | DocumentEvent(Document.Published _), Some ConfirmingPublication ->
            Done |> StateChangedEvent
        | DocumentEvent(Document.PublicationRejected _), Some RejectingPublication ->
            Done |> StateChangedEvent
        | _ -> UnhandledEvent

(**
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
        | ConfirmingPublication ->
            Stay, [ toOriginator documentFactory Document.ConfirmPublication ]
        | RejectingPublication ->
            Stay, [ toOriginator documentFactory Document.RejectPublication ]
        | Done ->
            StopSaga, []

(**
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

## C# saga

The C# API keeps the same two functions. `HandleEvent` returns a persisted state action;
`ApplySideEffects` returns commands and `Stay`, `NextState`, or `StopSaga`.

```csharp
public abstract record PublicationState
{
    public sealed record ReservingSlug(DocumentId DocumentId, string Slug) : PublicationState;
    public sealed record ConfirmingPublication : PublicationState;
    public sealed record RejectingPublication : PublicationState;
    public sealed record Done : PublicationState;
}

public sealed record PublicationData;

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

    public override EventAction<PublicationState> HandleEvent(
        object message,
        SagaState<PublicationData, FSharpOption<PublicationState>> sagaState) =>
        (message, sagaState.State?.Value) switch
        {
            (Event<DocumentEvent>
                { EventDetails: DocumentEvent.PublicationRequested requested }, null) =>
                StateChanged(new PublicationState.ReservingSlug(
                    requested.DocumentId, requested.Slug)),
            (Event<SlugEvent> { EventDetails: SlugEvent.SlugReserved },
                PublicationState.ReservingSlug _) =>
                StateChanged(new PublicationState.ConfirmingPublication()),
            (Event<SlugEvent> { EventDetails: SlugEvent.SlugUnavailable },
                PublicationState.ReservingSlug _) =>
                StateChanged(new PublicationState.RejectingPublication()),
            (Event<DocumentEvent> { EventDetails: DocumentEvent.Published },
                PublicationState.ConfirmingPublication _) =>
                StateChanged(new PublicationState.Done()),
            (Event<DocumentEvent> { EventDetails: DocumentEvent.PublicationRejected },
                PublicationState.RejectingPublication _) =>
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
            PublicationState.ConfirmingPublication _ => new()
            {
                Transition = Stay(),
                Commands = [SagaCommands.ToOriginator(
                    _documents, new DocumentCommand.ConfirmPublication())]
            },
            PublicationState.RejectingPublication _ => new()
            {
                Transition = Stay(),
                Commands = [SagaCommands.ToOriginator(
                    _documents, new DocumentCommand.RejectPublication())]
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

The document and slug aggregates use the same `HandleCommand` and `ApplyEvent` pattern taught in
chapter 1. [Write a saga](../how-to/write-a-saga.html) includes the complete C# registration call.

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
    .AddAggregate<DocumentAggregate>()
    .AddAggregate<SlugAggregate>()
    .AddSaga<PublicationSaga, DocumentEvent, PublicationData, PublicationState>(
        create: sp => new PublicationSaga(
            sp.AggregateFactory<DocumentAggregate>(),
            sp.AggregateFactory<SlugAggregate>()),
        startOn: e => e is Event<DocumentEvent>
            { EventDetails: DocumentEvent.PublicationRequested });
```

## How resumption works

Assume the saga has stored `ConfirmingPublication` and sent `ConfirmPublication`, then the process
stops. On restart FCQRS:

1. loads the saga snapshot when one exists;
2. replays later state changes;
3. restores the starting-event context;
4. subscribes the saga again;
5. calls `applySideEffects` for `ConfirmingPublication` with `recovering = true`.

The saga cannot know whether the earlier confirmation reached the document. It sends the command
again. The document's idempotent decision returns `Published` without storing a duplicate if the first
command already succeeded.

This is resumability: recover durable progress and re-drive the next safe action. It is not rewinding
the other aggregate, and it is not exactly-once delivery.

> **Motivation:** Restarting the workflow from its first command could repeat already completed work.
> Re-driving only the action implied by the last stored state limits repetition to one retry-safe step.

## Run both outcomes

Publish two documents under the same slug:

<pre>
document A + guides/fcqrs
  -> SlugReserved
  -> Published

document B + guides/fcqrs
  -> SlugUnavailable
  -> PublicationRejected
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

## Understand it and use it

- **Understand:** [Sagas](../concepts/sagas.html) explains transitions, starting, resumption, uncertain
  delivery, and compensation from first principles. [Consistency and
  recovery](../concepts/consistency-and-recovery.html) compares the saga journal with other durable
  boundaries.
- **Apply:** [Write a saga](../how-to/write-a-saga.html) is the shorter F# and C# recipe.
  [Dispatch async effects](../how-to/dispatch-async-effects.html) is the alternative when work may
  safely be lost.

Next, [test the state machine and evolve its events](4-testing-and-evolution.html).
*)
