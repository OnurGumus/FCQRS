# Start a workflow from an aggregate event

A projection that starts a `Welcome` workflow for each `Opened` event an account stores. The workflow
ID comes from the event, and the projection keeps its progress in the journal's database, so each
stored event starts its workflow once, also when the projection hands it over again after a restart.

```bash
dotnet run --project samples/start-from-an-event/fsharp -c Release
dotnet run --project samples/start-from-an-event/csharp -c Release
```

The first run opens Alice's account and the welcome is sent. A second run continues from the same
database: the account is already open, and no welcome is sent again. The programs reference the
projects under `src/`. [Start a workflow from an aggregate event](../../docs/how-to/start-a-workflow-from-an-event.md)
quotes them.
