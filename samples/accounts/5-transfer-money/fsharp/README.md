# Step 5: Transfer money (F#)

With the .NET 11 SDK installed, from this folder:

```text
dotnet run
```

The program opens accounts for Alice and Bob, then starts two transfers as workflows. The first moves
money to Bob; the second comes back to Alice because Carol has no account. The program delivers the
first transfer to Bob a second time, as a workflow does after a restart, and the money moves once. It
prints both statements. Run it again: the transfer IDs are workflow IDs, so the same transfers do not
start twice.

[Read the step](https://onurgumus.github.io/FCQRS/tutorial/transfer-money.html).
