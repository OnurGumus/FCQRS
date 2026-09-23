# Step 4: Show a statement (F#)

With the .NET 11 SDK installed, from this folder:

```text
dotnet run
```

The program sends Alice's account the commands from step 2 and waits after each one until a projection
has written the matching row to a `statement` table in the same SQLite file. It then reads the
statement with SQL. Run it again: the projection resumes after the last event it wrote, and the
statement grows.

[Read the step](https://onurgumus.github.io/FCQRS/tutorial/show-a-statement.html).
