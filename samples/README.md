# FCQRS getting-started projects

These two projects run the same FCQRS flow on .NET 10:

1. send `CreateDocument` to a document aggregate;
2. persist `DocumentCreated` in SQLite;
3. project the stored event into an in-memory read model;
4. wait for that projection by correlation id;
5. query and print the projected document.

Choose the language you want to start with:

- [F# getting started](getting-started-fsharp/)
- [C# getting started](getting-started-csharp/)

Both projects reference the FCQRS source project so they always compile against the repository version.
An application outside this repository should use `dotnet add package FCQRS` instead.
