# FCQRS

FCQRS is a small F# framework for building applications around Command Query Responsibility
Segregation (CQRS) and event sourcing, on top of Akka.NET actors. It is usable from both F# and C#,
and it is designed so that the reliability of an event-sourced, actor-based system is available to
ordinary applications without making you assemble the distributed-systems machinery yourself.

![FCQRS at a glance](docs/img/cqrs-overview.svg)

## The idea in two minutes

Most systems keep one model and make it serve everyone — the code that decides whether an order may
ship, the customer's status page, the warehouse pick list, the finance report. That one model slowly
buckles under demands that pull in different directions. CQRS splits it: a **write side** shaped for
correct decisions, and **read sides** each shaped for the question they answer. What flows between
them is a stream of **events** — facts in the past tense — and FCQRS stores those events as the
source of truth rather than storing current state. State, read models, and snapshots are all just
re-derivable views of the event log, which is what makes them safe to rebuild and lets you fix a
projection bug by replaying history instead of patching production.

On top of that, FCQRS runs each entity as an Akka.NET actor, so an aggregate processes one command
at a time with no locks, distributes across a cluster, and sleeps when idle. **Sagas** turn events
into follow-up commands, making side effects like sending e-mail reliable and recoverable. And a
**correlation id** threads through a whole request, so a client can know exactly when the read side
has caught up with a command it just sent — read-your-writes without polling.

The same domain is expressed just as cleanly in C#: with C# 15 discriminated `union` types for
commands and events, the C# version of an aggregate reads almost identically to the F# one, and the
framework serialises both natively.

## The shape of an aggregate

An aggregate is three types and two functions — no base class, no attributes. It takes a command,
decides, and returns an action; only persisted events change its state.

```fsharp
let handleCommand (cmd: Command<_>) state =
    match cmd.CommandDetails, state with
    | Register(name, pass), { Username = None } -> VerificationRequested(name, pass) |> PersistEvent
    | Register _,            { Username = Some _ } -> AlreadyRegistered |> DeferEvent   // emitted, not stored
    | ...

let applyEvent event state =
    match event.EventDetails with
    | VerificationRequested(name, pass) -> { state with Username = Some name; Password = Some pass }
    | _ -> state
```

## Start here: the workshop

The best way in is the **[FCQRS workshop](docs/workshop/README.md)** — a read-in-order guide that
builds the whole picture from first principles, in F# and C# side by side, against two real
applications. It starts with the smallest possible example and graduates to a full document-management
app traced end to end. The nine parts cover why CQRS and event sourcing, the cast of characters,
your first aggregate, event sourcing and recovery, the read side, client coordination, sagas, the
full system end to end, and configuration.

## The example applications

- **[`saga_sample`](saga_sample/)** — in this repository. A user registers, receives a verification
  e-mail via a saga, and signs in. The smallest complete picture of every moving part.
- **[`focument`](https://github.com/onurgumus/focument)** (F#) and **`focument-csharp`** (C#) — a
  document-versioning web app: create and edit documents, browse full history, restore any past
  version, with a SQLite read model and an approval saga.

```bash
dotnet build
dotnet run --project saga_sample      # set AUTOMATED_TEST=1 to run without key presses
```

## API reference

Full API documentation is published at **[onurgumus.github.io/FCQRS](https://onurgumus.github.io/FCQRS/)**.

## License

See [LICENSE.md](LICENSE.md).
