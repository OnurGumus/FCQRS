---
title: Send at an expected version
category: Apply
categoryindex: 4
index: 3
---

# Send at an expected version

A document editor loads version `7` and sends its changes with that version. If another edit has
already advanced the document to version `8`, FCQRS rejects the stale command before the aggregate's
decision function runs.

Use `Fcqrs.sendIfVersion` in F# or `SendIfVersionAsync` in C#, available in FCQRS 6.5.0 or later.
The examples use the document types from
`samples/getting-started-fsharp/Document.fs` and `samples/getting-started-csharp/Document.cs`:

```fsharp
open FCQRS.Common
open FCQRS.FSharp
open Program

let editDocument api documents expectedVersion documentId content =
    Fcqrs.sendIfVersion api documents expectedVersion
        (Fcqrs.newCid ()) (Fcqrs.aggregateId documentId)
        (EditDocument(documentId, content))
        (function
         | DocumentEdited _ | DocumentRejected _ -> true
         | _ -> false)
```

<div class="cs-alt"></div>

```csharp
using FCQRS;
using static FCQRS.Common;
using static FCQRS.CSharp;
using static FCQRS.CSharp.ActorWiring;

public sealed class DocumentEditor(
    FcqrsRuntime runtime,
    AggregateRefs<DocumentCommand, DocumentEvent> documents)
{
    public Task<Event<DocumentEvent>> EditDocument(
        long expectedVersion, string documentId, string content,
        CancellationToken cancellationToken) =>
        runtime.Actor.SendIfVersionAsync(
            documents.Factory, expectedVersion,
            Values.NewCID(), Values.CreateAggregateId(documentId),
            (DocumentCommand)new EditDocument(documentId, content),
            (DocumentEvent reply) => reply is DocumentEdited or DocumentRejected,
            cancellationToken);
}
```

In F#, `api` and `documents` come from `Fcqrs.actor` and `Fcqrs.aggregate`. In C#,
`AddAggregate<DocumentAggregate>()` registers the typed `AggregateRefs` for injection, and `AddFcqrs`
registers `FcqrsRuntime`. Import `FCQRS.CSharp.ActorWiring` as shown to make the extension method
available. Call the service after the host has started. If several aggregates share
the same command and event types, resolve the refs keyed by the aggregate class, as described in
[Use FCQRS from C#](use-from-csharp.html).

`expectedVersion` is a nonnegative `int64` in F# or `long` in C#. Supply the version that accompanied
the data being edited. In a read model, store the aggregate event's `Version` alongside the document
fields and commit both in the same projection transaction. A delayed read model can return an older
version; the aggregate then detects that the edit was based on stale data.

## Handle a conflict

A version mismatch raises `FCQRS.Common.AggregateVersionConflictException`. Its `AggregateId` property
is a string; `ExpectedVersion` and `ActualVersion` are 64-bit integers. A mismatch does not depend on
the event filter accepting a domain reply.

```fsharp
async {
    try
        let! reply = editDocument api documents 7L "doc-1" "Revised content"
        printfn "Document reply: %A" reply.EventDetails
    with :? AggregateVersionConflictException as conflict ->
        printfn "Document %s changed: expected %d, actual %d"
            conflict.AggregateId conflict.ExpectedVersion conflict.ActualVersion
}
```

<div class="cs-alt"></div>

```csharp
try
{
    var reply = await editor.EditDocument(
        7L, "doc-1", "Revised content", cancellationToken);
    Console.WriteLine($"Document reply: {reply.EventDetails}");
}
catch (AggregateVersionConflictException conflict)
{
    Console.WriteLine(
        $"Document {conflict.AggregateId} changed: " +
        $"expected {conflict.ExpectedVersion}, actual {conflict.ActualVersion}");
}
```

On conflict, load the current document and let the caller review or merge its changes. Automatically
substituting `ActualVersion` and resending would permit the stale edit that the check was intended to
prevent. The reported actual version was current at the check; another command can advance it before
the exception reaches the caller.

When the version matches, the domain still decides whether the edit is valid. The method returns the
first matching aggregate reply, including a deferred `DocumentRejected` reply. Inspect that reply
before reporting that the edit succeeded.

Conditional waits match the command ID, correlation ID, target aggregate, and event filter. FCQRS
preserves these IDs for persisted and deferred replies and guarded `RunAsync` continuations. If a
handler uses `PublishEvent` with an envelope it builds itself, copy the incoming command's `Id` and
`CorrelationId` into that envelope. Otherwise, the conditional wait can time out even though the event
was published; a different command's reply with the same correlation ID cannot complete this wait.

## Understand the version boundary

FCQRS checks the version inside the aggregate actor immediately before calling its decision function.
One aggregate instance processes commands sequentially, so another command cannot run between this
check and that decision. An initial mismatch skips the decision function and fold, produces no domain
event, and writes nothing to the journal.

The checked value is the aggregate's persisted domain version:

| Action or lifecycle stage | Version |
|---|---|
| No events have been persisted | `0` |
| Persist one event | Advances by `1` |
| `PersistAllEvents` with several events | Advances once per event in the batch |
| Defer a reply or perform no write | Unchanged |
| Recover from the journal or a snapshot | Restores the persisted version |

Two concurrent edits expecting version `7` cannot both persist from that version. After one persists,
the other observes the advanced version and conflicts. Two commands that write nothing can both
match version `7`. This check is not a command identifier or a durable record of a previous request.

The boundary covers one aggregate identity. It does not make changes to other aggregates atomic and
does not wait for a projection. For correlated [read-your-writes](read-your-writes.html), subscribe
before sending the conditional command, then await the projection notification when the reply was
journaled. Alternatively, call a transactional projection's `CatchUpAsync` after the command reply,
as shown in [Catch up projections](catch-up-projections.html), before querying the updated read model.

## Handle delayed work and unknown outcomes

A stashed conditional command retains its expected version and checks it again when it is unstashed.
For `RunAsync`, FCQRS checks before dispatching the effect and checks the same expected version again
when the result command returns. If the aggregate changed while the effect was running, that result
command conflicts. Work already performed outside the actor is not undone. See
[Dispatch a best-effort async effect](dispatch-async-effects.html) for its recovery limits.

The command wait uses `akka.fcqrs.command-timeout`, which defaults to 30 seconds. An already-canceled
token prevents the C# request from starting. Once a request starts, a timeout or cancellation stops
waiting; it does not prove that no event was saved or prevent processing already in flight, even if
the caller has not yet observed that the command was sent. Retry policy still belongs to the
application. A retry using the original version can conflict after the first attempt succeeded, but
that conflict alone cannot identify which command advanced the version. Use a domain operation
identifier when repeated requests need a durable, recognizable result.

Upgrade every node that can receive aggregate commands before using this API. Conditional commands
use a distinct transport message; older receivers do not support it and can reject it or leave the
caller waiting until timeout. They do not execute it as an ordinary unguarded command. Persisted
command and event envelope shapes remain unchanged.

[Test your domain](test-your-domain.html) covers decision and replay tests. Test competing conditional
commands with a running FCQRS runtime too: the expected-version check belongs to the actor, so calling
the domain decision function directly does not exercise it.
