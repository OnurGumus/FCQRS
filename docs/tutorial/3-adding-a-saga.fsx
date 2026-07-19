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

This chapter adds a user quota to the document application. It reuses the validated values from
[chapter 1](1-the-aggregate.html) and adds `Username`, which also identifies the user aggregate.
*)

module Values =
    type DocumentId =
        | DocumentId of Guid
        static member OfGuid g = DocumentId g
        member this.Value = let (DocumentId g) = this in g
        override this.ToString() = let (DocumentId g) = this in g.ToString()
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
    type Username =
        | Username of ShortString
        static member TryCreate s =
            match ValueLens.TryCreate s with
            | Ok ss -> Ok(Username ss)
            | Error _ -> Error "a username is required"
        member this.Value = let (Username s) = this in ValueLens.Value s

(**
The rule is: each user may create at most three documents per minute. A document aggregate cannot
enforce it because its state contains one document. The user aggregate can own the quota because its
state covers one user's recent document creations. A **saga** coordinates the request between the two
aggregates.

## Why an aggregate can't reach across the boundary

An aggregate serializes decisions about its own state. Reading another aggregate's state inside that
decision would create a consistency boundary that neither actor owns. Instead, the document records a
request. The saga reacts to that event, sends a command to the user, and sends the user's result back to
the document.

Aggregates turn commands into events. Sagas react to events by issuing commands. A saga also stores its
state, allowing the coordination to continue after recovery.

Here's the flow we're about to build, end to end:

<pre>
  create request
        |
        v
  Document  -- CreateOrUpdateRequested -->  (this starts the saga)
        |
        v
  saga  -- ConsumeQuota -->  User[owner]
                                |
                 +--------------+--------------+
                 |                             |
          QuotaApproved                 QuotaRejected
          (within quota)                (over quota)
                 |                             |
                 v                             v
          saga: Approve                 saga: Hold
                 |                             |
                 v                             v
   Document: ApprovedEvt        Document: HeldForApproval
</pre>

This design adds a persistent coordinator and asynchronous steps. Use it when work crosses aggregate
boundaries or must retain progress across restarts. A short best-effort lookup can use an
[ephemeral async effect](../how-to/dispatch-async-effects.html) instead.

## Step 1: record a pending document request

Creating a document now stores `CreateOrUpdateRequested`. That event starts the saga. The saga later
sends `Approve` or `Hold` to the same document. Editing an existing document remains a direct update
because the quota applies only to creation.
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

    type Approval = Pending | AwaitingApproval | Approved | Rejected
    type DocumentError = DocumentNotFound

    type Command =
        | CreateOrUpdate of Root * Username   // who's asking
        | Approve
        | Reject
        | Hold

    type Event =
        | CreateOrUpdateRequested of Root * Username
        | Updated of Root
        | Errored of DocumentError
        | ApprovedEvt of DocumentId
        | RejectedEvt of DocumentId
        | HeldForApproval of DocumentId

    type State = { Document: Root option; Approval: Approval }
    let initial = { Document = None; Approval = Pending }

    let decide (cmd: Command<_>) state =
        match cmd.CommandDetails, state.Document with
        | CreateOrUpdate(doc, owner), None -> CreateOrUpdateRequested(doc, owner) |> PersistEvent
        | CreateOrUpdate(doc, _), Some existing when existing.Id = doc.Id -> Updated doc |> PersistEvent
        | CreateOrUpdate _, _ -> Errored DocumentNotFound |> DeferEvent
        // A repeated verdict replies again but does not store a duplicate event.
        | Approve, Some doc ->
            let e = ApprovedEvt doc.Id
            if state.Approval = Approved then DeferEvent e else PersistEvent e
        | Reject, Some doc ->
            let e = RejectedEvt doc.Id
            if state.Approval = Rejected then DeferEvent e else PersistEvent e
        | Hold, Some doc ->
            let e = HeldForApproval doc.Id
            if state.Approval = AwaitingApproval then DeferEvent e else PersistEvent e
        | _ -> UnhandledEvent

    let fold evt state =
        match evt.EventDetails with
        | CreateOrUpdateRequested(doc, _) -> { state with Document = Some doc; Approval = Pending }
        | Updated doc -> { state with Document = Some doc }
        | ApprovedEvt _ -> { state with Approval = Approved }
        | RejectedEvt _ -> { state with Approval = Rejected }
        | HeldForApproval _ -> { state with Approval = AwaitingApproval }
        | Errored _ -> state

(**
<div class="cs-alt"></div>

```csharp
// C#: the extended Document aggregate. Approval is an enum; the verdict cases
// guard on current state so a re-issued command defers instead of duplicating.
public enum Approval { Pending, AwaitingApproval, Approved, Rejected }

public record DocumentState(Document? Document, Approval Approval = Approval.Pending)
{
    public static readonly DocumentState Initial = new(null);
}

public sealed class DocumentAggregate : Aggregate<DocumentState, DocumentCommand, DocumentEvent>
{
    public override DocumentState InitialState => DocumentState.Initial;
    public override string EntityName => "Document";

    public override EventAction<DocumentEvent> HandleCommand(
        Command<DocumentCommand> cmd, DocumentState state) =>
        (cmd.CommandDetails, state.Document) switch
        {
            (DocumentCommand.CreateOrUpdate c, null) =>
                EventActions.Persist<DocumentEvent>(new DocumentEvent.CreateOrUpdateRequested(c.Document, c.Owner)),
            (DocumentCommand.CreateOrUpdate c, { } existing) when existing.Id == c.Document.Id =>
                EventActions.Persist<DocumentEvent>(new DocumentEvent.Updated(c.Document)),
            (DocumentCommand.CreateOrUpdate, _) =>
                EventActions.Defer<DocumentEvent>(new DocumentEvent.Error(new DocumentError.DocumentNotFound())),
            (DocumentCommand.Approve, { } doc) =>
                state.Approval == Approval.Approved
                    ? EventActions.Defer<DocumentEvent>(new DocumentEvent.Approved(doc.Id))
                    : EventActions.Persist<DocumentEvent>(new DocumentEvent.Approved(doc.Id)),
            (DocumentCommand.Reject, { } doc) =>
                state.Approval == Approval.Rejected
                    ? EventActions.Defer<DocumentEvent>(new DocumentEvent.Rejected(doc.Id))
                    : EventActions.Persist<DocumentEvent>(new DocumentEvent.Rejected(doc.Id)),
            (DocumentCommand.Hold, { } doc) =>
                state.Approval == Approval.AwaitingApproval
                    ? EventActions.Defer<DocumentEvent>(new DocumentEvent.HeldForApproval(doc.Id))
                    : EventActions.Persist<DocumentEvent>(new DocumentEvent.HeldForApproval(doc.Id)),
            _ => EventActions.Ignore<DocumentEvent>()
        };

    public override DocumentState ApplyEvent(Event<DocumentEvent> evt, DocumentState state) =>
        evt.EventDetails switch
        {
            DocumentEvent.CreateOrUpdateRequested e =>
                state with { Document = e.Document, Approval = Approval.Pending },
            DocumentEvent.Updated e => state with { Document = e.Document },
            DocumentEvent.Approved => state with { Approval = Approval.Approved },
            DocumentEvent.Rejected => state with { Approval = Approval.Rejected },
            DocumentEvent.HeldForApproval => state with { Approval = Approval.AwaitingApproval },
            _ => state
        };
}
```
*)

(**
The verdict commands are idempotent. If `Approve` is retried after the document is already approved,
the aggregate defers the same reply. The saga receives confirmation, but the journal does not gain a
duplicate approval. `UnhandledEvent` covers commands that do not make sense in the current state.

## Step 2: let the user aggregate own the quota

The user aggregate is keyed by username and stores the document slots granted during the last minute.
It uses the document id to recognize a retried request.
*)

module User =
    open Values

    type Command = ConsumeQuota of DocumentId

    type Event =
        | QuotaApproved of DocumentId * DateTime
        | QuotaRejected

    type Consumption = { DocId: DocumentId; At: DateTime }
    type State = { Consumed: Consumption list }
    let initial = { Consumed = [] }

    [<Literal>]
    let Limit = 3
    let Window = TimeSpan.FromMinutes 1.0

    let private prune (reference: DateTime) slots =
        let cutoff = reference - Window
        slots |> List.filter (fun c -> c.At > cutoff)

    let decide (cmd: Command<_>) state =
        match cmd.CommandDetails with
        | ConsumeQuota docId ->
            match state.Consumed |> List.tryFind (fun c -> c.DocId = docId) with
            // Re-delivery: reply with the existing grant without storing it again.
            | Some existing -> QuotaApproved(docId, existing.At) |> DeferEvent
            | None ->
                if prune cmd.CreationDate state.Consumed |> List.length < Limit then
                    QuotaApproved(docId, cmd.CreationDate) |> PersistEvent
                else
                    QuotaRejected |> DeferEvent

    let fold evt state =
        match evt.EventDetails with
        | QuotaApproved(docId, _) when state.Consumed |> List.exists (fun c -> c.DocId = docId) -> state
        | QuotaApproved(docId, at) -> { state with Consumed = prune at ({ DocId = docId; At = at } :: state.Consumed) }
        | QuotaRejected -> state

(**
<div class="cs-alt"></div>

```csharp
// C#: the User aggregate is idempotent by document id,
// and time read from the command / folded only from the event.
public readonly record struct Consumption(DocumentId DocId, DateTime At);

public record UserState(IReadOnlyList<Consumption> Consumed)
{
    public static readonly UserState Initial = new(Array.Empty<Consumption>());
}

public sealed class UserAggregate : Aggregate<UserState, UserCommand, UserEvent>
{
    public const int Limit = 3;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public override UserState InitialState => UserState.Initial;
    public override string EntityName => "User";

    static IReadOnlyList<Consumption> Prune(IEnumerable<Consumption> slots, DateTime reference) =>
        slots.Where(c => c.At > reference - Window).ToArray();

    public override EventAction<UserEvent> HandleCommand(Command<UserCommand> cmd, UserState state) =>
        cmd.CommandDetails switch
        {
            UserCommand.ConsumeQuota c => Decide(state, c.DocId, cmd.CreationDate),
            _ => EventActions.Ignore<UserEvent>()
        };

    static EventAction<UserEvent> Decide(UserState state, DocumentId docId, DateTime at)
    {
        var existing = state.Consumed.FirstOrDefault(c => c.DocId == docId);
        if (state.Consumed.Any(c => c.DocId == docId))
            return EventActions.Defer<UserEvent>(new UserEvent.QuotaApproved(docId, existing.At));
        return Prune(state.Consumed, at).Count < Limit
            ? EventActions.Persist<UserEvent>(new UserEvent.QuotaApproved(docId, at))
            : EventActions.Defer<UserEvent>(new UserEvent.QuotaRejected());
    }

    public override UserState ApplyEvent(Event<UserEvent> evt, UserState state) =>
        evt.EventDetails switch
        {
            UserEvent.QuotaApproved e when state.Consumed.Any(c => c.DocId == e.DocId) => state,
            UserEvent.QuotaApproved e =>
                state with { Consumed = Prune(state.Consumed.Append(new Consumption(e.DocId, e.ConsumedAt)), e.ConsumedAt) },
            _ => state
        };
}
```
*)

(**
A repeated `ConsumeQuota` for the same document returns the original grant as a deferred reply, so it
does not spend or store another slot.

The decision reads `cmd.CreationDate` and stores that timestamp in `QuotaApproved`. The fold uses only
the timestamp carried by the event. Calling `DateTime.UtcNow` inside the fold would prune a different set
of slots each time the history was replayed.

## The saga

The quota saga starts when a document is requested. It asks the user aggregate to consume a slot, then
tells the document to approve or hold. Its four states record which reply it is waiting for.
*)

module QuotaSaga =
    open Values

    type State =
        | CheckingQuota of Username * DocumentId
        | Approving of DocumentId
        | Holding of DocumentId
        | Done

(**
A saga receives events from both aggregates as `obj`. The active patterns recover the typed event
envelopes, allowing the handler to match the event and current saga state together. Each accepted event
returns the next state with `StateChangedEvent`.
*)

    let private (|DocEvent|_|) (o: obj) =
        match o with
        | :? (Event<Document.Event>) as e -> Some e.EventDetails
        | _ -> None

    let private (|UserEvent|_|) (o: obj) =
        match o with
        | :? (Event<User.Event>) as e -> Some e.EventDetails
        | _ -> None

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

(**
`handleEvent` selects the next state. `applySideEffects` maps that state to a transition and commands.
`toAggregate` addresses the user by username. `toOriginator` addresses the document that started this
saga.
*)

    let private applySideEffects documentFactory userFactory sagaState _recovering =
        match sagaState.State with
        | CheckingQuota(owner, docId) ->
            Stay, [ toAggregate userFactory owner.Value (User.ConsumeQuota docId) ]
        | Approving _ -> Stay, [ toOriginator documentFactory Document.Approve ]
        | Holding _ -> Stay, [ toOriginator documentFactory Document.Hold ]
        | Done -> StopSaga, []

(**
The saga issues commands rather than calling an external API inside the state transition. External
handlers still need idempotency and timeouts because a remote service cannot share the saga's journal
transaction. During recovery, FCQRS calls this function with `recovering = true` to re-drive the current
state. This quota saga returns the same commands because both target aggregates handle retries
idempotently. A branch calling an external system can use the flag to choose a status check or another
recovery-specific command, but suppressing every recovery command can leave the saga waiting forever.

The `Saga` record connects the functions, originator factory, initial data, start predicate, and
snapshot policy. `StartOn` selects the originator event that creates a saga instance.
*)

    let startsOn (e: Event<Document.Event>) =
        match e.EventDetails with
        | Document.CreateOrUpdateRequested _ -> true
        | _ -> false

    let definition documentFactory userFactory =
        { Name = "QuotaSaga"
          InitialData = ()                       // no cross-step data: progress lives in State
          Originator = documentFactory
          HandleEvent = handleEvent
          ApplySideEffects = applySideEffects documentFactory userFactory
          StartOn = startsOn
          Snapshots = Default }

(**
<div class="cs-alt"></div>

```csharp
// C#: the whole saga is a class deriving Saga<OriginatorEvent, Data, State>.
// HandleEvent matches events from both aggregates (state is None until the first
// transition); ApplySideEffects returns the transition + commands. The "StartOn"
// predicate isn't a member here. It is passed to AddSaga in the next block.
public sealed class QuotaSaga : Saga<DocumentEvent, QuotaSagaData, QuotaState>
{
    readonly Func<string, IEntityRef<object>> _documents, _users;
    public QuotaSaga(Func<string, IEntityRef<object>> documents, Func<string, IEntityRef<object>> users)
        { _documents = documents; _users = users; }

    public override QuotaSagaData InitialData => new();
    public override string SagaName => "QuotaSaga";
    public override Func<string, IEntityRef<object>> Originator => _documents;

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
            QuotaState.Approving => new() { Transition = Stay(), Commands = [SagaCommands.ToOriginator(_documents, new DocumentCommand.Approve())] },
            QuotaState.Holding   => new() { Transition = Stay(), Commands = [SagaCommands.ToOriginator(_documents, new DocumentCommand.Hold())] },
            QuotaState.Done      => new() { Transition = StopSaga(), Commands = [] },
            _ => new() { Transition = Stay(), Commands = [] }
        };
}
```
*)

(**
## Wiring it all up

The composition root registers both aggregates, constructs the saga with their factories, and wires
the [saga-start handshake](../concepts/sagas.html).
*)

let wire (api: IActor) =
    let documents = Fcqrs.aggregate api { Name = "Document"; Initial = Document.initial; Decide = Document.decide; Fold = Document.fold; Snapshots = Default }
    let users     = Fcqrs.aggregate api { Name = "User";     Initial = User.initial;     Decide = User.decide;     Fold = User.fold; Snapshots = Default }
    let quota = Fcqrs.saga api (QuotaSaga.definition documents.Factory users.Factory)
    Fcqrs.wireSagaStarters api [ quota ]
    documents

(**
<div class="cs-alt"></div>

```csharp
// C#: register both aggregates and the saga via the DI host-builder. AddSaga's
// startOn is the safe-start rule and the C# counterpart of StartOn.
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
*)

(**
The start handshake prevents `CreateOrUpdateRequested` from being published before the new saga has
subscribed. The application declares the start event; FCQRS enforces the creation and publication order.

## Run the quota path

Send four creates for the same user (each a different document id) and the fourth crosses the line:

<pre>
create #1 (alice)  ->  ApprovedEvt       "saved"
create #2 (alice)  ->  ApprovedEvt       "saved"
create #3 (alice)  ->  ApprovedEvt       "saved"
create #4 (alice)  ->  HeldForApproval   "over quota - awaiting approval"
</pre>

The first three requests follow `CreateOrUpdateRequested -> ConsumeQuota -> QuotaApproved -> Approve ->
ApprovedEvt`. The fourth receives `QuotaRejected`; the saga sends `Hold`, and the document reaches
`AwaitingApproval`. After a minute, `prune` removes expired grants when the next quota command is
decided.

## What you now understand

A user aggregate owns the quota and a document aggregate owns approval. The saga coordinates them by
mapping events to states and states to commands. Retry safety comes from idempotent target commands, and
replay safety comes from storing time in events instead of reading the clock in folds.

## Common mistakes

- **Reading another aggregate from `decide`.** Move the cross-boundary process into a saga.
- **Assuming exactly-once delivery.** Make repeated commands return the existing business result without
  repeating the state change.
- **Reading the clock in `fold`.** Carry the decision time in the stored event.
- **Calling an external service from a transition.** Issue a command to a handler with explicit timeout,
  retry, and idempotency behaviour.
- **Using a saga for disposable work.** Use an ephemeral async effect when losing in-flight work during
  restart is acceptable.

## Further study

- [Sagas](../concepts/sagas.html): state, startup, and recovery.
- [Consistency and recovery](../concepts/consistency-and-recovery.html): version checks and failure
  boundaries.
- [Write a saga](../how-to/write-a-saga.html): the focused API recipe.

## You've built the whole loop

You now have the complete runtime flow: aggregates store facts, projections build query data, and a
saga coordinates work across aggregate boundaries. Continue with [testing and
evolution](4-testing-and-evolution.html) before preparing the application for production. The complete
application is available in [`focument_fsharp`](https://github.com/OnurGumus/focument_fsharp).
*)
