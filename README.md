# FCQRS

[![NuGet](https://img.shields.io/nuget/vpre/FCQRS.svg?label=NuGet)](https://www.nuget.org/packages/FCQRS)
[![Downloads](https://img.shields.io/nuget/dt/FCQRS.svg)](https://www.nuget.org/packages/FCQRS)
[![CI](https://github.com/OnurGumus/FCQRS/actions/workflows/ci.yaml/badge.svg)](https://github.com/OnurGumus/FCQRS/actions/workflows/ci.yaml)

FCQRS runs event-sourced applications on Akka.NET, with F# and C# APIs.
Write the rules; FCQRS stores events, rebuilds state, and feeds query views.

## Run your first example

From the repository root, with .NET 10 installed:

```text
dotnet run --project samples/registration-fsharp
```

Or C#:

```text
dotnet run --project samples/registration-csharp
```

```text
Registered: Alice (version 1)
Query: Alice
```

Run again: `Already registered: Alice (version 1)`. The saved registration survives the restart.

The rule is:

```fsharp
let decide (command: Command<RegisterUser>) (state: string option) =
    let (RegisterUser name) = command.CommandDetails
    persistIf state.IsNone (UserRegistered(defaultArg state name))
```

`persistIf` saves the first registration and defers later replies using the existing name.
An aggregate processes one account's commands at a time; other accounts run independently.

[Get started](https://onurgumus.github.io/FCQRS/get-started.html) ·
[Task guides](https://onurgumus.github.io/FCQRS/how-to/index.html) ·
[API reference](https://onurgumus.github.io/FCQRS/reference/index.html) ·
[Configuration](https://onurgumus.github.io/FCQRS/configuration.html)

## Install in your own project

```text
dotnet add package FCQRS
```

The [F#](samples/registration-fsharp/) and [C#](samples/registration-csharp/) samples use the published
package on stable .NET 10. Copy either folder to start a standalone project.

See [LICENSE.md](LICENSE.md).
