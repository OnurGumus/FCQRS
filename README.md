# FCQRS

FCQRS is a small F# framework for building applications with **CQRS** and **event sourcing** on top
of Akka.NET actors, usable from both F# and C#. You write pure decision and fold functions; the
framework supplies the actors, sharding, persistence, sagas, and client coordination.

![How FCQRS fits together](docs/img/architecture.svg)

Each entity is an **aggregate** — an actor that processes one command at a time, decides what
happened, and emits **events**. Events are appended to a journal (the source of truth) and flow out to
**read models** shaped for querying and to **sagas** that turn events into follow-up commands. A
**correlation id** threads through a request so a caller knows exactly when the read side has caught
up. The same domain reads almost identically in C#, using C# 15 discriminated `union` types.

## Documentation

The full documentation lives at **[onurgumus.github.io/FCQRS](https://onurgumus.github.io/FCQRS/)**,
organized by what you're trying to do:

- **Get started** — install and run a complete write-and-read loop in minutes.
- **Tutorial** — build an app step by step: aggregate, read model, query, saga.
- **Concepts** — the why behind each piece: CQRS & event sourcing, aggregates, the read side, sagas,
  consistency & recovery, C# interop.
- **How-to guides** — focused recipes for specific tasks.
- **Reference** — the generated API docs and the configuration reference.

## A taste

```fsharp
let handleCommand (cmd: Command<_>) state =
    match cmd.CommandDetails, state with
    | Register(name, pass), { Username = None } -> RegisterSucceeded(name, pass) |> PersistEvent
    | Register _,            { Username = Some _ } -> AlreadyRegistered |> DeferEvent   // emitted, not stored
    | ...

let applyEvent event state =
    match event.EventDetails with
    | RegisterSucceeded(name, pass) -> { state with Username = Some name; Password = Some pass }
    | _ -> state
```

## Examples

- **[`sample/`](sample/)** — the smallest complete picture: a `User` that registers and logs in.
- **[`saga_sample/`](saga_sample/)** — adds a verification saga that sends an e-mail.
- **[`focument`](https://github.com/onurgumus/focument)** (F#) and **`focument-csharp`** (C#) — a
  document-versioning web app with a SQLite read model and an approval saga.

```bash
dotnet build
dotnet run --project sample        # set AUTOMATED_TEST=1 to run without key presses
```

## License

See [LICENSE.md](LICENSE.md).
