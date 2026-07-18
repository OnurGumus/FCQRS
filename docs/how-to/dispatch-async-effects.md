---
title: Dispatch async effects (mini saga)
category: How-to
categoryindex: 5
index: 8
---

# Dispatch async effects without a saga

Sometimes an aggregate needs a *short read* from the outside world — summarize some text with an AI, look
up a rate, call a service — and then act on the answer. A full [saga](write-a-saga.html) can do this, but
it is a lot of ceremony when the result is best-effort. The `RunAsync` effect is the middle tier: a
**"mini saga"** that runs an async side effect off the aggregate's mailbox and feeds the result back as a
command — with **no persistence ceremony**.

The key property: `decide` stays a **pure, inspectable function**. It returns a *data description* of the
effect, not a closure — so you unit-test it by structural equality, with no runtime and no service in sight.
The service lives only in a **runner** you register alongside the aggregate.

## The shape

Take a `Note` aggregate: a command asks an AI to summarize the note's text. Describe the effect as data and
`dispatch` it from `decide` (commands are imperative instructions; events are past-tense facts):

```fsharp
type NoteEffect = SummarizeText of string    // the effect, described as data

let decide (cmd: Command<NoteCommand>) state =
    match cmd.CommandDetails with
    | Summarize       -> dispatch (SummarizeText state.Body)   // pure: just a description
    | RecordSummary s -> SummaryRecorded s |> PersistEvent
    | GiveUp          -> SummaryUnavailable |> PersistEvent
```

`Summarize` returns the *description* and nothing else — no AI client in sight — so `decide` stays pure.

## Register the runner

The runner is the only place the service appears. It maps the description to a command; wrap the outside
call so that **every** failure becomes a command rather than an escaping exception:

```fsharp
let notes =
    Fcqrs.aggregateWithEffects api
        { Name = "Note"; Initial = Note.initial; Decide = decide; Fold = fold; Snapshots = Default }
        (fun (SummarizeText text) -> async {
            try
                let! summary = ai.Summarize text     // the actual AI/HTTP call
                return RecordSummary summary          // success -> a command
            with _ ->
                return GiveUp })                      // any failure -> a command, never an exception
```

At runtime: `Summarize` returns the `SummarizeText` description; the runner turns it into a command off the
mailbox (the aggregate keeps handling other commands); FCQRS self-dispatches that command back — reusing the
originating correlation id — so it re-enters `decide` **re-validated against current state**.

> The `try/with` that maps failure to a command *is* the totality contract. If you prefer,
> `Fcqrs.total (fun _ex -> GiveUp) (async { ... })` is a one-liner for exactly this shape.

## It stays pure — test it without Akka

Because the effect is data, `decide` is testable like any other decision:

```fsharp
Expect.equal
    (decide (mkCommand Summarize) state)
    (dispatch (SummarizeText state.Body))
    "Summarize dispatches a summarization effect"
```

## Two rules you must respect

- **Ephemeral.** The in-flight work is process state — it is **not journaled**. A crash, restart, or shard
  rebalance while it runs loses it *silently*; nothing re-issues it. Use `RunAsync` only when that loss is
  tolerable. When the result **must** survive a crash, use a [saga](write-a-saga.html), which persists its
  intent.
- **Total.** The runner must map *every* outcome to a command — that is what the `try/with` above is for.
  An exception that escapes the runner **fail-fasts the process**, exactly like a throwing `fold`. Model a
  service error or timeout as a domain command (`GiveUp`, `Retry`), never as an exception.

## Observability

The runner runs off the mailbox, so FCQRS spans it for you: a low-cardinality `Dispatch:<CaseName>` span
(here `Dispatch:SummarizeText`), parented onto the originating command's trace, covering the runner's
execution. The *domain* outcome shows in the child command span it produces (`Command:RecordSummary` or
`Command:GiveUp`). See [Observe your system](observability.html).

## From C#

The same mechanism, Task-based:

```csharp
// decide returns a description:
EventActions.Dispatch<NoteEvent>(new SummarizeText(state.Body));

// register with the runner (must be total — catch into a command):
var refs = ActorWiring.InitAggregateWithEffects(
    actor, Note.Initial, "Note", Decide, Fold,
    runner: async description =>
    {
        try   { var summary = await ai.Summarize(((SummarizeText)description).Text);
                return new RecordSummary(summary); }
        catch { return new GiveUp(); }
    },
    SnapshotPolicy.Default);
```
