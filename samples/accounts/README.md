# Accounts tutorial samples

Each folder is one step of the accounts tutorial, in F# and C#. Every step is a complete program
that uses the published FCQRS package and stores its events in a SQLite file beside the executable.

The programs target .NET 11. The C# programs declare commands and events as C# 15 unions, so the
compiler checks that every `switch` handles each case. This folder's `global.json` selects the .NET 11 SDK;
run the programs from here or from a step's folder.

1. [Open an account](1-open-an-account/): commands, events, the journal, and folding state.
2. [Withdraw money](2-withdraw-money/): rules, rejections that are not stored, and one command at a
   time.
3. [Restart the bank](3-restart-the-bank/): loading an account, snapshots, and what loading requires
   from `fold`.
4. [Show a statement](4-show-a-statement/): a read model, a projection that writes it, and waiting for
   the read model after a command.
5. [Transfer money](5-transfer-money/): a saga that moves money between accounts, refunds a transfer
   the target cannot take, and commands that are safe to repeat.
6. [Add a memo](6-add-a-memo/): an optional field that old events read as missing, and a read model
   rebuilt from the first event. It continues from step 5's database, so run step 5 first.

[serve-over-http](serve-over-http/) puts step 4's account and statement behind ASP.NET Core endpoints
for the [Serve an aggregate over HTTP](https://onurgumus.github.io/FCQRS/how-to/serve-over-http.html)
guide. Run it with `dotnet run --project serve-over-http/fsharp` or `serve-over-http/csharp`.
