# Registration over HTTP (C#)

From the repository root, with .NET 10 installed:

```text
dotnet run --project samples/registration-http-csharp -- --urls http://localhost:5080
```

Wait for `Listening on http://localhost:5080`, then use a second terminal:

```sh
curl -i http://localhost:5080/accounts/alice \
  -H 'Content-Type: application/json' -d '{"name":"Alice"}'
curl http://localhost:5080/accounts/alice
```

Both responses contain `{"id":"alice","name":"Alice"}`. The first POST returns 201; repeats return
200 with the saved name, even if you ask for Bob. Use `/accounts/bob` for a separate account.

`Program` defines the endpoints. `UserView` projects saved names and lets POST wait until GET can read
them. The project links the console sample's `Account` source file, so both use the same rule.

Ctrl+C stops the server. Run again to recover from `registration-http.db` beside the executable.
GET may return 404 while the view rebuilds; repeating the POST waits for that account's view.
This example stores profiles; it does not implement authentication.

[Read the HTTP guide](https://onurgumus.github.io/FCQRS/tutorial/http-api.html).
