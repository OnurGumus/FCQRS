(**
---
title: 2. Wiring and running it
category: Tutorial
categoryindex: 3
index: 3
---
*)

(*** hide ***)
#r "nuget: FCQRS, 6.0.0-rc1"
open System
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Common
open FCQRS.Model.Data
open FCQRS.FSharp

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

module Document =
    open Values
    type Root =
        { Id: DocumentId; Title: Title; Content: Content }
        static member TryCreate(guid, title, content) =
            match Title.TryCreate title, Content.TryCreate content with
            | Ok t, Ok c -> Ok { Id = DocumentId.OfGuid guid; Title = t; Content = c }
            | Error e, _ -> Error e
            | _, Error e -> Error e
    type State = { Document: Root option; Version: int64 }
    let initial = { Document = None; Version = 0L }
    type Command = CreateOrUpdate of Root
    type Event = Updated of Root
    let decide (cmd: Command<Command>) state =
        match cmd.CommandDetails with
        | CreateOrUpdate doc -> Updated doc |> PersistEvent
    let fold (event: Event<Event>) state =
        match event.EventDetails with
        | Updated doc -> { state with Document = Some doc; Version = state.Version + 1L }

(**
# 2. Wiring and running it

Right now our [`Document`](1-the-aggregate.html) is a pair of pure functions with nowhere to live. They
can't remember anything between calls, and nobody can send them a command. This chapter fixes both, and
ends with a tiny demo that makes event sourcing *visible*: you'll run the program, run it again, and
watch a number go up because the past was waiting on disk. Keep adding to `Program.fs`.

## Two paths, on purpose: writing and reading

Here's the shape of the thing we're about to build:

<pre>
                decide / fold
  command  ---------------------->  JOURNAL   (append-only events; the truth)
                                       |
                                       v
                                   projection
                                       |
                                       v
                                  read model   (a derived view; disposable)
</pre>

The letters CQRS just mean: the path that **writes** and the path that **reads** are separate on
purpose. Writes flow through the aggregate and land in the **journal**, an append-only log of events
that is the single source of truth. Reads come from a **read model** *derived* from that log.

> 💡 **Mental model.** Treat the journal like a bank's transaction ledger and the read model like the
> balance shown in your banking app. The ledger is authoritative and never edited; the balance is just a
> convenient running total computed from it. Lose the displayed balance and you recompute it from the
> ledger — you've lost nothing. Lose the ledger and you've lost everything. That asymmetry is the whole
> design: **the read model is disposable; the journal is not.**

## Create the actor system

`Fcqrs.actor` builds the entire Akka.NET system from plain values. The only thing you're *required* to
hand it is a database connection (via `Fcqrs.connect`). Everything else — serializers, sharding,
snapshots, a one-node cluster that joins itself — comes from defaults baked into the package.
*)

let buildApi () : IActor =
    let config = ConfigurationBuilder().Build()
    let loggerFactory = LoggerFactory.Create(fun _ -> ())
    let connection =
        Fcqrs.connect FCQRS.Actor.DBType.Sqlite "Data Source=tutorial.db;"
    Fcqrs.actor config loggerFactory (Some connection) "tutorial"

(**
<div class="cs-alt"></div>

```csharp
// C#: the DI host-builder creates the actor system and registers the aggregate;
// config and logger come from the container, so there's no explicit buildApi.
var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddFcqrs("Data Source=tutorial.db;", "tutorial")
    .AddAggregate<DocumentAggregate, DocumentState, DocumentCommand, DocumentEvent>();
```
*)

(**
Notice the config is an *empty* `ConfigurationBuilder().Build()`. If you've used Akka.NET before, this
is the part to do a double-take at:

> 🤔 **Did you know?** Akka.NET is normally configured with HOCON — often pages of it for persistence,
> serialization, and sharding. FCQRS ships those defaults embedded and merges anything you supply over
> them, so the *minimum* is no HOCON file at all. (When you eventually need to override a knob, you can —
> see [Configuration](../configuration.html). The default is "nothing," not "you can't.")

That `tutorial.db` file **is** the journal, and the fact that it's a real file on disk is exactly what
makes the demo at the end work — it outlives the process. (Swapping `DBType.Sqlite` for Postgres or SQL
Server is a one-line change; the rest of your code doesn't notice. See
[Configure the database](../how-to/configure-the-database.html).)

## A read side to listen on

A **projection** is a function run once per event, in order. A real one folds each event into a SQL
table you can query; ours does the smallest thing possible — nothing — because we need no read model
yet. FCQRS still publishes each aggregate event to subscribers, which is what the next step waits on:
*)

let handle (_offset: int64) (_event: obj) = ()   // no read model yet — events auto-publish

(**
<div class="cs-alt"></div>

```csharp
// C#: a projection handler is just void — update the read model; FCQRS publishes
// each aggregate event to subscribers for you.
public static void Handle(long offset, object ev) { /* no read model yet */ }

builder.Services.AddProjection((offset, ev) => Handle(offset, ev));
```
*)

(**
Why does an empty handler still work? Because with `Projection.single`, FCQRS **re-publishes each
aggregate event to subscribers** after your handler runs — and that re-publish is what lets a caller
know its write reached the read side. That's the mechanism behind the next step. (A production
projection writes to a table and tracks its offset — its own how-to:
[Add a projection](../how-to/add-a-projection.html).)

## Send a command — and read your own write

Now the subtle part, and the reason well-built CQRS systems feel solid instead of flaky. Writes are
asynchronous: the command is handled, the event is journaled, and *then* it propagates to the read side
a beat later. So the naïve "save, then immediately query" can read **stale data** — the classic bug
where you create something, redirect to its page, and it's not there yet.

You might reach for a `Thread.Sleep` or a retry loop. Don't — there's an exact signal. FCQRS threads a
**correlation id (CID)** through the whole round trip, and the move is:

<pre>
  mint a CID
      |
      v
  SUBSCRIBE to that CID    &lt;-- before sending, so the answer can't slip past
      |
      v
  send command  -->  aggregate  -->  journal  -->  projection re-publishes  -->  your wait wakes
</pre>

> 🎯 **Key principle.** Subscribe *before* you send. If you subscribed afterward, the event could be
> processed in the gap and you'd wait forever for a notification that already fired. Subscribe-then-send
> closes that window by construction — the same reason you start recording before the rocket launches,
> not after.

`Fcqrs.aggregate` registers the aggregate and returns a handle whose `.Send` ties it together: you give
it the CID, the aggregate id, the command, and a predicate for the event you're waiting on.
*)

let run () =
    async {
        let api = buildApi ()

        let documents =
            Fcqrs.aggregate api
                { Name = "Document"
                  Initial = Document.initial
                  Decide = Document.decide
                  Fold = Document.fold
                  Snapshots = Default }

        // No sagas yet — the saga-starter still has to be wired, with none. (Ch. 3 adds one.)
        Fcqrs.wireSagaStarters api []

        let subs = Fcqrs.projection api (Projection.single 0 handle)

        let cid = Fcqrs.newCid ()
        // A FIXED id, so every run addresses the SAME document — that's what lets the version climb.
        let id = Fcqrs.aggregateId "11111111-1111-1111-1111-111111111111"

        // Subscribe to this CID *before* sending so the confirmation can't be missed.
        use awaiter = subs.Subscribe(cid, 1)

        match Document.Root.TryCreate(Guid "11111111-1111-1111-1111-111111111111", "Welcome", "draft") with
        | Error e -> printfn "rejected: %s" e
        | Ok doc ->
            let! event =
                documents.Send cid id (Document.CreateOrUpdate doc)
                    (fun e ->
                        match e with
                        | Document.Updated _ -> true)

            do! awaiter.Task |> Async.AwaitTask // read side is now up to date
            printfn "saved %A at version %A" event.EventDetails event.Version
    }

(**
<div class="cs-alt"></div>

```csharp
// C#: resolve the command handler + subscription from DI, then the same
// subscribe-before-send, read-your-writes flow.
var app = builder.Build();
var documents = app.Services.GetRequiredService<Handler<DocumentCommand, DocumentEvent>>();
var subs = app.Services.GetRequiredService<ISubscribe>();

var cid = Values.NewCID();
var id = Values.CreateAggregateId("11111111-1111-1111-1111-111111111111");

using var awaiter = subs.SubscribeForFirst(cid);   // subscribe BEFORE sending
if (Document.TryCreate(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Welcome", "draft", out var doc, out var err))
{
    var ev = await documents(
        e => e is DocumentEvent.Updated,
        cid, id, new DocumentCommand.CreateOrUpdate(doc));
    await awaiter.Task;                             // read side is now up to date
    Console.WriteLine($"saved {ev.EventDetails} at version {ev.Version}");
}
```
*)

(**
Wire it to your entry point:

```fsharp
[<EntryPoint>]
let main _ =
    run () |> Async.RunSynchronously
    0
```

## Run it, then run it again

This is the moment the whole tutorial has been building toward, so do it for real:

```bash
dotnet run
# saved Updated ... at version 1

dotnet run
# saved Updated ... at version 2
```

Sit with what just happened. Between the two runs the process **exited completely** — every byte of
in-memory state was thrown away. Yet the second run reports version `2`. Nobody wrote "load the
document" code, because there isn't any to write. On startup FCQRS found the document's one stored event,
replayed it through your `fold` (`0 → 1`), and *then* applied the new write (`1 → 2`). The number climbs
because the journal persisted across the restart and `fold` is pure enough to reconstruct the exact same
state every time.

> 💡 **Try this.** Delete `tutorial.db*` and run again — you're back to version `1`. That's the asymmetry
> from the start of the chapter made tangible: erase the derived state and it rebuilds from the log;
> there's nothing else to lose because the log *was* the truth.

⚠️ And the honest counterweight: for one document with one field, this is a lot of moving parts to print
a number. You wouldn't reach for it here. What you just watched scales, though — to thousands of
entities that must stay consistent under concurrent edits, each with a perfect audit trail and free
rebuildable views. You got all three without writing the persistence, the loading, or the concurrency
control. *That's* the trade you're actually evaluating, not the line count of a hello-world.

## What you now understand

A command doesn't change anything by itself — it produces an event, the event lands in a durable log,
and everything else (current state, read models, the answer to your caller) is *derived* from that log.
You also met the one piece of discipline that makes async writes feel synchronous to a caller:
subscribe to a correlation id, then send, then wait.

## Common mistakes

- **Querying right after sending, without waiting on the CID.** Intermittent "it's not there yet" bugs
  that only show under load. Subscribe-then-send-then-await is the fix.
- **Subscribing *after* sending.** The event can fire in the gap; your wait never completes. Always
  subscribe first.
- **Treating the read model as precious.** It's rebuildable from the journal — back up the journal, not
  the read model.
- **Forgetting `Fcqrs.wireSagaStarters`.** Even with zero sagas the starter must be wired (with `[]`);
  chapter 3 is where it earns a real argument.

## Further study

- [The read side](../concepts/read-models.html) — projections, offsets, and why a read model can always
  be thrown away and rebuilt.
- [Consistency and recovery](../concepts/consistency-and-recovery.html) — correlation ids,
  read-your-writes, snapshots, and what happens across a restart.
- [Configure the database](../how-to/configure-the-database.html) — point the journal at Postgres or SQL
  Server with a one-line change.

Next, a rule a single aggregate structurally *cannot* enforce — a per-user quota — and the
[saga](3-adding-a-saga.html) that coordinates the two aggregates it takes.
*)
