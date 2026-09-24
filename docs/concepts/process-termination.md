---
title: When FCQRS stops the process
category: Understand
categoryindex: 3
index: 9
---

# When FCQRS stops the process

Suppose the tutorial's account has a bug, and `decide` throws an exception for a withdrawal instead of
returning a decision. The caller never receives a reply. The program writes this to standard error,
followed by a stack trace, and exits:

```text
Process terminated.
Process terminated due to aggregate command-handler error
```

On Linux and macOS the exit code is 134. With logging configured, an error entry naming the aggregate,
here `Fatal error in aggregate handleCommand for ...`, comes first.

FCQRS stops the whole process on purpose when code or stored data fails in a way that it cannot tie
back to a caller. This page explains why, lists every case, and describes what an application has to
provide around it.

## An actor has no caller to report to

In a CRUD web application, a request runs on one call stack, from the controller to the database and
back. When code throws, the exception unwinds that stack: the transaction rolls back, the caller gets
an error response, and the next request starts clean. The failure stays inside the request that caused
it.

FCQRS has no such stack. The caller sends a command as a message and waits for a reply message. The
aggregate takes the command from its mailbox later, on its own thread, one message after another. When
`decide` throws, no caller frame is there to catch the exception. The only link back to the request is
the reply, and that reply will never be sent.

The usual answer in an actor system is to restart the actor and continue with the next message. For
FCQRS, continuing would hide the failure:

- the failed command disappears, its caller waits until the command timeout without learning why, and
  the account decides later commands as if that command had never arrived;
- after `fold` throws, the state in memory no longer matches the journal;
- after the journal rejects an event, the next event would leave a gap in the journal's sequence
  numbers, which stops transactional projections;
- a saga restarted after `applySideEffects` throws runs the same step again and fails again, or its
  workflow stops without anyone noticing.

In each case the process would keep running while its behaviour no longer follows from the journal.
So FCQRS stops it. The failure then shows up where operators look: the process exit, the error log, and
the restart count. On the next start, every aggregate and saga recovers from its stored events, which
are the one record FCQRS treats as true.

> **Motivation:** A stop discards memory, not history. Stored events and committed read-model
> transactions survive it, and recovery rebuilds everything else from them. A process that keeps
> running after its state has diverged gives no such guarantee.

## What a stop does

FCQRS calls `Environment.FailFast`. The process ends immediately:

- `finally` blocks, finalizers, `ProcessExit` handlers, and hosted-service shutdown do not run;
- commands that were not yet stored are lost, on every aggregate in the process. Callers in other
  processes, such as HTTP clients, see a broken connection or a timeout;
- stored events, saved snapshots, and committed projection transactions are unaffected;
- buffered logs and traces are lost unless `Telemetry.FatalFlush` drains them first, as
  [Observe your system](../how-to/observability.html#Flush-telemetry-on-a-fatal-exit) shows.

## Every case that stops the process

### Aggregates

- `decide` (`HandleCommand` in C#) throws.
- `fold` (`ApplyEvent` in C#) throws, while handling a command or while the aggregate recovers.
- The journal rejects an event it was asked to store.
- A `RunAsync` runner throws, or an aggregate returns `RunAsync` without a registered runner. See
  [Dispatch a best-effort async effect](../how-to/dispatch-async-effects.html#Failure-contract).

### Sagas

- `handleEvent` (`Start` or `HandleEvent` in C#) throws. One message is exempt: a handler that throws on
  `ExpectationExhausted` is logged and receives it again one deadline later.
- `applySideEffects` (`ApplySideEffects` in C#) throws.
- A stored saga state cannot be applied during recovery.
- FCQRS cannot send one of the commands the saga returned.
- A `StayExpecting` expectation is invalid: its deadline is not positive, its retry intervals are not
  positive or shrink, or a resend command carries its own delay.
- The journal rejects a saga's event.

### Starting a saga

An aggregate stores an event that starts a saga only after that saga reports it is ready, as
[Sagas](sagas.html#Starting-is-itself-a-race) explains. The process stops when:

- a start rule (`StartOn`) throws;
- the wait exceeds `config:akka:fcqrs:saga-start-timeout`, 30 seconds by default. The common causes are
  an F# application that never called `Fcqrs.wireSagaStarters`, and a saga that cannot store its start,
  for example because its journal is unavailable. Call `wireSagaStarters` after registering the
  aggregates and sagas, with an empty list when there are no sagas; the C# host builder calls it for
  you.

### Stored history and messages

- A stored event or snapshot cannot be read, for example after an event case was removed or a type was
  renamed without a journal name. [Evolve persisted events](../how-to/evolve-events.html) covers the
  safe changes.
- An event upcaster throws, or returns a type the aggregate or saga cannot fold, while history is read.
- An event or message cannot be serialized.

### Offset-based projections

- The handler registered with `Fcqrs.projection` or `AddProjection` throws, including a failure of its
  own database. See [Add a projection](../how-to/add-a-projection.html#Handle-failures-visibly).

## Failures that do not stop the process

Some failures have a place to go, so FCQRS reports them there instead:

| Failure | What happens |
|---|---|
| A business rule turns a command away | `decide` returns a rejection, as in the tutorial's [withdraw money](../tutorial/withdraw-money.html) step |
| The journal cannot store an event, for example because the database is down | The aggregate stops and recovers on its next command; a saga stops and restarts from its journal |
| An aggregate cannot read its history from the journal | The aggregate stops; its next command tries again |
| No reply arrives within `akka.fcqrs.command-timeout` | The caller gets `TimeoutException` |
| An offset-based projection cannot read the journal | FCQRS logs the error and retries with backoff |
| A transactional projection's handler throws | The transaction rolls back, the projection stops, and `IProjection.Completion` faults |
| A saga's handler throws on `ExpectationExhausted` | FCQRS logs the error and delivers it again one deadline later |

## What the application provides

- **Decisions that return.** Report a broken business rule as a rejection or an event, never as an
  exception. Keep `fold` free of anything that can throw, because it runs again during every recovery.
- **Total effect runners.** Catch every exception in a `RunAsync` runner and turn it into a command.
- **A supervisor.** Run the process under something that restarts it, such as systemd, a container
  restart policy, or Kubernetes, and alert on restarts. A bug in `fold` or unreadable stored data fails
  the same way on every start, so repeated restarts mean the code or the data needs a fix. Do not
  delete journal rows to get past one.
- **Telemetry that leaves in time.** Register `Telemetry.FatalFlush` so the last logs and traces
  reach their backend.
- **Callers that expect unknown outcomes.** A command in flight when the process stopped may or may
  not have been stored. Give commands IDs that make a retry safe, as the transfer IDs in
  [Transfer money](../tutorial/transfer-money.html) do.

[Consistency and recovery](consistency-and-recovery.html) describes what each component recovers from
after a restart. [Observe your system](../how-to/observability.html) shows how to collect the logs and
traces that explain a stop.
