(**
---
title: 4. Publish under a unique URL
category: Learn FCQRS
categoryindex: 2
index: 6
---
*)

(*** hide ***)
#r "nuget: FCQRS, 6.3.0"
#load "../../samples/getting-started-fsharp/Document.fs"
#load "../../samples/getting-started-fsharp/Publication.fs"
#load "../../samples/getting-started-fsharp/Checks.fs"

(**
# 4. Publish under a unique URL

Your document now contains `second draft`. Publish it under `guides/fcqrs`, a **slug** that becomes
part of a document's URL. The rule is: each slug may belong to only one document.

Keep the same project, document ID, and SQLite database. All examples in this chapter run on stable
.NET 10 in both F# and C#.

## Publish the edited document

```text
dotnet run --project samples/getting-started-fsharp -- --publish DOCUMENT_ID guides/fcqrs
```

<div class="cs-alt" data-fs="text" data-cs="text"></div>

```text
dotnet run --project samples/getting-started-csharp -- --publish DOCUMENT_ID guides/fcqrs
```


```text
publication version 4; guides/fcqrs -> Published
query returned 'second draft'
```

The document's history now has four events:

| Version | Event | Meaning |
|---|---|---|
| 1 | `DocumentCreated` | The original document was created. |
| 2 | `DocumentEdited` | Its content became `second draft`. |
| 3 | `PublicationRequested` | The document started reserving `guides/fcqrs`. |
| 4 | `PublicationFinished Published` | The slug was reserved and publication completed. |

The slug has its own event history. Reserving it does not increment the document's version.
The sample records publication state; it does not run an HTTP server or create a public website.

## Try to claim the same slug twice

Run the ordinary quickstart command again to create a **second** document. Keep its new ID and publish
it under the same slug:

```text
dotnet run --project samples/getting-started-fsharp -- --publish SECOND_DOCUMENT_ID guides/fcqrs
```

<div class="cs-alt" data-fs="text" data-cs="text"></div>

```text
dotnet run --project samples/getting-started-csharp -- --publish SECOND_DOCUMENT_ID guides/fcqrs
```


```text
publication version 3; guides/fcqrs -> Rejected
query returned 'first event'
```

The second document was never edited, so its creation, request, and result occupy versions 1 through 3.
The first document still owns the slug. Repeating either publication command returns the same result
without adding another publication event. A document has one publication attempt in this sample;
choosing a different slug after rejection would need an explicit new transition.

## Two rules have two owners

The document knows whether its content may be published. A separate **slug aggregate** knows who owns
one slug. Two document actors cannot enforce slug uniqueness using only their own state.

A **saga** stores the progress of a workflow and sends commands across those boundaries:

<pre>
document                         publication saga                         slug
   |                                    |                                  |
   |-- PublicationRequested ----------->|                                  |
   |                                    |-- ReserveSlug(documentId) ------>|
   |                                    |<-- SlugReserved / Unavailable ---|
   |<-- FinishPublication -------------|                                  |
   |-- PublicationFinished ----------->|  done                             |
</pre>

Each aggregate still makes its own decision. There is no shared transaction across the two owners.
The saga stores its intended next step before issuing its command, so recovery can re-drive that step.

## Let the slug protect its own owner

In `Publication.fs` / `Publication.cs`, the slug's decision is another first-writer rule:

<!-- sample: fsharp Publication.fs reserve -->
```fsharp
let decide (command: FCQRS.Common.Command<Command>) state =
    match command.CommandDetails, state.ReservedFor with
    | ReserveSlug id, None -> SlugReserved id |> PersistEvent
    | ReserveSlug id, Some owner when id = owner -> SlugReserved id |> DeferEvent
    | ReserveSlug id, Some _ -> SlugUnavailable id |> DeferEvent

let fold (event: FCQRS.Common.Event<Event>) state =
    match event.EventDetails with
    | SlugReserved id -> { ReservedFor = Some id }
    | SlugUnavailable _ -> state
```

<div class="cs-alt"></div>

<!-- sample: csharp Publication.cs reserve -->
```csharp
public override EventAction<SlugEvent> HandleCommand(Command<ReserveSlug> command, SlugState state) =>
    state.ReservedFor switch
    {
        null => EventActions.Persist<SlugEvent>(new SlugReserved(command.CommandDetails.DocumentId)),
        var owner when owner == command.CommandDetails.DocumentId =>
            EventActions.Defer<SlugEvent>(new SlugReserved(owner)),
        _ => EventActions.Defer<SlugEvent>(new SlugUnavailable(command.CommandDetails.DocumentId))
    };

public override SlugState ApplyEvent(Event<SlugEvent> stored, SlugState state) =>
    stored.EventDetails is SlugReserved reserved ? new(reserved.DocumentId) : state;
```


The first reservation stores the owner. A retry from that same document returns `SlugReserved`
without changing ownership. Another document receives `SlugUnavailable`. Both deferred replies
leave the slug's state unchanged. Slugs are compared as exact strings here. An HTTP application must
choose its URL normalization rules before using the slug as an aggregate ID.

## Read the workflow as a table

| Stored saga state | Incoming answer | Next stored state | Next command |
|---|---|---|---|
| Not started | `PublicationRequested` | `ReservingSlug` | Reserve the slug |
| `ReservingSlug` | `SlugReserved` | `ReportingResult Published` | Finish publication |
| `ReservingSlug` | `SlugUnavailable` | `ReportingResult Rejected` | Finish publication |
| `ReportingResult` | Matching `PublicationFinished` | `Done` | Stop the saga |
| Either active wait | Deadline exhausted | Corresponding uncertain state | Park for investigation |
| An uncertain state | Matching late answer | Resume the matching normal transition | Continue or finish |

The code in `handleEvent` / `HandleEvent` checks document IDs and results before accepting answers.
The timeout cases keep **unknown** separate from **rejected**. Only `SlugUnavailable` establishes the
rejection in this workflow.

<!-- sample: fsharp Publication.fs react -->
```fsharp
let handleEvent (message: obj) (saga: SagaState<unit, State option>) =
    match message, saga.State with
    | (:? Event<DocumentEvent> as event), None ->
        match event.EventDetails with
        | PublicationRequested(id, slug) -> StateChangedEvent(ReservingSlug(id, slug))
        | _ -> UnhandledEvent
    | (:? Event<Slug.Event> as event), Some(ReservingSlug(id, slug) | ReservationUncertain(id, slug)) ->
        match event.EventDetails with
        | Slug.SlugReserved owner when owner = id -> StateChangedEvent(ReportingResult(id, slug, Published))
        | Slug.SlugUnavailable rejected when rejected = id -> StateChangedEvent(ReportingResult(id, slug, Rejected))
        | _ -> UnhandledEvent
    | (:? Event<DocumentEvent> as event), Some(ReportingResult(id, slug, result) | ReportUncertain(id, slug, result)) ->
        match event.EventDetails with
        | PublicationFinished(foundId, foundSlug, foundResult)
            when (foundId, foundSlug, foundResult) = (id, slug, result) -> StateChangedEvent Done
        | _ -> UnhandledEvent
    | :? ExpectationExhausted, Some(ReservingSlug(id, slug)) ->
        StateChangedEvent(ReservationUncertain(id, slug))
    | :? ExpectationExhausted, Some(ReportingResult(id, slug, result)) ->
        StateChangedEvent(ReportUncertain(id, slug, result))
    | _ -> UnhandledEvent
```

<div class="cs-alt"></div>

<!-- sample: csharp Publication.cs react -->
```csharp
public override EventAction<PublicationState> HandleEvent(object message,
    SagaState<PublicationData, FSharpOption<PublicationState>> saga) =>
    (message, saga.State?.Value) switch
    {
        (Event<DocumentEvent> { EventDetails: PublicationRequested requested }, null) =>
            StateChanged(new ReservingSlug(requested.Id, requested.Slug)),
        (Event<SlugEvent> reply, ReservingSlug state) => ReservationReply(reply, state.Id, state.Slug),
        (Event<SlugEvent> reply, ReservationUncertain state) => ReservationReply(reply, state.Id, state.Slug),
        (Event<DocumentEvent> reply, ReportingResult state) => CompletionReply(reply, state.Id, state.Slug, state.Result),
        (Event<DocumentEvent> reply, ReportUncertain state) => CompletionReply(reply, state.Id, state.Slug, state.Result),
        (ExpectationExhausted, ReservingSlug state) => StateChanged(new ReservationUncertain(state.Id, state.Slug)),
        (ExpectationExhausted, ReportingResult state) => StateChanged(new ReportUncertain(state.Id, state.Slug, state.Result)),
        _ => Unhandled()
    };

static EventAction<PublicationState> ReservationReply(Event<SlugEvent> reply, string id, string slug) =>
    reply.EventDetails switch
    {
        SlugReserved reserved when reserved.DocumentId == id => StateChanged(new ReportingResult(id, slug, PublicationResult.Published)),
        SlugUnavailable unavailable when unavailable.DocumentId == id => StateChanged(new ReportingResult(id, slug, PublicationResult.Rejected)),
        _ => Unhandled()
    };

static EventAction<PublicationState> CompletionReply(Event<DocumentEvent> reply, string id, string slug, PublicationResult result) =>
    reply.EventDetails is PublicationFinished finished && (finished.Id, finished.Slug, finished.Result) == (id, slug, result)
        ? StateChanged(new PublicationDone()) : Unhandled();
```


`StateChangedEvent` / `StateChanged` asks FCQRS to persist the next saga state. It does not send the
next command itself. That happens in the side-effect function after persistence:

<!-- sample: fsharp Publication.fs effects -->
```fsharp
let applySideEffects documentFactory slugFactory (saga: SagaState<unit, State>) _recovering =
    let waitFor command =
        expecting (TimeSpan.FromMinutes 5.) (FixedInterval(TimeSpan.FromSeconds 2.)) [ command ], []
    match saga.State with
    | ReservingSlug(id, slug) -> waitFor (toAggregate slugFactory slug (Slug.ReserveSlug id))
    | ReportingResult(_, slug, result) -> waitFor (toOriginator documentFactory (FinishPublication(slug, result)))
    | ReservationUncertain _ | ReportUncertain _ -> Stay, []
    | Done -> StopSaga, []
```

<div class="cs-alt"></div>

<!-- sample: csharp Publication.cs effects -->
```csharp
public SagaSideEffectResult<PublicationState> Effects(PublicationState state) => state switch
{
    ReservingSlug reserving => WaitFor(SagaCommands.ToAggregate(slugs, reserving.Slug, new ReserveSlug(reserving.Id))),
    ReportingResult reporting => WaitFor(SagaCommands.ToOriginator(documents, new FinishPublication(reporting.Slug, reporting.Result))),
    ReservationUncertain or ReportUncertain => new() { Transition = Stay(), Commands = [] },
    PublicationDone => new() { Transition = StopSaga(), Commands = [] },
    _ => throw new InvalidOperationException("Unknown publication state")
};

static SagaSideEffectResult<PublicationState> WaitFor(ExecuteCommand command) => new()
{
    Transition = Stay(),
    Expect = Expectations.Create([command], TimeSpan.FromMinutes(5), RetrySchedules.Fixed(TimeSpan.FromSeconds(2)))
};
```


Both active waits have a five-minute deadline and retry every two seconds. The deadline is measured
from the persisted state-entry time. A restart re-arms the best-effort reminder without postponing
that deadline. Recovery issues the same commands, so `ReserveSlug` and `FinishPublication` must be
retry-safe. The document stores the completion once and defers repeated matching completions.

After exhaustion, the uncertain state parks with no further automatic retries. It still accepts a
matching late reply. An application must expose parked workflows to operators and provide a deliberate
reconciliation or compensation path; the sample checks these transitions without inventing a failed
publication. A caller's 30-second wait can time out before the workflow's deadline. That also means
unknown, not rejected.

## Start the workflow before releasing its event

The runner registers the document and slug aggregates, then the saga and its `StartsOn` predicate,
then the projection. The predicate selects `PublicationRequested`.

<!-- sample: fsharp Program.fs wire -->
```fsharp
let slugs =
    Fcqrs.aggregate api
        { Name = "GettingStartedFSharpSlug"; Initial = Publication.Slug.initial
          Decide = Publication.Slug.decide; Fold = Publication.Slug.fold
          Snapshots = Default; Passivation = PassivationPolicy.Default }
let publication = Fcqrs.saga api (Publication.definition documents.Factory slugs.Factory pause paused)
Fcqrs.wireSagaStarters api [ publication ]
let subscriptions = Fcqrs.projection api (Projection.single 0 handleProjection)
```

<div class="cs-alt"></div>

<!-- sample: csharp Program.cs wire -->
```csharp
builder.Services.AddFcqrs($"Data Source={database};", "getting-started-csharp")
    .AddAggregate<DocumentAggregate>()
    .AddAggregate<SlugAggregate>()
    .AddSaga<PublicationSaga, DocumentEvent, PublicationData, PublicationState>(
        sp => new PublicationSaga(sp.AggregateFactory<DocumentAggregate>(), sp.AggregateFactory<SlugAggregate>(), pause, paused),
        PublicationSaga.StartsOn)
    .AddProjection(HandleProjection, lastOffset: 0);
```


FCQRS subscribes a starting saga before publishing its trigger event, so the new saga can receive the
event that starts it. Recovery restores workflow progress and re-drives side effects; it cannot make
external operations exactly once. An external payment or email step would need its own idempotency,
timeout, retry, and compensation decisions.

## Wait across a recovered workflow

One publication produces several events. Waiting for the first notification on a correlation id could
wake the caller at `PublicationRequested`, before publication has finished.

The sample's projection observer instead selects `PublicationFinished` for the requested document ID
and slug. It is installed **before runtime startup** and receives replay from offset zero as well as
new events. This matters after a restart: a recovered saga can finish under its original correlation
id before a new request begins. An observer created afterward could miss that completion.

The observer completes after the projection has applied the document's preceding events. That is why
the printed query contains `second draft`. This coordination covers this one in-memory projection;
it is not a durable subscription for a remote client.

**Predict:** try editing the first document now. It returns
`edit rejected: Editing closes when publication starts`. Keeping content fixed ensures the finished
publication describes the version that requested its slug.

Continue to [5. Test changes and recovery](4-testing-and-evolution.html) to pause a workflow between
its two owners, restart it, and verify the stored history.

*)

(*** hide ***)
Checks.sagaChecks ()
printfn "Evaluated docs/tutorial/3-adding-a-saga.fsx"
