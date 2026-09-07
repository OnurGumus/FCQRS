(**
---
title: 3. Edit your document
category: Learn FCQRS
categoryindex: 2
index: 5
---
*)

(*** hide ***)
#r "nuget: FCQRS, 6.3.0"
#load "../../samples/getting-started-fsharp/Document.fs"
#load "../../samples/getting-started-fsharp/Publication.fs"
#load "../../samples/getting-started-fsharp/Checks.fs"

(**
# 3. Edit your document

Your document contains `first event` at version `1`. An edit is a different request from a create:
it changes an existing document's content.

## Change the document you already saved

Use the document ID from the quickstart. Replace `DOCUMENT_ID` below and keep the quotes around the
content so the shell passes it as one argument.

```text
dotnet run --project samples/getting-started-fsharp -- --edit DOCUMENT_ID "second draft"
```

<div class="cs-alt" data-fs="text" data-cs="text"></div>

```text
dotnet run --project samples/getting-started-csharp -- --edit DOCUMENT_ID "second draft"
```


```text
edited version 2; query returned 'second draft'
```

The original `DocumentCreated` event is still in SQLite. The edit adds `DocumentEdited`; applying both
events in order produces the new state. The projection follows the same history and updates its view.

<pre>
version 1: DocumentCreated(..., "first event")
version 2: DocumentEdited(id, "second draft")
                       |
                       v
            current content: "second draft"
</pre>

## Repeat the edit, then restart

**Predict:** run the same edit command again. The result is:

```text
edit reply version 2; document contains 'second draft'
```

There is no new event because the requested content already matches the current content. The runner
prints the aggregate reply on this path; it does not wait for a journal projection notification that
will never arrive.

Now start another process with `--recover DOCUMENT_ID`, as in chapter 2:

```text
recovery reply version 2; document contains 'second draft'
```

Creation and editing now compose into one recoverable history.

## Follow the edit decision

These are the edit cases in the same `decide` / `HandleCommand` function you read in chapter 1:

<!-- sample: fsharp Document.fs edit -->
```fsharp
| EditDocument(id, content), Some document, None when document.Id = id ->
    if System.String.IsNullOrWhiteSpace content then
        DocumentRejected "Content must not be blank" |> DeferEvent
    elif document.Content = content then
        DocumentEdited(id, content) |> DeferEvent
    else
        DocumentEdited(id, content) |> PersistEvent
| EditDocument _, None, _ -> DocumentRejected "Document does not exist" |> DeferEvent
| EditDocument _, _, _ -> DocumentRejected "Editing closes when publication starts" |> DeferEvent
```

<div class="cs-alt"></div>

<!-- sample: csharp Document.cs edit -->
```csharp
(EditDocument edit, { } document, null) when edit.Id == document.Id =>
    string.IsNullOrWhiteSpace(edit.Content)
        ? EventActions.Defer<DocumentEvent>(new DocumentRejected("Content must not be blank"))
        : edit.Content == document.Content
            ? EventActions.Defer<DocumentEvent>(new DocumentEdited(edit.Id, edit.Content))
            : EventActions.Persist<DocumentEvent>(new DocumentEdited(edit.Id, edit.Content)),
(EditDocument, null, _) => EventActions.Defer<DocumentEvent>(new DocumentRejected("Document does not exist")),
(EditDocument, _, _) => EventActions.Defer<DocumentEvent>(new DocumentRejected("Editing closes when publication starts")),
```


The rules are explicit: the document must exist, content must not be blank, and editing is permitted
before publication starts. The final rule will matter in the next chapter, where a published
version must remain fixed while its URL is being reserved.

**Try it:** send `--edit missing-document "draft"`. The reply is
`edit rejected: Document does not exist`; the rejection adds no event. An empty string produces a
blank-content rejection for an existing editable document.

Repeating the latest content is safe here. An old edit retried after a newer edit could overwrite
that newer content. Applications that need protection against out-of-order retries should add a
request ID or an expected-version rule. This sample's publication saga never issues edit commands.

## Handle both success and rejection

The runner subscribes before sending, inspects the aggregate reply, and waits only for a stored edit.
`Journaled` is a delivery flag on the reply envelope; it identifies whether that command stored an
event. It is separate from the persisted version.

<!-- sample: fsharp Program.fs edit-request -->
```fsharp
let correlationId = Fcqrs.newCid ()
use projected = subscriptions.Subscribe(correlationId, 1)
let! reply = documents.Send correlationId aggregateId (EditDocument(documentId, content)) (fun _ -> true)
match reply.EventDetails with
| DocumentEdited(_, content) ->
    if reply.Journaled = Some true then
        do! projected.Task.WaitAsync(TimeSpan.FromSeconds 30.) |> Async.AwaitTask
        printfn "edited version %A; query returned '%s'" reply.Version readModel[documentId].Content
    else printfn "edit reply version %A; document contains '%s'" reply.Version content
| DocumentRejected reason -> printfn "edit rejected: %s" reason
| other -> failwithf "Unexpected edit reply: %A" other
```

<div class="cs-alt"></div>

<!-- sample: csharp Program.cs edit-request -->
```csharp
var correlationId = Values.NewCID();
using var projected = subscriptions.SubscribeForFirst(correlationId);
var reply = await documents(_ => true, correlationId, aggregateId, new EditDocument(documentId, content));
switch (reply.EventDetails)
{
    case DocumentEdited edited:
        if (reply.Journaled?.Value == true)
        {
            await projected.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Console.WriteLine($"edited version {reply.Version}; query returned '{readModel[documentId].Content}'");
        }
        else Console.WriteLine($"edit reply version {reply.Version}; document contains '{edited.Content}'");
        break;
    case DocumentRejected rejected: Console.WriteLine($"edit rejected: {rejected.Reason}"); break;
    default: throw new InvalidOperationException("Unexpected edit reply");
}
```


A new stored edit needs the projection wait before querying. A deferred edit or rejection does not
have a new journal event. The timeout bounds the wait; it does not prove the command failed.

## Represent more than one event in stable C#

F# adds cases to `DocumentCommand` and `DocumentEvent`. C# uses an abstract record as the common type,
with ordinary derived records for the cases. The `JsonDerivedType` attributes give those cases explicit
names in serialized JSON. They require no preview compiler.

For example, `DocumentCreated` and `DocumentEdited` are different `DocumentEvent` values. The handler
and projection accept `Event<DocumentEvent>` and select the concrete case by pattern matching.
The complete declarations are in `Document.cs`; adding a new case means updating its declaration,
serialization registration, decision, fold, and projection.

The F# creation case keeps its original serialized shape. The C# sample includes a reader for older
`Event<DocumentCreated>` envelopes, so a document created with the earlier sample remains usable.
[Testing and evolution](4-testing-and-evolution.html) explains that compatibility boundary.

Continue to [4. Publish under a unique URL](3-adding-a-saga.html). Keep the same document ID:
its edited content is the version you will publish.

*)

(*** hide ***)
Checks.documentChecks ()
printfn "Evaluated docs/tutorial/3-edit-your-document.fsx"
