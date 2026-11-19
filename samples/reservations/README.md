# Reservations sample

A workflow per reservation that applies one change at a time. Changes arrive as signals; after each
change it applies, the workflow takes every change that arrived meanwhile and applies only the
newest. When the booking system fails every attempt, the workflow waits and tries the newest change
again. The program holds the first change, sends versions 2, 3 and 2 again while it is applied,
then the final version 4. After the workflow has ended it sends version 2 once more, which `feed`
reports as `Repeated`, and a version 5, which it reports as `Late`.

```bash
dotnet run --project samples/reservations/fsharp -c Release
dotnet run --project samples/reservations/csharp -c Release
```

Each run deletes its database first. The programs reference the projects under `src/`.
`test/Facade.Tests/ReservationTests.fs` tests the F# workflow with `WorkflowTest`.
[Apply only the newest change](../../docs/how-to/apply-the-newest-change.md) quotes them.
