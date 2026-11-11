# Workflow sample

A quote workflow in F# and C#. It fetches a price from an external service that fails twice,
waits for an approval signal, and sends the quote.

Run it, stop it in the middle of a step, and run it again:

```bash
dotnet run --project samples/workflows/fsharp -- --crash
dotnet run --project samples/workflows/fsharp
```

The first run stops the process inside the `send-quote` step. The second run finds the workflow in
the journal, does not call the price service again, and runs `send-quote` again with the same
idempotency key. Use `samples/workflows/csharp` for the C# program.

The programs reference the projects under `src/`. [Write a workflow](../../docs/how-to/write-a-workflow.md)
quotes them.
