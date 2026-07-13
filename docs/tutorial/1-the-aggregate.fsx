(**
---
title: 1. The aggregate
category: Tutorial
categoryindex: 3
index: 2
---
*)

(*** hide ***)
#r "nuget: FCQRS, 6.0.0-preview28"
open System
open FCQRS.Common
open FCQRS.Model.Data
open FCQRS.FSharp

(**
# 1. The aggregate

Think about the last time you edited a document and wished you could see *who* changed that one
paragraph, and *when*. An ordinary database can't tell you — it overwrote the old value the instant you
hit save. The current state is all it keeps; the story of how you got there is gone. Event sourcing is
the decision to keep the story instead, and rebuild the current state from it whenever you need it. This
chapter is where that decision becomes code — and, pleasantly, it's the part with no actors, no
database, and no async at all. Just types and two pure functions you can run in your head.

## Commands and events are not the same thing

Let's start with the single idea everything else hangs off. In CRUD you have one verb — *save* — that
both decides and records in one breath. Event sourcing pulls those apart into two different things, and
once you see the seam you can't unsee it.

A **command** is a request, phrased in the imperative: "create or update this document." It's a wish.
It can be turned down. An **event** is a fact, phrased in the past tense: "this document *was* updated."
It already happened, and you never un-happen a fact — you can only record a new one that corrects it.

> 💡 **Mental model.** A command is someone asking; an event is what the record will forever say
> happened. The bank teller hears "withdraw \$100" (command); the ledger gets "withdrew \$100" — or
> "declined: insufficient funds" (event). The request and the recorded outcome are different sentences,
> and the ledger only ever keeps the second kind.

That's why they're deliberately *different sets*. One `CreateOrUpdate` command might produce an
`Updated` fact on a good day and a `Rejected` one on a bad day. If commands and events were the same
type you'd be quietly assuming every request succeeds — which is exactly the assumption that makes CRUD
code lie to you later.

The thing that receives a command, decides, and emits the event is an **aggregate**: the consistency
boundary for one entity — one document, one account, one order. In FCQRS an aggregate is an actor that
handles one command at a time, so there are no locks and no races inside it. But hold that lightly:

> 🎯 **Key principle.** The aggregate is *a consistency boundary with a pure decision function at its
> centre.* "It's an actor" is how FCQRS implements the boundary; it isn't the idea. We won't touch the
> actor machinery until chapter 2 — and the decision function we write here would be just as correct if
> the boundary were a lock or a database transaction instead.

⚠️ Is all this worth it for a notes app you'll delete next week? Honestly, no — plain CRUD is less code,
and you should reach for it. The calculus flips the moment "what changed, when, and in what order" has
real value: money, approvals, anything several people edit at once. Then keeping the events stops being
ceremony and *becomes the feature* — audit, undo, and rebuildable views all fall out of it for free.

Open `Program.fs` in the project from the [tutorial intro](index.html) and follow along. The opens
first:

```fsharp
open System
open FCQRS.Common
open FCQRS.Model.Data
open FCQRS.FSharp
```

## Make the illegal values impossible to type

Before the document, its raw materials: a title and a body. We *could* use bare `string`s — and for a
prototype that's a fine shortcut. But a bare string lets an empty title through, and lets a caller swap
title and body without the compiler noticing. The fix is an old DDD habit: **validate once, at the
edge, and bake the result into the type.** After that, holding a `Title` *is* the proof it's a valid
title; nothing downstream has to re-check.

FCQRS hands you two validated primitives for this — `ShortString` and `LongString` — built through
`ValueLens`, which returns a `Result` so bad input can't sneak past. We wrap each in its own domain type
so the compiler will never let a `Content` stand in for a `Title`:
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

(**
<div class="cs-alt"></div>

```csharp
// C#: validated value objects wrap FCQRS's ShortString / LongString.
using static FCQRS.Model.CSharp;   // StringTypes
using static FCQRS.Model.Data;      // ShortString, LongString
using System.Diagnostics.CodeAnalysis;

public readonly record struct DocumentId(Guid Value)
{
    public static DocumentId OfGuid(Guid g) => new(g);
    public override string ToString() => Value.ToString();
}

public readonly record struct Title(ShortString Value)
{
    public static bool TryCreate(string s, [NotNullWhen(true)] out Title result)
    {
        if (StringTypes.TryCreateShortString(s, out var v)) { result = new Title(v); return true; }
        result = default; return false;
    }
}

public readonly record struct Content(LongString Value)
{
    public static bool TryCreate(string s, [NotNullWhen(true)] out Content result)
    {
        if (StringTypes.TryCreateLongString(s, out var v)) { result = new Content(v); return true; }
        result = default; return false;
    }
}
```
*)

(**
`ShortString` and `LongString` are just FCQRS's ready-made validated strings (they also serialize
cleanly into the event log, which matters later). The wrapping in `Title`/`Content` is plain F# you'd
write the same way with any framework — the principle is "parse, don't validate," and the framework only
supplies the validated primitive underneath.

> 🤔 **Did you know?** This is why `TryCreate` returns a `Result` rather than throwing. A thrown
> exception on bad input would force every caller into a try/catch; a `Result` makes "this might be
> rejected" part of the type, so the compiler reminds you to handle the rejection at the one place it
> can happen — the edge.

## State, command, event

Now the document itself. `Root` is the content as it travels on the wire, with a smart constructor that
validates title and body *together* and hands back a single `Result`. An aggregate should never have to
reason about half-valid input, and this is where we guarantee it won't:
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

(**
`State` is what the aggregate folds events into and keeps in memory. A document we've never heard of has
no content yet and sits at version `0`:
*)

    type State = { Document: Root option; Version: int64 }
    let initial = { Document = None; Version = 0L }

(**
And the command/event pair. Right now there's exactly one of each — every write is a `CreateOrUpdate`
that yields an `Updated`.

You might wonder: *if there's only one case each, why bother splitting command from event at all?*
Because the split isn't about how many cases you have today — it's about keeping the *request* and the
*record* free to diverge tomorrow. In [chapter 3](3-adding-a-saga.html) `CreateOrUpdate` will start
producing several different events depending on a quota check, and the code here won't have to be
reshaped to allow it. Starting with one case keeps the moving parts visible; the seam is already in the
right place.
*)

    type Command = CreateOrUpdate of Root

    type Event = Updated of Root

(**
<div class="cs-alt"></div>

```csharp
// C#: the root + state are records; command and event are C# 15 unions.
public sealed record Document(DocumentId Id, Title Title, Content Content)
{
    public static bool TryCreate(Guid id, string title, string content,
        [NotNullWhen(true)] out Document? result, [NotNullWhen(false)] out string? error)
    {
        if (!Title.TryCreate(title, out var t)) { result = null; error = "Invalid title"; return false; }
        if (!Content.TryCreate(content, out var c)) { result = null; error = "Invalid content"; return false; }
        result = new Document(DocumentId.OfGuid(id), t, c); error = null; return true;
    }
}

public record DocumentState(Document? Document = null, long Version = 0)
{
    public static readonly DocumentState Initial = new();
}

public union DocumentCommand(DocumentCommand.CreateOrUpdate)
{
    public record CreateOrUpdate(Document Document);
}

public union DocumentEvent(DocumentEvent.Updated)
{
    public record Updated(Document Document);
}
```
*)

(**
## `decide`: the one function that earns its keep

Here's the heart of the write side. `decide` takes the command — wrapped in a `Command<_>` envelope
that carries metadata like a timestamp and a correlation id — and the current state, and returns an
**action**. Read that word carefully: it returns a *description of what should happen*, not a mutation.
It doesn't write to anything. It decides.
*)

    let decide (cmd: Command<Command>) state =
        match cmd.CommandDetails with
        | CreateOrUpdate doc -> Updated doc |> PersistEvent

(**
<div class="cs-alt"></div>

```csharp
// C#: decide is the aggregate's HandleCommand method (a switch expression).
public override EventAction<DocumentEvent> HandleCommand(
    Command<DocumentCommand> cmd, DocumentState state) =>
    cmd.CommandDetails switch
    {
        DocumentCommand.CreateOrUpdate c =>
            EventActions.Persist<DocumentEvent>(new DocumentEvent.Updated(c.Document)),
        _ => EventActions.Ignore<DocumentEvent>()
    };
```
*)

(**
The two actions you'll reach for constantly are a study in contrast:

**`PersistEvent e`** is the happy path — append `e` to the log, bump the version, fold it into state,
and publish it. Return this when something genuinely happened.

**`DeferEvent e`** publishes `e` as an *answer* without storing it. The state and version don't move,
nothing lands in the log. This is the right call for rejections — `AlreadyExists`, `Errored`. The caller
still needs to hear "no," but a refusal isn't a fact that changed the entity, so letting it into the
permanent history would be a lie.

> ⚠️ **Common mistake.** Persisting your rejections "so there's a record of them." It feels tidy, but
> now `fold` has to pattern-match events that mean *nothing changed*, your version counter inflates on
> failed attempts, and a future replay re-applies non-events. Keep history to things that actually
> happened; deliver the "no" with `DeferEvent`. (Our single case is a plain `PersistEvent` because here
> every write is a real change — we'll use `DeferEvent` in chapter 3.)

There are two more actions, `IgnoreEvent` and `UnhandledEvent`, for "do nothing" and "I don't handle
this here" — you'll meet both in chapter 3.

## `fold`: rebuild the present from the past

State is *not* the source of truth and is never stored. It's a cache, reconstructed by replaying every
event through `fold`. Which means this function lives a double life: it runs once when a brand-new event
is persisted, and it runs again, many times, when old events are replayed to rebuild state after a
restart. Those two lives **must** produce identical results.
*)

    let fold (event: Event<Event>) state =
        match event.EventDetails with
        | Updated doc -> { state with Document = Some doc; Version = state.Version + 1L }

(**
<div class="cs-alt"></div>

```csharp
// C#: fold is the aggregate's ApplyEvent method.
public override DocumentState ApplyEvent(Event<DocumentEvent> evt, DocumentState state) =>
    evt.EventDetails switch
    {
        DocumentEvent.Updated e => state with { Document = e.Document, Version = state.Version + 1 },
        _ => state
    };
```
*)

(**
That `Version + 1L` is small but it's the whole magic trick. Each replayed event re-runs this fold, so
when you restart the app next chapter and the document's one stored event replays, the version ticks
from `0` to `1` again — and a fresh write takes it to `2`. The version literally counts how many facts
are in the log.

> 🎯 **Key principle.** `fold` must be pure — no clock, no `Random`, no I/O. The instant it depends on
> something that changes between runs, replaying the same events produces a *different* state, and event
> sourcing's core promise ("the log fully determines the present") quietly breaks. If you need the
> current time, the *command* captures it and the *event* carries it; `fold` only ever reads what the
> event already holds.

## Bind the functions to an actor

Everything above is plain F# you could lift into any project. The framework shows up in exactly one
place: `Fcqrs.aggregate` takes a record describing the aggregate and **registers it** — spinning up the
cluster-sharding region behind the scenes — and hands back a typed *handle* we'll use to talk to it in
chapter 2.
*)

    let register (api: IActor) =
        Fcqrs.aggregate api
            { Name = "Document"
              Initial = initial
              Decide = decide
              Fold = fold
              Snapshots = Default }        // snapshot cadence: Default | NoSnapshots | Every n

(**
<div class="cs-alt"></div>

```csharp
// C#: HandleCommand/ApplyEvent live on a class deriving Aggregate<>, registered
// through the DI host-builder (the C# counterpart of Fcqrs.aggregate).
public sealed class DocumentAggregate : Aggregate<DocumentState, DocumentCommand, DocumentEvent>
{
    public override DocumentState InitialState => DocumentState.Initial;
    public override string EntityName => "Document";
    // HandleCommand and ApplyEvent as shown above.
}

services
    .AddFcqrs("Data Source=tutorial.db;", "tutorial")
    .AddAggregate<DocumentAggregate, DocumentState, DocumentCommand, DocumentEvent>();
```
*)

(**
Notice what you *didn't* write: no base class to inherit, no attributes to sprinkle, no actor lifecycle
to manage. Those four fields — `Name`, `Initial`, `Decide`, `Fold` — are the entire contract between
your domain and the framework.

## What you now understand

You came in thinking of "save" as one action. You're leaving with it split into a *command* (a request
that might be refused) and an *event* (a fact you keep forever), joined by a pure `decide` that chooses
between them and a pure `fold` that rebuilds the present by replaying the past. That's not FCQRS
trivia — it's the shape of every event-sourced aggregate in any language.

And because there's no Akka in any of it, you can test the valuable part right now with ordinary
function calls:

```fsharp
let doc = Document.Root.TryCreate(System.Guid.NewGuid(), "Spec", "draft") |> Result.value
// decide returns an action, not a mutation — so you assert on the action:
let action = Document.decide (cmd (Document.CreateOrUpdate doc)) Document.initial
// => PersistEvent (Updated doc)
```

## Common mistakes

- **Putting a clock or random value in `fold`.** It passes every test that doesn't restart the process,
  then corrupts state on the first replay. If the value can change between runs, capture it in the
  command and carry it in the event.
- **Reaching for `PersistEvent` on rejections.** Rejections are answers, not history — `DeferEvent`.
- **Using bare strings for domain values in anything but a throwaway.** The empty title gets in, the
  swapped arguments compile, and you debug it in production instead of at the type boundary.
- **Treating `State` as the source of truth.** It's a derived cache. The event log is the truth; `State`
  is just what's convenient to hold in memory.

## Further study

- [Aggregates and the write side](../concepts/aggregates.html) — the long-form reasoning behind
  command/event and why the boundary is an actor.
- [CQRS and event sourcing](../concepts/cqrs-and-event-sourcing.html) — why storing events instead of
  current state changes everything downstream.
- [Test your domain](../how-to/test-your-domain.html) — the tiny `cmd`/`evt` helpers used above, with a
  full set of assertions.

Next, we give these functions a running home and watch the version climb across restarts:
[wiring and running it](2-running-it.html).
*)
