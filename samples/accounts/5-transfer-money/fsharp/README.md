# Step 5: Transfer money (F#)

With the .NET 11 SDK installed, from this folder:

```text
dotnet run
```

The program opens accounts for Alice and Bob, then asks Alice's account for two transfers. A saga
delivers the first to Bob, and refunds the second to Alice because Carol has no account. The program
delivers the first transfer to Bob a second time, as a saga does after a restart, and the money moves
once. It prints both statements. Run it again: the transfer IDs stop the same transfers from being sent
twice.

[Read the step](https://onurgumus.github.io/FCQRS/tutorial/transfer-money.html).
