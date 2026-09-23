# FCQRS

[![NuGet](https://img.shields.io/nuget/vpre/FCQRS.svg?label=NuGet)](https://www.nuget.org/packages/FCQRS)
[![Downloads](https://img.shields.io/nuget/dt/FCQRS.svg)](https://www.nuget.org/packages/FCQRS)
[![CI](https://github.com/OnurGumus/FCQRS/actions/workflows/ci.yaml/badge.svg)](https://github.com/OnurGumus/FCQRS/actions/workflows/ci.yaml)

FCQRS runs event-sourced applications on Akka.NET, with F# and C# APIs.
Write the rules; FCQRS stores events, rebuilds state, and feeds query views.

## Run your first example

From `samples/accounts`, with the .NET 11 SDK installed:

```text
dotnet run --project 1-open-an-account/fsharp
```

Or C#:

```text
dotnet run --project 1-open-an-account/csharp
```

```text
Opened for Alice (version 1)
Deposited 100 (version 2)
Deposited 50 (version 3)
```

The program then prints the events FCQRS stored. Run it again and the versions continue at 4: FCQRS
rebuilt Alice's account from its stored events.

The rules are two functions:

```fsharp
let decide (command: Command<AccountCommand>) (state: AccountState) =
    match command.CommandDetails with
    | Open owner -> PersistEvent(Opened owner)
    | Deposit amount -> PersistEvent(Deposited amount)

let fold (event: Event<AccountEvent>) (state: AccountState) =
    match event.EventDetails with
    | Opened owner -> { state with Owner = Some owner }
    | Deposited amount -> { state with Balance = state.Balance + amount }
```

`decide` chooses the event to store for a command, and `fold` applies a stored event to the state.
FCQRS handles one account's commands one at a time; other accounts run independently.

[Why FCQRS](https://onurgumus.github.io/FCQRS/overview.html) ·
[Tutorial](https://onurgumus.github.io/FCQRS/tutorial/open-an-account.html) ·
[Task guides](https://onurgumus.github.io/FCQRS/how-to/index.html) ·
[API reference](https://onurgumus.github.io/FCQRS/reference/index.html) ·
[Configuration](https://onurgumus.github.io/FCQRS/configuration.html)

## Install in your own project

```text
dotnet add package FCQRS
```

The [F#](samples/accounts/1-open-an-account/fsharp/) and [C#](samples/accounts/1-open-an-account/csharp/)
samples use the published package on .NET 11; the C# one uses C# 15. Copy either folder to start a
standalone project.

See [LICENSE.md](LICENSE.md).

## Build the documentation

The site uses [FsLiveDocs](https://adz.github.io/FsLiveDocs/introduction.html).
With the repository's .NET SDK, Python 3, and Node.js installed:

```text
dotnet tool restore
python3 scripts/build-docs.py
python3 -m http.server 8000 --directory output
```

Open `http://localhost:8000`. The build runs the literate F# scripts, checks sample excerpts,
audits Markdown examples, and generates the guides, API reference, and search index.
See [the documentation authoring notes](scripts/DOCUMENTATION.md) for the source formats and checks.
