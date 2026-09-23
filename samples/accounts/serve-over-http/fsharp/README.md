# Serve over HTTP (F#)

With the .NET 11 SDK installed, from this folder:

```text
dotnet run -- --urls http://localhost:5080
```

The program puts step 4's account and statement behind ASP.NET Core endpoints. Each POST sends one
command to an account and, when the account stores an event, waits until the statement has it before
replying with the balance. A rejection returns 422 with the account's reason.

```text
curl http://localhost:5080/accounts/alice -H 'Content-Type: application/json' -d '{"owner":"Alice"}'
curl http://localhost:5080/accounts/alice/deposits -H 'Content-Type: application/json' -d '{"amount":100}'
curl http://localhost:5080/accounts/alice/statement
```

[Read the guide](https://onurgumus.github.io/FCQRS/how-to/serve-over-http.html).
