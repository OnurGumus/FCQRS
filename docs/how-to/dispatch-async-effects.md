---
title: Dispatch async effects (mini saga)
category: How-to
categoryindex: 5
index: 8
---

# Dispatch async effects without a saga

Sometimes an aggregate needs a *short read* from the outside world — ask an AI to cluster some text, look
something up — and then act on the answer. A full [saga](write-a-saga.html) can do this, but it is a lot
of ceremony when the result is best-effort. The `RunAsync` effect is the middle tier: a **"mini saga"**
that runs an async side effect off the aggregate's mailbox and feeds the result back as a command — with
**no persistence ceremony**.

The key property: `decide` stays a **pure, inspectable function**. It returns a *data description* of the
effect, not a closure — so you unit-test it by structural equality, with no runtime and no oracle in sight.
The oracle lives only in a **runner** you register alongside the aggregate.

## The shape

Describe the effect as data, and `dispatch` it from `decide`:

```fsharp
type RoundEffect =
    | ClusterThemes of ApprovedText list

let decide (cmd: Command<RoundCommand>) state =
    match cmd.CommandDetails with
    | CloseRound -> dispatch (ClusterThemes state.Submissions)   // pure: just a description
    | RecordThemes themes -> ThemesRecorded themes |> PersistEvent
    | ClusteringFailed -> ClusteringGaveUp |> PersistEvent
```

Register the aggregate **with the runner** — the only place the oracle appears. Wrap the body in
`total` so every failure (an oracle error, a timeout) becomes a command rather than an escaping exception:

```fsharp
let round =
    Fcqrs.aggregateWithEffects api
        { Name = "Round"; Initial = Round.initial; Decide = decide; Fold = fold; Snapshots = Default }
        (fun (ClusterThemes texts) ->
            total (fun _ex -> ClusteringFailed) (async {
                let! themes = oracle.Cluster texts     // the actual AI/HTTP call
                return RecordThemes themes }))
```

At runtime: `CloseRound` returns the `ClusterThemes` description; the runner turns it into a command off
the mailbox (the aggregate keeps handling other commands); FCQRS self-dispatches that command back, reusing
the originating correlation id, so it re-enters `decide` **re-validated against current state**.

## It stays pure — test it without Akka

Because the effect is data, `decide` is testable exactly like any other decision:

```fsharp
Expect.equal
    (decide (mkCommand CloseRound) state)
    (dispatch (ClusterThemes state.Submissions))
    "CloseRound dispatches a clustering effect"
```

## Two rules you must respect

- **Ephemeral.** The in-flight work is process state — it is **not journaled**. A crash, restart, or shard
  rebalance while it runs loses it *silently*; nothing re-issues it. Use `RunAsync` only when that loss is
  tolerable. When the result **must** survive a crash, use a [saga](write-a-saga.html), which persists its
  intent.
- **Total.** The runner must map *every* outcome to a command — that is what `total` is for. An exception
  that escapes the runner **fail-fasts the process**, exactly like a throwing `fold`. Model an oracle error
  or timeout as a domain command (`ClusteringFailed`, `ClusteringTimedOut`), never as an exception.

## Observability

The runner runs off the mailbox, so FCQRS spans it for you: a low-cardinality `Dispatch:<CaseName>` span
(e.g. `Dispatch:ClusterThemes`), parented onto the originating command's trace, covering the runner's
execution. The *domain* outcome shows in the child command span it produces (`Command:RecordThemes` or
`Command:ClusteringFailed`). See [Observe your system](observability.html).

## From C#

The same mechanism, Task-based:

```csharp
// decide returns a description:
EventActions.Dispatch<RoundEvent>(new ClusterThemes(state.Submissions));

// register with the runner (must be total — catch into a command):
var refs = ActorWiring.InitAggregateWithEffects(
    actor, Round.Initial, "Round", Decide, Fold,
    runner: async description =>
    {
        try   { var themes = await oracle.Cluster(((ClusterThemes)description).Texts);
                return new RecordThemes(themes); }
        catch { return new ClusteringFailed(); }
    },
    SnapshotPolicy.Default);
```
