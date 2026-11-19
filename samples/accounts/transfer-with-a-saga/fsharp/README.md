# Transfer money with a saga (F#)

Step 5's transfer written with a saga, the mechanism FCQRS offered before workflows. Sagas stay
supported for existing applications and their stored journals; new code uses the workflow of
[step 5](../../5-transfer-money/). This program uses step 5's account and statement files.

With the .NET 11 SDK installed, from this folder:

```text
dotnet run
```

The program opens accounts for Alice and Bob, then asks Alice's account for two transfers. The saga
delivers the first to Bob, and refunds the second to Alice because Carol has no account. The program
delivers the first transfer to Bob a second time, as a saga does after a restart, and the money moves
once. It prints both statements.

[Write a saga](https://onurgumus.github.io/FCQRS/how-to/write-a-saga.html) explains the code.
