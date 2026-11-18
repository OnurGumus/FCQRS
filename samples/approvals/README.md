# Approvals sample

The quote workflow from `samples/workflows`, compiled unchanged, with two transactional projections:
one keeps a table of the quotes waiting for approval, the other a row per instance with its status.
The program starts three quotes, lists the two that wait, checks each quote with `Status` before
approving it, lists again, and prints every instance's status.

```bash
dotnet run --project samples/approvals/fsharp -c Release
dotnet run --project samples/approvals/csharp -c Release
```

Each run deletes its database first. The programs reference the projects under `src/`.
[Find workflows that wait for a signal](../../docs/how-to/find-waiting-workflows.md) quotes them.
