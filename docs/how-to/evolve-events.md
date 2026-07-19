---
title: Evolve persisted events
category: How-to
categoryindex: 5
index: 4
---

# Evolve persisted events

Persisted events are data contracts. A deployment may read events written months ago, and a rolling
deployment may exchange messages between two application versions. Change event types with that
lifetime in mind.

## Register stable journal names

Without a stable mapping, a journal manifest can depend on the CLR assembly and type name. Register a
name before the actor system writes events:

```fsharp
open FCQRS.FSharp

Fcqrs.journalTypes
    [ journalType<Document.Event> "document.event"
      journalType<User.Event> "user.event" ]
```

```csharp
services
    .AddFcqrs(connectionString, "app")
    .WithJournalTypes(types =>
    {
        types.Type<DocumentEvent>("document.event");
        types.Type<UserEvent>("user.event");
    });
```

The mapping lets a type move to another namespace or assembly while old rows keep the same manifest.
Update the mapping to point the stable name at the new CLR type.

## Choose the change strategy

Use the smallest compatible strategy:

| Change | Strategy |
|---|---|
| New business outcome | Add a new event case |
| New data not required for old history | Add optional data or a new event version |
| Field renamed in source only | Keep its serialized name or provide compatibility decoding |
| Field meaning changes | Create a new event case or version |
| Type moves namespace or assembly | Keep the registered journal name |
| Old event no longer affects current state | Keep a fold case that applies its historical meaning |

Do not change an old event's meaning to match a new rule. Replay must reproduce the decision that was
recorded at the time, not reinterpret history using today's rule.

## Keep readers ahead of writers

For a rolling deployment:

1. deploy code that can read both the old and new event shapes;
2. verify recovery and projection replay on that version;
3. deploy code that starts writing the new shape;
4. remove old decoding only after no stored event or active node needs it.

If old and new nodes exchange commands or saga messages, both versions must understand every shape used
during the overlap.

## Add compatibility tests

Keep representative serialized events from the previous release as test fixtures. Verify that the new
code can deserialize them and fold them into the expected state. Also replay a mixed history containing
old and new event cases.

Test projections with the same mixed history. Aggregate recovery succeeding does not prove that every
read model understands the new event.

## Rebuild derived data

When an event remains valid but a projection interpretation changes, leave the journal alone. Deploy
the corrected projection and [rebuild its read model](rebuild-a-read-model.html).

Stable journal names handle type identity. They do not automatically migrate event fields or meanings.
Those changes remain part of the application's compatibility design.
