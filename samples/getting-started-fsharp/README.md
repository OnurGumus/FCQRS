# Getting started with FCQRS and F#

Run from the repository root:

```bash
dotnet run --project samples/getting-started-fsharp
```

Expected output:

```text
stored version 1; query returned 'created from F#'
```

The program uses one command and one event so the complete write and read path stays visible in one
file. It stores the journal beside the built executable, uses a new aggregate id on each run, and
rebuilds its in-memory read model from offset zero.

Continue with the [tutorial](../../docs/tutorial/) to add decisions, deferred replies, a durable read
model, and a saga.
