---
title: Evolve persisted events
category: Apply
categoryindex: 4
index: 9
---

# Evolve persisted events

A document journal originally stored its body in a `Text` field. The current event calls that field
`Content` and records a content format. All historical documents in this example contain plain text:

```fsharp
type DocumentSavedV1 = { Id: string; Text: string }
type DocumentSavedV2 = { Id: string; Content: string }
type DocumentSavedV3 = { Id: string; Content: string; Format: string }

let v1ToV2 (old: DocumentSavedV1) : DocumentSavedV2 =
    { Id = old.Id; Content = old.Text }

let v2ToV3 (old: DocumentSavedV2) : DocumentSavedV3 =
    { Id = old.Id; Content = old.Content; Format = "text/plain" }
```

<div class="cs-alt"></div>

```csharp
public sealed record DocumentSavedV1(string Id, string Text);
public sealed record DocumentSavedV2(string Id, string Content);
public sealed record DocumentSavedV3(string Id, string Content, string Format);

public static class DocumentUpcasters
{
    public static DocumentSavedV2 V1ToV2(DocumentSavedV1 old) =>
        new(old.Id, old.Text);

    public static DocumentSavedV3 V2ToV3(DocumentSavedV2 old) =>
        new(old.Id, old.Content, "text/plain");
}
```

An **event upcaster** converts a stored payload to the representation current journal consumers use.
Here, a chain converts V1 to V2 and then V2 to V3. A stored V2 event needs only the second conversion;
a stored V3 event already has the current shape.

The registration APIs on this page require FCQRS 6.5.0 or later.

The default `"text/plain"` is valid because it describes the old documents. Do not invent a default
that changes the historical meaning. Converters must be deterministic: use the old payload and fixed
compatibility rules, without database queries, external calls, current time, or random values.
Different actors or projections can call the converters concurrently, so they must also be
thread-safe. Pure functions that leave the input and shared state unchanged satisfy both requirements.

## Register names and conversions before consumers

Keep all three payload types readable, with distinct stable journal names. A stable name identifies
the serialized payload; it is not a request to convert its fields. Keep the old name mapped to the
old type so that deserialization can succeed before the upcaster runs.

F# registers stable names before creating the actor system. Register upcasters on the returned API
before registering any aggregates, sagas, or projections:

```fsharp
open FCQRS.FSharp

let startRuntime configuration loggerFactory connectionString =
    Fcqrs.journalTypes
        [ journalType<DocumentSavedV1> "document.saved.v1"
          journalType<DocumentSavedV2> "document.saved.v2"
          journalType<DocumentSavedV3> "document.saved.v3" ]

    let api =
        Fcqrs.actor configuration loggerFactory
            (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite connectionString)) "documents"

    let api = Fcqrs.withEventUpcaster<DocumentSavedV1, DocumentSavedV2> api v1ToV2
    let api = Fcqrs.withEventUpcaster<DocumentSavedV2, DocumentSavedV3> api v2ToV3
    // Register aggregates, sagas, and projections with this API next.
    api
```

<div class="cs-alt"></div>

```csharp
using FCQRS;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

var fcqrs = builder.Services
    .AddFcqrs(connectionString, "documents")
    .WithJournalTypes(types =>
    {
        types.Type<DocumentSavedV1>("document.saved.v1");
        types.Type<DocumentSavedV2>("document.saved.v2");
        types.Type<DocumentSavedV3>("document.saved.v3");
    })
    .WithEventUpcaster<DocumentSavedV1, DocumentSavedV2>(DocumentUpcasters.V1ToV2)
    .WithEventUpcaster<DocumentSavedV2, DocumentSavedV3>(DocumentUpcasters.V2ToV3);

// Continue with fcqrs.AddAggregate<...>(), sagas, and a projection before starting the host.
```

Complete C# builder registrations before building or resolving the host. The builder's configuration
freezes when dependency injection first resolves `IActor`, and the builder installs its conversions
before any registered consumer starts.

A payload without a stable name is stored under its CLR type name, without an assembly version, so
nodes on different FCQRS releases can read it during a rolling upgrade. Renaming or moving such a type
breaks its existing rows. The state rows and snapshots of sagas built with `Fcqrs.saga` or the C# saga
builder still use CLR names that include the saga's state type and its originator's event type, even
when those types have stable names. Keep those types in place while such sagas can recover.

Stable journal names use the existing process-wide registry. Upcasters are scoped to one actor
system; configuring another actor system does not add conversions to this one. The actor system's
conversion registry freezes when the first aggregate, saga, or projection initializes, so complete
any direct runtime registrations before that point too.

Chain resolution follows the payload types, independently of registration order. Each conversion
returns exactly one payload of a different type. Splitting an event, dropping it, or returning `null`
is unsupported. FCQRS allows one converter per source type and rejects cycles. A converter that
throws or returns `null` fails the read instead of skipping the event.

Converter failures follow the consumer's failure policy. Aggregate or saga recovery and ordinary
projections terminate the process under FCQRS's fail-fast policy. A transactional projection rolls
back the current event's transaction and faults `IProjection.Completion`; its checkpoint does not
advance past that event.

The source type is the envelope's declared payload type. For `Event<DocumentSavedV1>`, register
`DocumentSavedV1`. For an F# union or C# record hierarchy stored as `Event<DocumentEventV1>`, register
`DocumentEventV1` and convert all of its cases. Registering only a derived C# case does not match an
envelope whose payload parameter is the base type.

Consumers of the example's converted journal events must accept `Event<DocumentSavedV3>`. The
application chooses which type new command decisions persist; an upcaster does not change writers.

## Understand what is converted

FCQRS converts journal events after deserialization, before passing them into these paths:

- aggregate recovery;
- the ordinary and transactional projection handlers;
- historical originator events carried by FCQRS saga journal wrappers, including a starting event
  embedded in an FCQRS saga snapshot.

The journal rows are unchanged. Conversion preserves the event envelope's ID, creation date, sender,
correlation ID, persisted version, and metadata. Projection offsets, persistence IDs, and journal
sequence numbers also stay unchanged. One old event still represents one position in its history.

Upcasting does not run for live commands, published replies, or the live `Persisted` and `Deferred`
callbacks. It does not change standalone deserialization through the FCQRS serializer. A live
subscriber receiving `Event<DocumentSavedV1>` still receives that type; registering V1-to-V3 journal
conversions does not make the subscriber understand it.

Without stable names, older rows may carry CLR assembly and type names. Keep those identities
resolvable until the old payload has been decoded. For a type move that leaves its serialized shape
unchanged, keep the existing stable name mapped to the moved type. For a shape conversion such as
V1 to V2 above, retain separate old and new mappings. Never reuse the old name for incompatible data.

## Handle snapshots separately

An aggregate snapshot contains state produced by earlier folds. Recovery loads that state and replays
only the journal events after the snapshot. Registering an upcaster does not recalculate the history
already represented by that state.

Keep the snapshot state shape and meaning compatible, or plan and test a snapshot migration or full
recovery from retained journal history. If the new fold would derive different state from old events,
verify that loading an old snapshot and replaying its remaining events produces the same result as
replaying the complete converted history. `NoSnapshots` controls future automatic snapshotting; it
does not tell recovery to ignore an existing snapshot.

Application-owned saga data and state also remain unchanged. FCQRS can rebuild its own generic saga
wrappers around a converted historical originator event while retaining that application data. This
does not migrate the fields or meaning of a saved workflow state.

## Choose the change strategy

Use the smallest compatible strategy:

| Change | Strategy |
|---|---|
| New business outcome | Add a new event case |
| New data not required for old history | Add optional data or a new version with a valid historical default |
| Field renamed in source only | Keep its serialized name or provide compatibility decoding |
| A readable old payload needs a new representation | Register a one-to-one upcaster or chain |
| Field meaning changes | Create a new event case or version |
| Type moves namespace or assembly | Keep the registered journal name |
| Old event no longer affects current state | Keep a fold case that applies its historical meaning |

Do not change an old event's meaning to match a new rule. Replay must reproduce the decision that was
recorded at the time, not reinterpret history using today's rule.

## Keep readers ahead of writers

For a rolling deployment:

1. deploy readers and mappings for both old and new event shapes, with the required journal upcasters;
2. verify recovery, snapshots, projection replay, and live message compatibility on that version;
3. deploy code that starts writing the new shape;
4. remove old decoding only after no stored event or active node needs it.

Every participant must understand the live command, event, and saga message shapes exchanged during
the overlap. Journal upcasters do not convert pub-sub traffic or `ContinueOrAbort` wire types. The
reader rollout therefore needs an application compatibility plan for those messages too; installing
upcasters alone does not make a mixed-version cluster compatible.

## Add compatibility tests

Keep representative serialized events from previous releases as fixtures. Check that the old types
still deserialize, that V1-to-V2-to-V3 conversion preserves the historical facts, and that direct V2
conversion reaches the same representation.

Replay mixed V1, V2, and V3 histories through aggregate recovery and both projection styles. Assert
that envelope identities and versions survive conversion and that each source event produces one
result. Raw deserialization tests alone do not exercise the runtime's upcasting path.

Test recovery with old snapshots as well as full journal replay. For sagas, cover old starting-event
wrappers and snapshots while checking that workflow data remains compatible. Include mixed-node live
messages when the deployment will overlap versions.

## Rebuild derived data

When an event remains valid but a projection interpretation changes, leave the journal alone. Deploy
the corrected projection and [rebuild its read model](rebuild-a-read-model.html) with the required
conversion chain.

An upcaster does not revisit rows a projection has already committed. Stable names identify the
stored types; upcasters adapt readable historical payloads, and the application remains responsible
for their meaning and snapshot compatibility.
