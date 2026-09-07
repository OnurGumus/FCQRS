# FCQRS document-store samples

Both projects follow the same path on stable .NET 10: create, repeat, recover, edit, publish, and
recover an interrupted publication workflow. Keep the same document ID and database between stages.

- [F# sample](getting-started-fsharp/)
- [C# sample](getting-started-csharp/)
- [Start the course](https://onurgumus.github.io/FCQRS/get-started.html)

`Document` contains the domain rules, `Publication` the slug aggregate and saga, `Program` the CLI
exercises, and `Checks` the executable checks. The C# `LegacyCreationReader` retains compatibility
with the original sample's event envelope. `fixtures` contains captured old event and snapshot data.

Both projects reference the FCQRS source project. An application outside this repository should use
`dotnet add package FCQRS` instead. `DOCSTORE_DATABASE` optionally selects a database path for isolated
checks; normal runs print the path beside the executable.

Run the entire path, including old-history recovery, with `python3 scripts/check-learning-path.py`
from the repository root.
