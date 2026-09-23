---
title: Dispatch a best-effort async effect
category: Apply
categoryindex: 4
index: 11
---

# Dispatch a best-effort async effect

Use `RunAsync` when a command needs a short asynchronous result and losing that in-flight work during a
restart is acceptable. Examples include a cache lookup, optional enrichment, or a suggestion that the
caller can request again.

Nothing in the tutorial's bank qualifies. Moving money must survive a restart, so the tutorial's
[transfer](../tutorial/transfer-money.html) uses a saga. This page uses a note that asks a service for
a summary instead.

Use a [saga](write-a-saga.html) when the work must resume after a restart, needs durable retries, or
crosses aggregate boundaries as a business process.

| Requirement | `RunAsync` | Saga |
|---|---:|---:|
| `decide` returns an effect description | yes | no |
| In-flight intent is persisted | no | yes |
| Result returns as a command | yes | yes |
| Survives process stop or shard movement | no | yes |
| Suitable for required external business action | no | yes, with an idempotent handler |

> **Motivation:** `RunAsync` keeps the decision function pure without pretending the in-flight work is
> durable. Choose it only when repeating the original request is an acceptable recovery strategy.

`decide` returns data describing the effect, not a closure that performs it. A separately registered
runner executes the description and returns a command.

## The shape

A note aggregate accepts `Summarize`, then records either a summary or an unavailable result. The effect
description contains the input required by the runner:

```fsharp
type NoteCommand = Summarize | RecordSummary of string | GiveUp
type NoteEvent = SummaryRecorded of string | SummaryUnavailable

// The effect, described as data.
type NoteEffect = SummarizeText of string

let decide (cmd: Command<NoteCommand>) state =
    match cmd.CommandDetails with
    // Pure: only a description of the work.
    | Summarize -> dispatch (SummarizeText state.Body)
    | RecordSummary s -> SummaryRecorded s |> PersistEvent
    | GiveUp -> SummaryUnavailable |> PersistEvent
```

<div class="cs-alt"></div>

```csharp
public union NoteCommand(Summarize, RecordSummary, GiveUp);
public sealed record Summarize;
public sealed record RecordSummary(string Summary);
public sealed record GiveUp;
public union NoteEvent(SummaryRecorded, SummaryUnavailable);
public sealed record SummaryRecorded(string Summary);
public sealed record SummaryUnavailable;

// The effect, described as data.
public sealed record SummarizeText(string Text);

EventAction<NoteEvent> Decide(Command<NoteCommand> cmd, NoteState state) =>
    cmd.CommandDetails switch
    {
        // Pure: only a description of the work.
        Summarize =>
            EventActions.Dispatch<NoteEvent>(new SummarizeText(state.Body)),
        RecordSummary result => EventActions.Persist<NoteEvent>(
            new SummaryRecorded(result.Summary)),
        GiveUp => EventActions.Persist<NoteEvent>(new SummaryUnavailable())
    };
```

No service client appears in `decide`, so the returned action can be compared directly in a unit test.

## Register the runner

The runner maps every outcome to a command. Catch service failures and timeouts at this boundary:

```fsharp
let notes =
    Fcqrs.aggregateWithEffects api
        { Name = "Note"; Initial = Note.initial; Decide = decide; Fold = fold
          Snapshots = Default; Passivation = PassivationPolicy.Default }
        (fun (SummarizeText text) -> async {
            try
                let! summary = ai.Summarize text
                return RecordSummary summary
            with _ ->
                return GiveUp })
```

<div class="cs-alt"></div>

```csharp
// C# cannot infer the command type from the Decide method group, so name all
// three types.
var notes = ActorWiring.InitAggregateWithEffects<
    NoteState, NoteCommand, NoteEvent>(
    actor,
    NoteState.Initial,
    "Note",
    Decide,
    Fold,
    runner: async description =>
    {
        var effect = (SummarizeText)description;
        try
        {
            var summary = await ai.Summarize(effect.Text);
            // Return the union: the aggregate expects a NoteCommand.
            return (NoteCommand)new RecordSummary(summary);
        }
        catch
        {
            return (NoteCommand)new GiveUp();
        }
    },
    SnapshotPolicy.Default);
```

The runner executes away from the aggregate mailbox, so the aggregate can process other commands while
the call is in flight. FCQRS sends the result command back to the same aggregate with the original
correlation id. The result re-enters `decide` against the state that exists when it arrives, not the
state that existed when the request began.

Several effects can complete out of order. Include a request id or expected state in the description and
result command when an older result must not overwrite newer work.

With [Send at an expected version](send-if-version.html), FCQRS checks the expected version before
starting the effect and again when its result command returns. A result that finds a different
version conflicts before its decision function runs. The check does not undo external work that the
runner has already performed.

`Fcqrs.total (fun _exception -> GiveUp) (async { ... })` provides the same exception-to-command mapping.

## Test it without Akka.NET

Because the effect is data, `decide` is testable like any other decision. This test uses a `command`
helper that wraps a payload with `TestEnvelope.Command`, as in [Test your domain](test-your-domain.html):

```fsharp
Expect.equal
    (decide (command Summarize) state)
    (dispatch (SummarizeText state.Body))
    "Summarize dispatches a summarization effect"
```

<div class="cs-alt"></div>

```csharp
var action = Decide(TestEnvelope.Command<NoteCommand>(new Summarize()), state);

Assert.Equal(
    EventActions.Dispatch<NoteEvent>(new SummarizeText(state.Body)),
    action);
```

## Failure contract

- **The work is ephemeral.** A process stop, actor restart, or shard move loses the in-flight operation.
  FCQRS does not reissue it.
- **The runner must be total.** An escaping exception terminates the process because FCQRS cannot turn an
  unknown runner failure into a valid domain command. Model timeout, rejection, and retry exhaustion as
  explicit result commands.
- **The result may be stale.** Validate the result command against current aggregate state before
  persisting it.

## Observability

FCQRS creates a `Dispatch:<CaseName>` span for the runner and parents it to the originating command's
trace. The result appears as a later command span such as `Command:RecordSummary` or `Command:GiveUp`.
See [Observe your system](observability.html).

The runner returns the command boxed as `object`. Every path returns a command that the
aggregate understands; an exception must not escape the runner.
