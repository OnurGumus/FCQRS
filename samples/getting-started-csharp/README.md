# Getting started with FCQRS and C#

Run from the repository root with the stable .NET 10 SDK:

```bash
dotnet run --project samples/getting-started-csharp
```

Expected output:

```text
stored version 1; query returned 'created from C#'
```

The program uses one concrete command and event type, so the first project does not require preview C#
union syntax. It stores the journal beside the built executable, uses a new aggregate id on each run,
and rebuilds its in-memory read model from offset zero.

Continue with [Use FCQRS from C#](../../docs/how-to/use-from-csharp.md) when the domain needs multiple
command and event cases, deferred replies, durable projections, or sagas.
