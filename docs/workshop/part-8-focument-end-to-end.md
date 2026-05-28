---
title: Part 8 · End to End
category: Workshop
categoryindex: 4
index: 9
---

# Part 8 — Focument, end to end

Every piece is now on the table: the aggregate (Part 3), event sourcing and recovery (Part 4), the
read side (Part 5), client coordination by correlation id (Part 6), and sagas (Part 7). This part
assembles them into the running Focument application and then follows one `POST /api/document` all
the way through, naming each part as we pass it. If the earlier parts were the instruments, this is
hearing them play together.

## Wiring it all up

An FCQRS application has a short, ordered bootstrap, and the order is not arbitrary — each step
depends on the one before it. Here is the FCQRS-relevant core of Focument's startup, with the
ASP.NET security plumbing trimmed away.

```fsharp
// focument/src/Server/Program.fs  (the CQRS wiring)
let actorApi =
    Actor.api builder.Configuration logf (Some connection)
              ("FocumentCluster" |> ValueLens.TryCreate |> Result.value)

// 1. the aggregate factory (the saga will need it)
let documentFactory = Document.Shard.Factory actorApi

// 2. the saga, given the originator factory it commands
let sagaFactory = DocumentSaga.factory actorApi documentFactory

// 3. declare which event starts which saga
actorApi.InitializeSagaStarter(fun evt ->
    match evt with
    | :? Common.Event<Document.Event> as e ->
        match e.EventDetails with
        | Document.CreatedOrUpdated _ -> [ sagaFactory ]
        | _ -> []
    | _ -> [])

// 4. start the projection, resuming from the stored offset
let lastOffset = ServerQuery.getLastOffset connectionString
let subs = Query.init actorApi (int lastOffset) (Projection.handleEventWrapper logf connectionString)

// 5. the façade the HTTP handlers call
let commandHandler = CommandHandler.api actorApi
```

```csharp
// focument-csharp/src/Server/Program.cs  (the CQRS wiring)
var actorApi = ActorApi.Create(builder.Configuration, logf, connectionString, "FocumentCluster");

// 1. aggregate + saga, dependencies injected via constructors
var documentShard = new DocumentShard(logf.CreateLogger<DocumentShard>());
var approvalSaga  = new DocumentApprovalSaga(documentShard, logf.CreateLogger<DocumentApprovalSaga>());
var sagaFactory   = approvalSaga.Factory(actorApi);

// 3. declare which event starts which saga
IActorExtensions.InitSagaStarterSimple(actorApi, evt =>
    evt is Event<DocumentEvent> { EventDetails: DocumentEvent.CreatedOrUpdated }
        ? [sagaFactory] : []);

// 4. start the projection, resuming from the stored offset
var lastOffset = ServerQuery.GetLastOffset(connectionString);
var subs = QueryApi.InitWithList(actorApi, (int)lastOffset,
    (offset, evt) => Projection.HandleEventWrapper(logf, connectionString, offset, evt));

// 5. the façade the HTTP handlers call
var commandHandler = ICommandHandlers.Create(actorApi, documentShard);
```

The sequence reads as a dependency chain. `Actor.api` / `ActorApi.Create` builds the `IActor` — the
handle to the whole actor system — from your configuration, a database connection, and a cluster
name. The aggregate factory comes next because the saga needs it as its originator. The saga is then
declared, and `InitializeSagaStarter` registers the single rule from Part 7: *a `CreatedOrUpdated`
event starts the `DocumentSaga`.* `Query.init` launches the projection stream from the last committed
offset (Part 5), and the command-handler façade is the thing the HTTP layer will call. After this,
the web framework maps `POST /api/document` to the handler we read in Part 6, and the application is
live.

## One request, traced through everything

Now watch a single browser action — *save a new document* — move through the system. Keep the
overview picture from Part 2 in mind; this is that diagram in motion.

![FCQRS at a glance](../img/cqrs-overview.svg)

**The request arrives.** `POST /api/document` lands in `createOrUpdateDocument`. The handler mints a
correlation id, validates the form into a `Document`, and — the rule from Part 6 — *subscribes to
that CID before sending anything*, filtering for the `Approved` event and taking one. Only then does
it hand `CreateOrUpdate` to the command handler.

**The aggregate decides.** The command routes to the one `Document` actor for this id (Part 3).
`handleCommand` sees `CreateOrUpdate` against an empty document and returns `CreatedOrUpdated |>
PersistEvent`. The event is appended to the journal, the version ticks from 0 to 1, `applyEvent`
sets the in-memory document, and the event is published (Part 4).

**Two listeners wake at once.** That published `CreatedOrUpdated` fans out, exactly as the overview
diagram shows.

The *projection* (Part 5) folds it: a row in `Documents` with `ApprovalStatus = 'Pending'`, a row in
`DocumentVersions`, and the offset advanced — all in one transaction. It returns the event, which is
republished on the subscription stream. But the handler is waiting for `Approved`, not
`CreatedOrUpdated`, so nothing completes yet.

The *saga starter* (Part 7) also saw `CreatedOrUpdated`, and our registration says that starts the
`DocumentSaga`. Through the handshake from Part 2 — the aggregate pausing just long enough to
guarantee the saga is subscribed — the saga comes to life and begins its state machine.

**The saga drives the workflow.** Now the loop from Part 7 plays out, every command flowing back to
the very same `Document` aggregate. The saga enters `GeneratingCode` and issues `SetApprovalCode`;
the aggregate persists `ApprovalCodeSet` (version 2). The saga advances to `SendingNotification`,
then `WaitingForApproval`, and issues `Approve`; the aggregate persists `Approved` (version 3). The
saga reaches its `Approved` state and returns `StopSaga`, passivating itself.

**The loop closes.** That `Approved` event is the one everybody was waiting for. The aggregate's
emission of it completes the command handler's own write-side await. And the projection folds it —
flipping `ApprovalStatus` to `'Approved'` and republishing the event — which finally matches the
client's CID subscription. `awaiter.Task` completes, and the handler returns `"Document received!"`.

**The client reads.** When the browser now calls `GET /api/documents`, it runs a plain `SELECT`
against the read model (Part 5) and sees the document with status `Approved` — guaranteed, because
the response was withheld until the read side had caught up. Read-your-writes, end to end.

## What just happened, in one breath

A single correlation id was minted in the HTTP handler, stamped on the command, carried onto the
aggregate's `CreatedOrUpdated`, threaded through the saga's `SetApprovalCode` and `Approve` commands
and their events, and finally matched by the subscription when the projection republished `Approved`.
One thread, from click to confirmed read. Three persisted events became one current-document row and
three history rows. No locks were taken, no read was stale, and every side effect went through a
command that could be recovered. That is the entire point of the machine, and you have now seen all
of it.

The last part, [Part 9](part-9-configuration.md), is the pragmatic coda: where the configuration
lives, the handful of knobs you will actually turn, and what changes when you run this for real.
