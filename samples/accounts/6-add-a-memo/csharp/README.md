# Step 6: Add a memo (C#)

This step continues the bank from step 5: on its first run, it copies the database that step 5's
C# program wrote. With the .NET 11 SDK installed, run step 5 first, then this step, from
`samples/accounts`:

```text
dotnet run --project 5-transfer-money/csharp
dotnet run --project 6-add-a-memo/csharp
```

The program sends a transfer with a memo. It prints Alice's stored transfers, where the ones step 5
stored have no memo, and statements from a new read model that was built from the first event.

[Read the step](https://onurgumus.github.io/FCQRS/tutorial/add-a-memo.html).
