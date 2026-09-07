(**
---
title: 1. Make one document decision
category: Learn FCQRS
categoryindex: 2
index: 3
---
*)

(*** hide ***)
#r "nuget: FCQRS, 6.3.0"
#load "../../samples/getting-started-fsharp/Document.fs"
#load "../../samples/getting-started-fsharp/Publication.fs"
#load "../../samples/getting-started-fsharp/Checks.fs"

(**
# 1. Make one document decision

The first create saved `first event`. The second create asked for `replacement attempt`, but returned
`first event` at the same version. Keep the sample from the [quickstart](../get-started.html) open.

The rule is **the first create wins for a document ID**. A create cannot overwrite an existing document.

## Name the three values

| Value | Example | Meaning |
|---|---|---|
| Command | `CreateDocument` carrying a document | A request that may or may not change history. |
| Event | `DocumentCreated` carrying that document | The outcome recorded when creation succeeds. |
| State | A document, or no document yet | The value used to decide the next request. |

In F#, `None` means there is no document and `Some document` means one is present. C# uses a nullable
document for the same distinction. Both versions start with no document.

`DocumentCommand` groups the requests the document understands. `DocumentEvent` groups its replies
and recorded outcomes. The sample includes editing and publication cases for later chapters; start
with the two create cases below.

## Read the decision before the setup

Find `decide` in F#, or `HandleCommand` in C#. These create cases inspect the request, current document,
and publication status. `_` means that a value does not affect this decision. In C#, `{ } existing`
matches a non-null document and gives it the name `existing`.

<!-- sample: fsharp Document.fs create -->
```fsharp
| CreateDocument document, None, _ -> DocumentCreated document |> PersistEvent
| CreateDocument _, Some existing, _ -> DocumentCreated existing |> DeferEvent
```

<div class="cs-alt"></div>

<!-- sample: csharp Document.cs create -->
```csharp
(CreateDocument create, null, _) => EventActions.Persist<DocumentEvent>(new DocumentCreated(create.Document)),
(CreateDocument, { } existing, _) => EventActions.Defer<DocumentEvent>(new DocumentCreated(existing)),
```


| Current document | Incoming content | Reply content | New event stored? |
|---|---|---|---|
| Absent | `first event` | `first event` | Yes |
| Contains `first event` | `replacement attempt` | **first event** | No |

The second case returns the existing document. Returning the incoming payload there would let a
repeated create change state without recording the change.

`Command<T>` and `Event<T>` are FCQRS **envelopes** around your values. They carry context such as
correlation id and version. The decision reads the request from `CommandDetails`; the fold reads the
outcome from `EventDetails`. The runtime constructs these envelopes for the application.

## Storing and replying have different effects

`PersistEvent` (`EventActions.Persist` in C#) appends the event to the **journal**, the ordered event
history. FCQRS increments the version, folds the event into state, and publishes it.

`DeferEvent` (`EventActions.Defer`) folds and publishes a reply without storing it or increasing the
persisted version. Here it returns `DocumentCreated` with the existing document as a repeated verdict.
It does not record another creation.

The creation case in `fold` / `ApplyEvent` puts that document into state. Returning the same document
on the deferred path therefore leaves state unchanged. A state change caused only by a deferred
reply would disappear on recovery because the journal contains no record of it.

## Check the rule without a database

Both functions are **pure**: their results depend on their inputs, without database or network work.
Run the sample's checks from the repository root:

```text
dotnet run --project samples/getting-started-fsharp -- --check
```

<div class="cs-alt" data-fs="text" data-cs="text"></div>

```text
dotnet run --project samples/getting-started-csharp -- --check
```


```text
All document, replay, and saga checks passed.
```

The checks also cover later chapters. Start with the creation checks in `Checks.fs` / `Checks.cs`:

<!-- sample: fsharp Checks.fs checks -->
```fsharp
let original = { Id = "notes"; Title = "FCQRS notes"; Content = "first event" }
let replacement = { original with Content = "replacement attempt" }
equal (PersistEvent(DocumentCreated original))
    (decide (command (CreateDocument original)) initial) "First create must store"
let created = fold (event 1L (DocumentCreated original)) initial
equal (DeferEvent(DocumentCreated original))
    (decide (command (CreateDocument replacement)) created) "Repeated create must preserve the original"
equal created (fold (event 1L (DocumentCreated original)) created) "Repeated reply must not change state"
```

<div class="cs-alt"></div>

<!-- sample: csharp Checks.cs checks -->
```csharp
var original = new Document("notes", "FCQRS notes", "first event");
var replacement = original with { Content = "replacement attempt" };
Equal(EventActions.Persist<DocumentEvent>(new DocumentCreated(original)),
    aggregate.HandleCommand(Command(new CreateDocument(original)), DocumentState.Initial), "First create must store");
var created = aggregate.ApplyEvent(Event(new DocumentCreated(original)), DocumentState.Initial);
Equal(EventActions.Defer<DocumentEvent>(new DocumentCreated(original)),
    aggregate.HandleCommand(Command(new CreateDocument(replacement)), created), "Repeated create must preserve the original");
Equal(created, aggregate.ApplyEvent(Event(new DocumentCreated(original)), created), "Repeated reply must not change state");
```


`command` / `Command` and `event` / `Event` are helpers in that file using `TestEnvelope` to supply
the framework fields. `equal` / `Equal` throws when the actual result differs from the expected one.

**Try it:** change the repeated-create branch to return the incoming document. Run `--check` again.
The repeated-create check fails. Restore the branch and confirm that the checks pass.

## Recover the same decision

FCQRS runs `fold` / `ApplyEvent` both after storing a new event and when recovering an aggregate.
Applying the same recorded history must produce the same state. This is **event sourcing**: state is
derived from retained events.

The fold must not read the clock, generate random values, or fetch external data. Record a needed
value in the event so replay uses the original value.

**Predict:** replay the original creation into empty state, then try a create with different content.
The second row of the table applies, just as it did before the program exited.

## Where the rule holds

One actor processes one document's commands sequentially. Two concurrent creates for the same
aggregate ID cannot both make their decision from empty state during normal processing. Different
document IDs have different actors and can run concurrently.

The sample treats repeated creates as requests to return the current document. An application that
must distinguish identical retries from conflicting requests needs an explicit conflict reply.
Input validation is also an application responsibility; the initial strings here are fixed sample data.

Continue to [2. Restart, project, and query](2-running-it.html) to check the recovery prediction.

*)

(*** hide ***)
Checks.documentChecks ()
printfn "Evaluated docs/tutorial/1-the-aggregate.fsx"
