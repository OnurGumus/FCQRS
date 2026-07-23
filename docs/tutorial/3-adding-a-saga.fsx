(**
---
title: 3. Adding a saga
category: Learn FCQRS
categoryindex: 2
index: 5
---
*)

(*** hide ***)
#r "nuget: FCQRS, 6.0.0"
open System
open System.Collections.Concurrent
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Common
open FCQRS.Model.Data
open FCQRS.FSharp

(**
# 3. Adding a saga

DocStore can now store and look up documents. This chapter adds one more rule: a document can be
published under a URL slug, and each slug may belong to only one document.

Every rule so far involved a single document, so one aggregate could enforce it alone. This rule is
different, and it runs into a restriction that has been true since chapter 1: an aggregate can read
and change only its own state. A document aggregate cannot look inside a slug aggregate, cannot lock
it, and cannot update it in the same transaction. Aggregates never call each other. FCQRS keeps them
isolated on purpose, because that isolation is what lets each one process commands, recover, and move
between nodes independently.

In a single-database application the new rule would be one transaction touching a documents table and
a slugs table. Across aggregates there is no shared transaction, so something else must coordinate,
and that coordinator is the saga. A saga is a durable workflow that does the job a transaction
coordinator does in a database: it waits for an event from one aggregate, sends a command to the
next, waits for the reply, and reports the outcome back to where the work started. It cannot lock
both aggregates or roll them back together. Instead it stores its own progress after every step, so a
crash in the middle resumes the conversation instead of losing it.

This example is intentionally small. The only new problem is durable coordination, so the saga
mechanics remain visible.

> **Course position:** chapter 2 completed work owned by one aggregate and projected its event. This
> chapter handles one rule with two independent owners. By the end you will be able to derive saga
> state from events, issue commands only after progress is stored, and explain safe startup and
> resumption.

## The rule belongs to two owners

The document aggregate decides whether its document may be published. The slug aggregate decides
whether its slug is still free. Neither one can see the other's state, so neither one can enforce the
whole rule alone. The saga carries the conversation between them:

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

The aggregates still make every business decision. The saga only remembers how far the conversation
has gone and sends the next message.

> **Motivation:** Keeping each rule with its owner prevents the saga from becoming a second, stale copy
> of document and slug state. The saga coordinates answers; it does not invent them.

The chapter's new pieces as a wireframe; each unimplemented body names the step that fills it in.

```fsharp
// Step 1 extends Document with Publish / FinishPublication commands and
// PublicationRequested / PublicationFinished events.

module Slug =
    // One aggregate instance per slug; the first Reserve wins.
    let decide (cmd: Command<Command>) (state: State) : EventAction<Event> =
        failwith "step 2"

    let fold (event: Event<Event>) (state: State) : State =
        failwith "step 2"

module PublicationSaga =
    // Store intended progress first, then send the next safe command.
    let handleEvent (message: obj) (sagaState: SagaState<unit, State option>)
        : EventAction<State> =
        failwith "step 3"

    let applySideEffects documentFactory slugFactory sagaState recovering
        : SagaTransition<State> * ExecuteCommand list =
        failwith "step 3"

    let startsOn (event: Event<Document.Event>) : bool =
        failwith "step 4"
```

<div class="cs-alt"></div>

```csharp
// Step 1 extends DocumentCommand with Publish / FinishPublication and
// DocumentEvent with PublicationRequested / PublicationFinished.

// One aggregate instance per slug; the first Reserve wins.
public sealed class SlugAggregate : Aggregate<SlugState, SlugCommand, SlugEvent>
{
    public override EventAction<SlugEvent> HandleCommand(
        Command<SlugCommand> cmd, SlugState state) =>
        throw new NotImplementedException("step 2");

    public override SlugState ApplyEvent(Event<SlugEvent> evt, SlugState state) =>
        throw new NotImplementedException("step 2");
}

// Store intended progress first, then send the next safe command.
public sealed class PublicationSaga
    : Saga<DocumentEvent, PublicationData, PublicationState>
{
    public override EventAction<PublicationState> HandleEvent(
        object message,
        SagaState<PublicationData, FSharpOption<PublicationState>> sagaState) =>
        throw new NotImplementedException("step 3");

    public override SagaSideEffectResult<PublicationState> ApplySideEffects(
        SagaState<PublicationData, PublicationState> sagaState, bool recovering) =>
        throw new NotImplementedException("step 3");

    public static bool StartsOn(object message) =>
        throw new NotImplementedException("step 4");
}
```

## Extend the document with publication

Chapters 1 and 2 gave the document its content model: `Root`, `CreateOrUpdate`, and `Updated`. Those
stay exactly as they were. Publication is added to that same aggregate — the state gains a publication
track beside the document. The document id is the aggregate's own identity, so the new `Publish`
command carries only the slug. Validate the slug at the application boundary before sending it.

The validated values are unchanged from chapter 1:
*)

module Values =
    type DocumentId =
        | DocumentId of Guid
        static member OfGuid value = DocumentId value
        member this.Value = let (DocumentId value) = this in value
        override this.ToString() = let (DocumentId value) = this in value.ToString()

    type Title =
        | Title of ShortString
        static member TryCreate s =
            match ValueLens.TryCreate s with
            | Ok ss -> Ok(Title ss)
            | Error _ -> Error "Invalid title"
        member this.Value = let (Title s) = this in ValueLens.Value s

    type Content =
        | Content of LongString
        static member TryCreate s =
            match ValueLens.TryCreate s with
            | Ok ss -> Ok(Content ss)
            | Error _ -> Error "Invalid content"
        member this.Value = let (Content s) = this in ValueLens.Value s

(**
<div class="cs-alt"></div>

```csharp
// Unchanged from chapter 1.
public readonly record struct DocumentId(Guid Value)
{
    public static DocumentId OfGuid(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}
```

## Step 1: let the document request publication

The document stores `PublicationRequested` before any reservation begins. Its state records the slug
being reserved, so the eventual result remains valid after recovery. A document must exist (chapter
1's `CreateOrUpdate`) before it can be published.
*)

module Document =
    open Values

    type Root =
        { Id: DocumentId; Title: Title; Content: Content }

        static member TryCreate(guid, title, content) =
            match Title.TryCreate title, Content.TryCreate content with
            | Ok t, Ok c -> Ok { Id = DocumentId.OfGuid guid; Title = t; Content = c }
            | Error e, _ -> Error e
            | _, Error e -> Error e

    type PublicationResult =
        | Published
        | Rejected

    type PublicationStatus =
        | NotRequested
        | WaitingForSlug of slug: string
        | Finished of slug: string * PublicationResult

    type State =
        { Document: Root option
          Publication: PublicationStatus }

    let initial = { Document = None; Publication = NotRequested }

    type Command =
        | CreateOrUpdate of Root
        | Publish of slug: string
        | FinishPublication of PublicationResult

    type Event =
        | Updated of Root
        | PublicationRequested of DocumentId * slug: string
        | PublicationFinished of DocumentId * slug: string * PublicationResult

    let decide (cmd: Command<Command>) state =
        match cmd.CommandDetails, state with
        // Content changes work exactly as in chapter 1.
        | CreateOrUpdate doc, _ ->
            Updated doc |> PersistEvent
        // First Publish: store the request; state now remembers the slug being reserved.
        | Publish slug, { Document = Some doc; Publication = NotRequested } ->
            PublicationRequested(doc.Id, slug) |> PersistEvent
        // The same Publish again (a retry): same reply, nothing stored twice.
        | Publish slug, { Document = Some doc; Publication = WaitingForSlug currentSlug }
            when slug = currentSlug ->
            PublicationRequested(doc.Id, slug) |> DeferEvent
        // The saga reports the outcome: store it once.
        | FinishPublication result, { Document = Some doc; Publication = WaitingForSlug slug } ->
            PublicationFinished(doc.Id, slug, result) |> PersistEvent
        // The same outcome again (saga recovery): same reply, nothing stored twice.
        | FinishPublication result, { Document = Some doc; Publication = Finished(slug, current) }
            when result = current ->
            PublicationFinished(doc.Id, slug, result) |> DeferEvent
        // Everything else: no document yet, a different slug, a contradicting result.
        | _ -> UnhandledEvent

    let fold (event: Event<Event>) state =
        match event.EventDetails with
        | Updated doc -> { state with Document = Some doc }
        | PublicationRequested(_, slug) -> { state with Publication = WaitingForSlug slug }
        | PublicationFinished(_, slug, result) -> { state with Publication = Finished(slug, result) }

(**
`FinishPublication` carries either `Published` or `Rejected`. Repeating the same result returns the
same `PublicationFinished` outcome with `DeferEvent`, so recovery can reissue one saga command without
storing another domain event. Keeping the result as data removes duplicate success and failure
branches from the aggregate.

The publication cases form two pairs: the first request is stored and its retry is deferred; the
first result is stored and its retry is deferred. The final wildcard rejects every command and state
combination that does not belong to this workflow — including `Publish` before the document exists.

<div class="cs-alt"></div>

```csharp
public enum PublicationResult { Published, Rejected }

public abstract record PublicationProgress
{
    public sealed record NotRequested : PublicationProgress;
    public sealed record WaitingForSlug(string Slug) : PublicationProgress;
    public sealed record Finished(string Slug, PublicationResult Result) : PublicationProgress;
}

// The chapter-1 state type gains a publication track beside the document.
public sealed record DocumentState(Document? Document, PublicationProgress Publication)
{
    public static readonly DocumentState Initial =
        new(null, new PublicationProgress.NotRequested());
}

public union DocumentCommand(
    DocumentCommand.CreateOrUpdate, DocumentCommand.Publish, DocumentCommand.FinishPublication)
{
    public record CreateOrUpdate(Document Document);
    public record Publish(string Slug);
    public record FinishPublication(PublicationResult Result);
}

public union DocumentEvent(
    DocumentEvent.Updated, DocumentEvent.PublicationRequested, DocumentEvent.PublicationFinished)
{
    public record Updated(Document Document);
    public record PublicationRequested(DocumentId DocumentId, string Slug);
    public record PublicationFinished(
        DocumentId DocumentId, string Slug, PublicationResult Result);
}

public sealed class PublicationDocumentAggregate
    : Aggregate<DocumentState, DocumentCommand, DocumentEvent>
{
    public override DocumentState InitialState => DocumentState.Initial;
    public override string EntityName => "Document";

    public override EventAction<DocumentEvent> HandleCommand(
        Command<DocumentCommand> command, DocumentState state) =>
        (command.CommandDetails, state) switch
        {
            // Content changes work exactly as in chapter 1.
            (DocumentCommand.CreateOrUpdate c, _) =>
                EventActions.Persist<DocumentEvent>(new DocumentEvent.Updated(c.Document)),
            // First Publish: store the request; state now remembers the slug being reserved.
            (DocumentCommand.Publish p,
                { Document: { } doc, Publication: PublicationProgress.NotRequested }) =>
                EventActions.Persist<DocumentEvent>(
                    new DocumentEvent.PublicationRequested(doc.Id, p.Slug)),
            // The same Publish again (a retry): same reply, nothing stored twice.
            (DocumentCommand.Publish p,
                { Document: { } doc, Publication: PublicationProgress.WaitingForSlug waiting })
                when p.Slug == waiting.Slug =>
                EventActions.Defer<DocumentEvent>(
                    new DocumentEvent.PublicationRequested(doc.Id, p.Slug)),
            // The saga reports the outcome: store it once.
            (DocumentCommand.FinishPublication finish,
                { Document: { } doc, Publication: PublicationProgress.WaitingForSlug waiting }) =>
                EventActions.Persist<DocumentEvent>(new DocumentEvent.PublicationFinished(
                    doc.Id, waiting.Slug, finish.Result)),
            // The same outcome again (saga recovery): same reply, nothing stored twice.
            (DocumentCommand.FinishPublication finish,
                { Document: { } doc, Publication: PublicationProgress.Finished current })
                when finish.Result == current.Result =>
                EventActions.Defer<DocumentEvent>(new DocumentEvent.PublicationFinished(
                    doc.Id, current.Slug, finish.Result)),
            // Everything else: no document yet, a different slug, a contradicting result.
            _ => EventActions.Ignore<DocumentEvent>()
        };

    public override DocumentState ApplyEvent(
        Event<DocumentEvent> stored, DocumentState state) =>
        stored.EventDetails switch
        {
            DocumentEvent.Updated updated =>
                state with { Document = updated.Document },
            DocumentEvent.PublicationRequested requested =>
                state with { Publication = new PublicationProgress.WaitingForSlug(requested.Slug) },
            DocumentEvent.PublicationFinished finished =>
                state with
                {
                    Publication = new PublicationProgress.Finished(finished.Slug, finished.Result)
                },
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
        // First reservation wins and becomes durable.
        | Reserve documentId, None ->
            SlugReserved documentId |> PersistEvent
        // The same document asking again gets the same answer, nothing stored.
        | Reserve documentId, Some current when current = documentId ->
            SlugReserved documentId |> DeferEvent
        // Any other document: taken; the owner does not change.
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
            // First reservation wins and becomes durable.
            (SlugCommand.Reserve reserve, null) =>
                EventActions.Persist<SlugEvent>(new SlugEvent.SlugReserved(reserve.DocumentId)),
            // The same document asking again gets the same answer, nothing stored.
            (SlugCommand.Reserve reserve, DocumentId owner) when owner == reserve.DocumentId =>
                EventActions.Defer<SlugEvent>(new SlugEvent.SlugReserved(reserve.DocumentId)),
            // Any other document: taken; the owner does not change.
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
        // Table row 1: the starting event opens the workflow.
        | DocumentEvent(Document.PublicationRequested(documentId, slug)), None ->
            ReservingSlug(documentId, slug) |> StateChangedEvent
        // Table rows 2 and 3: the slug's answer decides which result to report.
        | SlugEvent(Slug.SlugReserved documentId), Some(ReservingSlug(expected, _))
            when documentId = expected ->
            ReportingResult Document.Published |> StateChangedEvent
        | SlugEvent(Slug.SlugUnavailable documentId), Some(ReservingSlug(expected, _))
            when documentId = expected ->
            ReportingResult Document.Rejected |> StateChangedEvent
        // Table row 4: the document confirmed the result; the workflow is complete.
        | DocumentEvent(Document.PublicationFinished(_, _, result)), Some(ReportingResult expected)
            when result = expected ->
            Done |> StateChangedEvent
        // Anything else is out of order for the current state.
        | _ -> UnhandledEvent

(**
<div class="cs-alt"></div>

```csharp
public override EventAction<PublicationState> HandleEvent(
    object message,
    SagaState<PublicationData, FSharpOption<PublicationState>> sagaState) =>
    (message, sagaState.State?.Value) switch
    {
        // Table row 1: the starting event opens the workflow.
        (Event<DocumentEvent>
            { EventDetails: DocumentEvent.PublicationRequested requested }, null) =>
            StateChanged(new PublicationState.ReservingSlug(
                requested.DocumentId, requested.Slug)),
        // Table rows 2 and 3: the slug's answer decides which result to report.
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
        // Table row 4: the document confirmed the result; the workflow is complete.
        (Event<DocumentEvent>
            { EventDetails: DocumentEvent.PublicationFinished finished },
            PublicationState.ReportingResult reporting)
            when finished.Result == reporting.Result =>
            StateChanged(new PublicationState.Done()),
        // Anything else is out of order for the current state.
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
        // Ask the slug to reserve; recovery re-sends this, and Reserve is retry-safe.
        | ReservingSlug(documentId, slug) ->
            Stay, [ toAggregate slugFactory slug (Slug.Reserve documentId) ]
        // Report the outcome back to the document that started the workflow.
        | ReportingResult result ->
            Stay, [ toOriginator documentFactory (Document.FinishPublication result) ]
        // Nothing left to send; the saga completes and passivates.
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
        // Ask the slug to reserve; recovery re-sends this, and Reserve is retry-safe.
        PublicationState.ReservingSlug state => new()
        {
            Transition = Stay(),
            Commands = [SagaCommands.ToAggregate(
                _slugs, state.Slug, new SlugCommand.Reserve(state.DocumentId))]
        },
        // Report the outcome back to the document that started the workflow.
        PublicationState.ReportingResult state => new()
        {
            Transition = Stay(),
            Commands = [SagaCommands.ToOriginator(
                _documents, new DocumentCommand.FinishPublication(state.Result))]
        },
        // Nothing left to send; the saga completes and passivates.
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

Now run the workflow. The helper below publishes one document and prints the result the saga drives
back. Two details from chapter 2 return here: the document is created with `CreateOrUpdate` before
`Publish`, and the subscription is made *before* sending so the result cannot slip past. The saga's
commands keep your correlation id, so one subscription sees the whole workflow — the final
`PublicationFinished` included.
*)

let buildApi () : IActor =
    let config = ConfigurationBuilder().Build()
    let loggerFactory = LoggerFactory.Create(fun _ -> ())

    let connection =
        Fcqrs.connect FCQRS.Actor.DBType.Sqlite "Data Source=tutorial.db;"

    Fcqrs.actor config loggerFactory (Some connection) "tutorial"

let readModel = ConcurrentDictionary<string, Document.Root>()

let handleProjection (_offset: int64) (message: obj) =
    match message with
    | :? Event<Document.Event> as event ->
        match event.EventDetails with
        | Document.Updated document -> readModel[document.Id.ToString()] <- document
        | _ -> ()
    | _ -> ()

let private isPublicationFinished (message: IMessageWithCID) =
    match message with
    | :? Event<Document.Event> as event ->
        match event.EventDetails with
        | Document.PublicationFinished _ -> true
        | _ -> false
    | _ -> false

let publishAndPrintOutcome
    (documents: AggregateHandle<Document.Command, Document.Event>)
    (subscriptions: FCQRS.Query.ISubscribe)
    (doc: Document.Root)
    slug
    =
    async {
        let id = Fcqrs.aggregateId (doc.Id.ToString())

        // The document must exist before it can be published (chapter 1's command).
        let! _created =
            documents.Send (Fcqrs.newCid ()) id (Document.CreateOrUpdate doc)
                (fun e ->
                    match e with
                    | Document.Updated _ -> true
                    | _ -> false)

        let cid = Fcqrs.newCid ()
        let mutable outcome = None

        // Subscribe BEFORE publishing: the saga's commands keep this CID, so the
        // final result arrives on this same subscription.
        use finished =
            subscriptions.Subscribe(cid, isPublicationFinished, 1, fun message ->
                outcome <- Some message)

        let! _requested =
            documents.Send cid id (Document.Publish slug)
                (fun e ->
                    match e with
                    | Document.PublicationRequested _ -> true
                    | _ -> false)

        do! finished.Task |> Async.AwaitTask

        match outcome with
        | Some(:? Event<Document.Event> as event) ->
            (match event.EventDetails with
             | Document.PublicationFinished(_, slug, result) ->
                 printfn "%s -> %A (%s)" slug result doc.Title.Value
             | _ -> ())
            |> ignore
        | _ -> ()
    }

(**
<div class="cs-alt"></div>

```csharp
// The wiring step configured the builder; C# needs no separate buildApi.
var readModel = new ConcurrentDictionary<string, Document>();

void HandleProjection(long _offset, object message)
{
    if (message is Event<DocumentEvent> { EventDetails: DocumentEvent.Updated updated })
        readModel[updated.Document.Id.ToString()] = updated.Document;
}

async Task PublishAndPrintOutcome(
    Handler<DocumentCommand, DocumentEvent> documents,
    ISubscribe subscriptions,
    Document doc,
    string slug)
{
    var id = Values.CreateAggregateId(doc.Id.ToString());

    // The document must exist before it can be published (chapter 1's command).
    await documents(
        e => e is DocumentEvent.Updated,
        Values.NewCID(), id, new DocumentCommand.CreateOrUpdate(doc));

    var cid = Values.NewCID();
    Event<DocumentEvent>? outcome = null;

    // Subscribe BEFORE publishing: the saga's commands keep this CID, so the
    // final result arrives on this same subscription. The filter also records
    // the matching event, because the awaiter's Task carries no payload.
    using var awaiter = subscriptions.SubscribeForFirst(cid, message =>
    {
        if (message is Event<DocumentEvent> { EventDetails: DocumentEvent.PublicationFinished } ev)
        {
            outcome = ev;
            return true;
        }
        return false;
    });

    await documents(
        e => e is DocumentEvent.PublicationRequested,
        cid, id, new DocumentCommand.Publish(slug));

    await awaiter.Task;

    if (outcome is { EventDetails: DocumentEvent.PublicationFinished finished })
        Console.WriteLine($"{finished.Slug} -> {finished.Result} ({doc.Title})");
}
```

```fsharp
let run () =
    async {
        let api = buildApi ()
        let documents = wire api
        let subscriptions = Fcqrs.projection api (Projection.single 0 handleProjection)

        let docA =
            Document.Root.TryCreate(Guid.NewGuid(), "FCQRS guide", "draft A") |> Result.value

        let docB =
            Document.Root.TryCreate(Guid.NewGuid(), "Competing guide", "draft B") |> Result.value

        do! publishAndPrintOutcome documents subscriptions docA "guides/fcqrs"
        do! publishAndPrintOutcome documents subscriptions docB "guides/fcqrs"
    }

[<EntryPoint>]
let main _ =
    run () |> Async.RunSynchronously
    0
```

<div class="cs-alt"></div>

```csharp
// Top-level statements are the entry point: register the projection, start the
// host from the wiring step, and run both publications.
builder.Services.AddProjection(HandleProjection, lastOffset: 0);

using var host = builder.Build();
await host.StartAsync();

var documents = host.Services.GetRequiredService<Handler<DocumentCommand, DocumentEvent>>();
var subscriptions = host.Services.GetRequiredService<ISubscribe>();

// Result.value in F#; validation cannot fail for these literals.
Document.TryCreate(Guid.NewGuid(), "FCQRS guide", "draft A", out var docA, out _);
Document.TryCreate(Guid.NewGuid(), "Competing guide", "draft B", out var docB, out _);

await PublishAndPrintOutcome(documents, subscriptions, docA, "guides/fcqrs");
await PublishAndPrintOutcome(documents, subscriptions, docB, "guides/fcqrs");

await host.StopAsync();
```

Publish two documents under the same slug:

```text
dotnet run
# guides/fcqrs -> Published (FCQRS guide)
# guides/fcqrs -> Rejected (Competing guide)
```

The slug aggregate serializes both reservations and accepts only the first owner. Each publication
saga stores its own progress and safely completes the matching document. Run again without deleting
`tutorial.db` and both documents are rejected — the reservation from the first run is durable.

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
- **Waiting without a deadline.** A slug that answers with an ignored command, or a lost message
  between nodes, parks the saga forever. Give every wait a timeout;
  [Write a saga](../how-to/write-a-saga.html) shows both the `toSelfAfter` reminder and the declared
  `StayExpecting` form.
- **Assuming `StopSaga` deletes history.** It completes and passivates the actor; persisted progress
  remains available for diagnostics and storage policy.

## Continue the learning path

Next, [test the state machine and evolve its events](4-testing-and-evolution.html). Chapter 4 turns the
decisions, folds, retries, and stored contracts from the first three chapters into executable checks.

After chapter 4, use [Sagas](../concepts/sagas.html) for deeper treatment of uncertain delivery and
compensation, or [Write a saga](../how-to/write-a-saga.html) as the compact implementation recipe.
*)
