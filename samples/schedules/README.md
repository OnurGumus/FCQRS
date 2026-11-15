# Schedule sample

A service with two scheduled jobs in F# and C#: a backup every night at 02:00 and an invoice every
Monday at 08:00, both on Oslo time. Each job is a workflow, and a schedule starts it.

```bash
dotnet run --project samples/schedules/fsharp
```

The program creates both schedules and prints their next due times. It then creates a demo schedule
that starts the backup workflow every two seconds, waits for three runs, prints them, deletes the
schedule, and exits. Run it again: it starts on the first run's journal, `jobs.db` next to the
executable, and the creates of the two schedules store nothing. Use `samples/schedules/csharp` for
the C# program.

The programs reference the projects under `src/`. [Run scheduled jobs](../../docs/how-to/run-scheduled-jobs.md)
quotes them.
