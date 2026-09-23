# Step 3: Restart the bank (C#)

With the .NET 11 SDK installed, from this folder:

```text
dotnet run
```

The program opens Alice's account and deposits 10, 250 times. The account saves a snapshot of its
state every 100 events. The program prints how many events the journal holds and the snapshots
FCQRS saved. Run it again: FCQRS loads the account from its newest snapshot and the events after it,
and the versions continue at 252.

[Read the step](https://onurgumus.github.io/FCQRS/tutorial/restart-the-bank.html).
