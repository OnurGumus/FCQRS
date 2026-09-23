# Step 1: Open an account (F#)

With the .NET 11 SDK installed, from this folder:

```text
dotnet run
```

The program opens Alice's account, deposits 100 and 50, and prints the rows FCQRS stored in the
journal. Run it again: the versions continue at 4, because FCQRS rebuilt the account from its stored
events before handling the new commands.

[Read the step](https://onurgumus.github.io/FCQRS/tutorial/open-an-account.html).
