# Step 2: Withdraw money (C#)

With the .NET 11 SDK installed, from this folder:

```text
dotnet run
```

The program opens Alice's account, deposits and withdraws money, and sends commands that break the
account's rules. Each rejected command gets a reply that is not stored. Two withdrawals sent together
are decided one after the other, so the second one sees the balance the first one left. The program
then prints the rows FCQRS stored in the journal.

[Read the step](https://onurgumus.github.io/FCQRS/tutorial/withdraw-money.html).
