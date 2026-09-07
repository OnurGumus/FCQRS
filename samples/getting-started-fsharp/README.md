# Build the document store with F#

From the repository root, using the .NET 10 SDK selected by `global.json`:

```text
dotnet run --project samples/getting-started-fsharp
```

```text
stored version 1; query returned 'first event'
repeat reply version 1; document contains 'first event'
document id: <generated id>
journal: <database path>
```

Keep the printed ID. Replace `DOCUMENT_ID` below and run the commands in order:

```text
dotnet run --project samples/getting-started-fsharp -- --check
dotnet run --project samples/getting-started-fsharp -- --recover DOCUMENT_ID
dotnet run --project samples/getting-started-fsharp -- --edit DOCUMENT_ID "second draft"
dotnet run --project samples/getting-started-fsharp -- --edit DOCUMENT_ID "second draft"
dotnet run --project samples/getting-started-fsharp -- --publish DOCUMENT_ID guides/fcqrs
```

The edit stores version `2`; repeating that edit keeps version `2`. Publication stores its request and
result at versions `3` and `4`, and the query returns `second draft`. Another document claiming the
same slug is rejected. Editing closes when publication starts.

`--check` runs pure domain, replay, and serialization checks. `--pause-publication ID SLUG` pauses a
new publication after its reservation result is stored; restart with `--publish ID SLUG` promptly to
observe saga recovery before its five-minute deadline. An elapsed deadline remains an unknown outcome
and requires investigation; it is not a rejection.

Ordinary runs generate new IDs. `--recover` sends a create to the supplied ID and returns the current
document; an unknown ID creates a document with `replacement attempt`. Each language keeps its own
SQLite journal beside the executable and rebuilds its in-memory projection from offset zero.
Keep the same build configuration throughout the course.

Follow the [getting-started guide](https://onurgumus.github.io/FCQRS/get-started.html) for explanations,
predictions, and expected output at each stage. The full course uses stable .NET 10.
