---
title: Sagas
category: Understand
categoryindex: 3
index: 6
---

# Sagas: durable coordination

An aggregate can make a correct decision only from state it owns. A document aggregate can decide
whether a document is ready to publish, but it cannot decide whether the requested URL slug is already
reserved by another document. That rule belongs to the aggregate identified by the slug.

A **saga** coordinates the conversation between those independent owners. It stores where the
conversation has reached, listens for outcomes, and sends the next command. It does not move another
aggregate's rules into one large object.

## Start with a workflow that can stop halfway

Publishing a document under `/guides/fcqrs` takes several steps:

1. the document records `PublicationRequested`;
2. the saga asks the `guides/fcqrs` slug aggregate to reserve itself;
3. the slug replies with `SlugReserved` or `SlugUnavailable`;
4. the saga reports `Published` or `Rejected` to the document.

The process can stop after any step. A node may restart after the slug is reserved but before the
result reaches the document.

> **Motivation:** A chain of in-memory callbacks forgets where it was when the process stops. A saga
> stores that progress so the conversation can continue from its last durable step.

## A saga is a state machine

The publication workflow can be written as a table before any FCQRS code:

| Current state | Incoming event | Persisted next state | Command after persistence |
|---|---|---|---|
| not started | `PublicationRequested` | `ReservingSlug` | `ReserveSlug` |
| `ReservingSlug` | `SlugReserved` | `ReportingResult Published` | `FinishPublication Published` |
| `ReservingSlug` | `SlugUnavailable` | `ReportingResult Rejected` | `FinishPublication Rejected` |
| `ReportingResult result` | `PublicationFinished result` | `Done` | none; stop |

The state names say what the workflow is waiting for. The state also carries the identifiers needed to
repeat the next command after recovery.

<img src="../img/saga-lifecycle.svg" alt="A saga receives an event, persists its next workflow state, sends commands for that state, and can recover and safely repeat those commands" width="760"/>

## The saga has two functions

An aggregate separates deciding an event from folding that event into state. A saga separates
accepting an incoming event from performing the work associated with its persisted state.

```text
handleEvent      : incoming event + current saga state -> persisted next state
applySideEffects : persisted saga state + recovering   -> transition + commands
```

`handleEvent` is the saga's event-driven transition function. For example, `SlugReserved` is accepted
only while the saga is `ReservingSlug`. It returns `StateChangedEvent (ReportingResult Published)`.
FCQRS stores that state change before running commands for the new state.

`applySideEffects` runs after a state change is durable. In `ReportingResult Published`, it returns
`FinishPublication Published` to the originating document. FCQRS also calls it after recovery, with
`recovering = true`, so the workflow can safely resume from the state it last stored.

This ordering is the core guarantee:

```text
incoming event
  -> handleEvent chooses next state
  -> next state is stored
  -> applySideEffects issues commands
```

The state is stored first, so a restart has a durable answer to “what should happen next?”

> **Motivation:** Keeping state selection separate from command emission makes the next action
> recoverable. The journal records the saga's intent before delivery introduces uncertainty.

## `StateChangedEvent` and `SagaTransition` are different

The two functions return different control values because they answer different questions.

`handleEvent` normally returns:

- `StateChangedEvent next`: accept the event and persist `next` as saga progress;
- `UnhandledEvent`: this event is not valid for the current saga state.

`applySideEffects` returns commands together with one of these transitions:

| Transition | Meaning |
|---|---|
| `Stay` | Keep the current persisted state after issuing the commands |
| `NextState next` | Persist another state immediately, without waiting for an incoming event |
| `StopSaga` | Issue any returned commands, then complete and passivate the saga |

Delayed commands returned with `StopSaga` are still delivered — they are the saga's final act. The
exception is `Self`-targeted delayed commands, which FCQRS cancels with a warning: a completed saga
must not be resurrected by its own final message. (With Akka.NET's remembered entities — the default
here — a passivated entity restarts when a message arrives, and recovery would re-drive the same
state.) Final delayed commands should target other entities.

Most workflows use `StateChangedEvent` for business events and `Stay` while waiting for a reply.
`NextState` is useful for an internal step that should advance immediately. Use it carefully: every
automatically entered state runs `applySideEffects` again, so a cycle of `NextState` transitions can
loop without waiting for new information.

## Aggregates and sagas have different authority

| Aggregate | Saga |
|---|---|
| Receives commands | Reacts to events |
| Protects rules inside one identity | Coordinates independent identities |
| Emits outcome events | Emits follow-up commands and stores workflow states |
| Owns domain decision state | Owns only process progress |

The publication saga does not inspect the slug aggregate's state. It sends `ReserveSlug`, and the slug
aggregate decides whether reservation is allowed. The saga reacts to that owner's answer.

## Why a saga needs an explicit start

Ordinary actors already exist logically before a caller sends a command. A saga instance is different:
it represents one particular workflow and should exist only when its starting business event occurs.

The `StartOn` predicate declares that boundary. For the publication saga it matches
`PublicationRequested` and ignores every other document event. The aggregate that produced this event
is the **originator**. Commands such as `toOriginator (FinishPublication Published)` route back to
that exact document instance.

Application code does not construct `SagaStartingEvent` directly. FCQRS wraps the matched originator
event in that internal envelope so the saga can retain:

- the event that created this workflow;
- the originator identity and version used by the startup handshake;
- the correlation and metadata context needed during recovery.

The distinction matters: `PublicationRequested` is the domain fact; `SagaStartingEvent` is FCQRS
runtime evidence about how this saga instance began.

> **Motivation:** `StartOn` is more than an event filter. It marks the creation boundary FCQRS needs to
> install the new saga before releasing the event that gives it work.

## Starting is itself a race

The first event must create the saga and also reach it. Publishing before the new saga subscribes can
lose the one event that moves it out of its initial state.

FCQRS closes that race with a saga starter handshake:

1. `StartOn` identifies an originator event that needs a saga;
2. the originator contacts the saga starter before publishing that event;
3. the starter creates the saga instance and establishes its subscription;
4. the saga stores its starting envelope;
5. the originator is allowed to persist and publish the domain event;
6. `handleEvent` receives it with no user-defined state yet and stores the first state.

<img src="../img/saga-starter.svg" alt="An originator event is matched by StartOn, the saga starter creates and subscribes the saga, and only then can the event be published safely" width="940"/>

`Fcqrs.wireSagaStarters` in F#, or `AddSaga(..., startOn: ...)` in C#, installs these start rules. An
empty F# application still calls `wireSagaStarters api []` so runtime startup follows one explicit
path.

## Resumption means re-drive, not rewind

FCQRS stores each accepted saga state transition. On restart it loads a snapshot if available, replays
later state changes, restores the starting-event context, and subscribes the saga again. It then calls
`applySideEffects` for the recovered state with `recovering = true`.

Suppose the last durable state is `ReportingResult Published`. FCQRS knows the saga must send
`FinishPublication Published`; it cannot know whether the previous process sent that command just
before it stopped. The correct recovery action is therefore to re-drive the state with a retry-safe
command.

```text
stored state: ReportingResult Published
unknown:      was FinishPublication Published delivered before the crash?
resume:       send FinishPublication Published again safely
```

The document aggregate should treat a repeated result as the same business outcome rather than store
it twice. At an external boundary, use a stable idempotency key or query the operation's status.
The `recovering` flag can select that status-check path when repeating the normal command is unsafe.

Returning no commands for every recovered state is usually wrong. It strands a saga when the original
command was never delivered. Resumability comes from durable state plus safe re-delivery, not from
exactly-once messaging.

> **Motivation:** At a crash boundary, the sender cannot prove whether its last command arrived.
> Persisting the intended step and making that command safe to repeat turns this uncertainty into a
> workflow that can resume.

## The starting event also protects resumption

During recovery FCQRS uses the stored starting event to check the originator exchange and continue the
startup protocol safely. If the originator has moved past the starting event's version, the check
fails: the originator answers the saga with an `AbortedEvent` — visible as an `Abort:` span and in the
message-flow logs — and the saga passivates instead of continuing from stale assumptions.

This check protects the FCQRS actor conversation. It does not make an independent database, payment
provider, or HTTP service part of the saga journal transaction. External operations still require
their own idempotency and reconciliation rules.

## Delivery and persistence cannot share one transaction

For any outgoing saga command, failure may occur:

- before delivery;
- after delivery but before the receiver acts;
- after the receiver acts but before its event reaches the saga;
- after the saga receives the event but before its next state is stored.

No local transaction spans the saga journal and every participant. Design each step so repetition is
safe, give every wait a timeout, and make failed or compensating states visible to operators.

## Compensation is a new action

A distributed workflow cannot generally erase work that already succeeded. Releasing a reserved slug
is not time travel; it is another command with its own outcome and possible failure. A sent e-mail may
have no useful compensation at all.

Model compensation explicitly: what triggers it, which state stores its progress, whether it is
idempotent, and what happens if it fails. Some workflows should stop for human resolution rather than
pretend every action is reversible.

## Decide whether you need a saga

Use a saga when work crosses independent consistency boundaries and its progress must survive restart.
Use an aggregate command when one owner can decide the rule. Use an async effect for best-effort work
that may safely be lost and does not need durable progress.

Chapter 3 of the [tutorial](../tutorial/3-adding-a-saga.html) builds the publication workflow from a
document and a slug aggregate. [Write a saga](../how-to/write-a-saga.html) is the compact F# and C#
recipe. [Consistency and recovery](consistency-and-recovery.html) places saga resumption beside the
other durable boundaries.
