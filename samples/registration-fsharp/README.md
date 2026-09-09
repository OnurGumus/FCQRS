# Register a user (F#)

With .NET 10 installed, from this folder:

```text
dotnet run
```

```text
Registered: Alice (version 1)
Query: Alice
```

Run again: `Already registered: Alice (version 1)` and `Query: Alice`.

`Account` contains the registration rule. `Program` starts FCQRS, sends the request, and queries Alice.
Change the command's name to `"Bob"`: the existing account still returns Alice. Then change
`accountId` to `"bob"` to create a separate registration. Each account starts at version 1.
SQLite stores the event in `registration.db` beside the executable. Keep the same build configuration
when repeating the run. This registers a profile; it does not implement credential authentication.

[Read the quickstart](https://onurgumus.github.io/FCQRS/get-started.html).
