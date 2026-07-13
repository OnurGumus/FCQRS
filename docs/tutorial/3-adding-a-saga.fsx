(**
---
title: 3. Adding a saga
category: Tutorial
categoryindex: 3
index: 4
---
*)

(*** hide ***)
#r "nuget: FCQRS, 6.0.0-preview28"
open System
open FCQRS.Common
open FCQRS.Model.Data
open FCQRS.FSharp

(**
# 3. Adding a saga

We'll reuse the validated values from [chapter 1](1-the-aggregate.html) — `Title`/`Content` wrap
FCQRS's `ShortString`/`LongString`, plus a `Username` (which doubles as the User aggregate's id):
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
Here's a rule that sounds trivial and turns out to be impossible with what we've built: *each user may
create at most three documents per minute.* Go ahead and try to put it in `Document.decide`. You can't —
a document only knows about itself. It has no idea how many *other* documents the same person created in
the last sixty seconds, and it would be a layering disaster if it did. The limit isn't a fact about one
document; it's a fact about a *user*, spanning many documents.

That's the shape of problem a **saga** exists for. This chapter introduces a second aggregate (a `User`
that owns the quota) and the saga that coordinates the two. It's the most involved chapter — and the one
where the framework finally does something you'd genuinely dread writing by hand.

## Why an aggregate can't reach across the boundary

Recall from [chapter 1](1-the-aggregate.html) that an aggregate is a *consistency boundary*: one entity,
deciding alone, one command at a time. That isolation is precisely what gives you no-locks, no-races
correctness. The price is that an aggregate cannot reach into another aggregate to check or change it —
if it could, you'd be right back to shared mutable state and the races we escaped.

> 🎯 **Key principle.** Aggregates turn commands into events. A **saga** is the mirror image: it turns
> events into commands. When something true happens in one aggregate (a document was requested) and it
> should *cause* something in another (consume a quota slot), the saga is the only thing allowed to carry
> that intent across the boundary. It is a process manager — a small, durable state machine that listens
> for events and issues commands.

> 💡 **Mental model.** Think of a travel booking. The "flight" service and the "hotel" service each guard
> their own data; neither reaches into the other. A *booking coordinator* watches for "flight reserved,"
> then tells the hotel "reserve a room," and if that fails, tells the flight "cancel." The coordinator
> holds no flight or hotel data of its own — it only watches and issues orders. That coordinator is a
> saga.

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

⚠️ Before we write it: a saga is real added complexity — a second persistent actor, an extra hop, more
states to reason about. Don't reach for one to send a welcome email you could fire inline. Reach for one
when the work genuinely **spans aggregates** or must be **reliably retried/compensated**. Our quota is
the first kind: two aggregates, so a saga isn't a style choice, it's structurally required.

## Step 1 — teach the Document about approval

The first write can no longer just succeed, because whether it's allowed now depends on the user's
quota — which the document can't see. So a creation becomes a *pending request* (`CreateOrUpdateRequested`)
that records the document and **starts the saga**; the saga later tells the document `Approve` or `Hold`.
An edit of a document we already hold still skips all of it — editing isn't gated.

This is where the command/event split from chapter 1 pays off: `CreateOrUpdate` now fans out into
several possible events, and because we kept them separate types from the start, nothing about the
caller's command has to change.
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

    type State = { Document: Root option; Version: int64; Approval: Approval }
    let initial = { Document = None; Version = 0L; Approval = Pending }

    let decide (cmd: Command<_>) state =
        match cmd.CommandDetails, state.Document with
        // First write — no document yet. A pending request that starts the quota saga.
        | CreateOrUpdate(doc, owner), None -> CreateOrUpdateRequested(doc, owner) |> PersistEvent
        // Edit of the document we already hold — no saga, no quota.
        | CreateOrUpdate(doc, _), Some existing when existing.Id = doc.Id -> Updated doc |> PersistEvent
        | CreateOrUpdate _, _ -> Errored DocumentNotFound |> DeferEvent
        // Verdicts — idempotent: if already in the target state, defer (still
        // published so a re-issuing saga sees it) rather than persist a duplicate.
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
        | CreateOrUpdateRequested(doc, _) -> { state with Document = Some doc; Version = state.Version + 1L; Approval = Pending }
        | Updated doc -> { state with Document = Some doc; Version = state.Version + 1L }
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

public record DocumentState(Document? Document, long Version, Approval Approval = Approval.Pending)
{
    public static readonly DocumentState Initial = new(null, 0L);
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
                state with { Document = e.Document, Version = state.Version + 1L, Approval = Approval.Pending },
            DocumentEvent.Updated e => state with { Document = e.Document, Version = state.Version + 1L },
            DocumentEvent.Approved => state with { Approval = Approval.Approved },
            DocumentEvent.Rejected => state with { Approval = Approval.Rejected },
            DocumentEvent.HeldForApproval => state with { Approval = Approval.AwaitingApproval },
            _ => state
        };
}
```
*)

(**
Look closely at the verdict cases — they're a small but important lesson:

> ⚠️ **Common mistake.** Assuming each command is delivered exactly once. Across restarts and retries a
> saga may re-issue `Approve` for a document that's *already* approved. If you blindly `PersistEvent`
> every time, you log duplicate approvals and inflate the version on no-ops. The guard here checks "am I
> already in that state?" and downgrades the repeat to a `DeferEvent` — published so the saga still hears
> "yes," but not re-recorded. Designing for **at-least-once** delivery, not exactly-once, is the rule,
> not the exception, in any distributed system.

Notice `UnhandledEvent` finally appears: it's how `decide` says "that command makes no sense in this
state" — distinct from `IgnoreEvent` ("valid, but do nothing").

## Step 2 — the User aggregate owns the quota

The second aggregate is keyed by username and remembers the slots a user consumed in the last minute.
Two design choices make it safe under a saga's retries, and both are worth pausing on.
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
            // Re-delivery: this document already holds a slot — re-grant the SAME slot.
            | Some existing -> QuotaApproved(docId, existing.At) |> PersistEvent
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
// C#: the User aggregate. Same two safety choices — idempotent by document id,
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
        if (state.Consumed.Any(c => c.DocId == docId))           // re-grant the SAME slot
            return EventActions.Persist<UserEvent>(new UserEvent.QuotaApproved(docId, existing.At));
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
First, **idempotency by document id**: if a slot was already granted for this document, `decide`
re-grants the *same* slot instead of spending a new one, so a retried `ConsumeQuota` can't double-charge.

Second — and this is the one people get wrong — look at where time comes from:

> 🎯 **Key principle.** `decide` reads `cmd.CreationDate` (the moment captured when the command was
> issued), and `fold` only ever uses the timestamp the event *carries* (`QuotaApproved(_, at)`). Neither
> calls `DateTime.UtcNow`. This is the chapter-1 purity rule with teeth: if `fold` read the wall clock,
> replaying the log a minute later would prune different slots and reconstruct a *different* quota state.
> Capture time in the command, carry it in the event, fold only what the event holds — and replay stays
> deterministic forever.

## The saga

A saga is a tiny state machine. It begins when a document is requested, asks the user to consume a
slot, then routes the document to approval or hold. The framework supplies the "not started yet"
preamble (the `None` you'll see below); these four states are ours, and the rest is two functions.
*)

module QuotaSaga =
    open Values

    type State =
        | CheckingQuota of Username * DocumentId
        | Approving of DocumentId
        | Holding of DocumentId
        | Done

(**
A saga sees its events as `obj`, because they arrive from **both** aggregates — `Document` events *and*
`User` events flow into the same handler. Two small active patterns recover the typed payload so the
handler can match on event-and-state together in one flat, readable pass. Each arm returns the next
state with `StateChangedEvent`.
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
`handleEvent` decides *what state we're in*; `applySideEffects` decides *what to do on entering it* — the
transition (`Stay`, `NextState`, `StopSaga`) and the commands to send. This is where the saga turns
events back into commands, and the facade keeps it to one line each with two helpers: `toAggregate`
addresses a *specific* aggregate instance by id (the `User` keyed by owner name), `toOriginator` replies
to whichever document started this saga.
*)

    let private applySideEffects documentFactory userFactory sagaState _recovering =
        match sagaState.State with
        | CheckingQuota(owner, docId) ->
            Stay, [ toAggregate userFactory owner.Value (User.ConsumeQuota docId) ]
        | Approving _ -> Stay, [ toOriginator documentFactory Document.Approve ]
        | Holding _ -> Stay, [ toOriginator documentFactory Document.Hold ]
        | Done -> StopSaga, []

(**
> ⚠️ **Common mistake.** Performing the side effect *inside* the saga — sending the email, calling the
> API — right here. Don't. A saga's job is to *issue a command*; the receiving actor performs the effect
> through the same persist-and-recover machinery as everything else, so it can be retried and audited.
> That's also what the `_recovering` flag is for: when a saga rebuilds itself after a restart it replays
> its states, and you use that flag to avoid re-firing real-world effects you already fired. (Our quota
> only issues commands to other aggregates, which are themselves idempotent, so we can ignore it here.)

Finally, bundle it into a `Saga` record. `StartOn` is typed to the *originator's* event, so the
framework **infers** the originator-event type — there's no type argument to remember or get wrong.
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
// predicate isn't a member here — it's passed to AddSaga (next block).
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

The composition root registers both aggregates, builds the saga from the factories they return, and
wires the saga-starter — the declaration that fires the safe
[start-up handshake](../concepts/sagas.html).
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
// startOn predicate is the safe-start rule — the C# counterpart of StartOn.
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
> 🤔 **Did you know?** There's a quiet race lurking in "an event starts a saga": if the document
> published `CreateOrUpdateRequested` *before* the saga was listening, the saga would miss the very event
> meant to create it. `wireSagaStarters` is what lets FCQRS run a brief handshake — hold the event until
> the saga is subscribed, *then* publish — so the start is safe by construction. You declare the rule;
> the framework guarantees the ordering. ([The handshake, in detail](../concepts/sagas.html).)

## Run it — watch the quota bite

Send four creates for the same user (each a different document id) and the fourth crosses the line:

<pre>
create #1 (alice)  ->  ApprovedEvt       "saved"
create #2 (alice)  ->  ApprovedEvt       "saved"
create #3 (alice)  ->  ApprovedEvt       "saved"
create #4 (alice)  ->  HeldForApproval   "over quota - awaiting approval"
</pre>

The first three each ride the path `CreateOrUpdateRequested -> ConsumeQuota -> QuotaApproved -> Approve
-> ApprovedEvt`. On the fourth, the `User` aggregate's sliding window is full, so it answers
`QuotaRejected`, the saga swings to `Holding`, and the document ends up `AwaitingApproval` instead of
approved. Wait a minute for the window to slide and the next create is approved again — because `prune`
drops the now-expired slots. You implemented a cross-entity, time-windowed rule without a single lock or
shared variable.

## What you now understand

An aggregate is sealed inside its own boundary on purpose, so when a rule spans entities you need a
different tool: a saga, which listens to events from several aggregates and issues commands back. You
saw the three pieces — `handleEvent` (event → next state), `applySideEffects` (state → commands), and
`StartOn` (which event spawns it) — and the two distributed-systems reflexes that keep it honest:
idempotent commands and time captured in events, never read in folds.

## Common mistakes

- **Trying to enforce a cross-entity rule inside one aggregate.** It can't see the others; that's the
  whole reason sagas exist.
- **Assuming exactly-once delivery.** Make commands idempotent — re-grant the same slot, downgrade a
  repeat verdict to `DeferEvent` — so retries are harmless.
- **Reading the clock in `decide`/`fold` instead of carrying time in the event.** Replay then drifts and
  reconstructs the wrong state.
- **Doing the side effect inside the saga.** Issue a command and let the target actor perform it, so the
  effect is retryable and recoverable; use `_recovering` to avoid re-firing on replay.
- **Reaching for a saga when a plain function call would do.** Sagas pay off for cross-aggregate or
  must-be-reliable work — not for everything that happens "after" an event.

## Further study

- [Sagas](../concepts/sagas.html) — process managers, the safe start-up handshake, and how they make
  side effects reliable.
- [Consistency and recovery](../concepts/consistency-and-recovery.html) — version checks, restarts, and
  avoiding duplicate effects.
- [Write a saga](../how-to/write-a-saga.html) — the same pattern as a focused recipe, with all the
  command builders.

## You've built the whole loop

Across three chapters you defined an aggregate from two pure functions, gave it a running home and
watched event sourcing reconstruct state across a restart, then added a second aggregate and a saga that
coordinates both to enforce a rule neither could alone — the entire FCQRS model. The complete, runnable
version of exactly this app is [`focument_fsharp`](https://github.com/OnurGumus/focument_fsharp). From
here, the [How-to guides](../how-to/index.html) are task-focused recipes, and
[Concepts](../concepts/index.html) goes deeper on anything you want to understand more fully.
*)
