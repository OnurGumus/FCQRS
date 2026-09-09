---
title: Register over HTTP
category: Learn FCQRS
categoryindex: 2
index: 5
---

# Register over HTTP

Use the same [account rule](../get-started.html) behind two ASP.NET Core endpoints. This optional
sample reuses `Account.fs` / `Account.cs` from the console project. [Jump to the requests](#Run-the-API).

## POST a registration

`id` comes from the URL; `request.Name` comes from JSON. The endpoint sends `RegisterUser` and waits
for the account to appear in the query view before returning:

<!-- sample: http-fsharp Program.fs register -->
```fsharp
let register (id: string) (request: RegistrationRequest)
             (ct: CancellationToken) = task {
    if not (valid id && valid request.Name) then
        return Results.BadRequest(
            {| error = "ID and name must be non-blank (max 255 characters)." |})
    else
        try
            let! reply =
                accounts.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId id)
                    (RegisterUser request.Name) (fun _ -> true)
                |> fun work -> Async.StartAsTask(work, cancellationToken = ct)
            do! users.WaitFor(id, ct)
            let (UserRegistered name) = reply.EventDetails
            let body = {| id = id; name = name |}
            return
                if reply.Journaled = Some true then
                    Results.Created($"/accounts/{Uri.EscapeDataString(id)}", body)
                else Results.Ok(body)
        with :? TimeoutException ->
            return Results.Problem(
                "Registration may have completed. Retry with the same account ID.",
                statusCode = StatusCodes.Status503ServiceUnavailable)
}
app.MapPost("/accounts/{id}",
    Func<string, RegistrationRequest, CancellationToken, Task<IResult>>(
        fun id request ct -> register id request ct)) |> ignore
```

<div class="cs-alt"></div>

<!-- sample: http-csharp Program.cs register -->
```csharp
app.MapPost("/accounts/{id}", async Task<IResult> (
    string id, RegistrationRequest request,
    Handler<RegisterUser, UserRegistered> accounts, CancellationToken ct) =>
{
    if (!Valid(id) || !Valid(request.Name))
        return Results.BadRequest(new {
            error = "ID and name must be non-blank (max 255 characters)."
        });

    try
    {
        var reply = await accounts(_ => true, Values.NewCID(),
            Values.CreateAggregateId(id), new RegisterUser(request.Name))
            .WaitAsync(ct);
        await users.WaitFor(id, ct);
        var body = new { id, name = reply.EventDetails.Name };
        return reply.Journaled?.Value == true
            ? Results.Created($"/accounts/{Uri.EscapeDataString(id)}", body)
            : Results.Ok(body);
    }
    catch (TimeoutException)
    {
        return Results.Problem(
            "Registration may have completed. Retry with the same account ID.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});
```

The first registration returns **201 Created** and a `Location` header. A repeated request returns
**200 OK** with the saved name. The registration rule still decides what gets persisted.

## Run the API

From the repository root, with .NET 10 installed:

```text
dotnet run --project samples/registration-http-fsharp -- --urls http://localhost:5080
```

<div class="cs-alt" data-fs="text" data-cs="text"></div>

```text
dotnet run --project samples/registration-http-csharp -- --urls http://localhost:5080
```

Wait for `Listening on http://localhost:5080`. In a second terminal:

```sh
curl -i http://localhost:5080/accounts/alice \
  -H 'Content-Type: application/json' -d '{"name":"Alice"}'
```

The response is `201 Created` with this body:

```json
{"id":"alice","name":"Alice"}
```

Query it immediately:

```sh
curl http://localhost:5080/accounts/alice
```

The response has the same body. Repeat the POST with `"name":"Bob"`: it returns `200 OK` and Alice.
Change the URL to `/accounts/bob` to register Bob separately.

## GET the query view

The GET endpoint reads the projected names:

<!-- sample: http-fsharp Program.fs query -->
```fsharp
let query (id: string) =
    if not (valid id) then Results.BadRequest()
    else
        match users.TryGet(id) with
        | true, name -> Results.Ok({| id = id; name = name |})
        | _ -> Results.NotFound()
app.MapGet("/accounts/{id}", Func<string, IResult>(fun id -> query id)) |> ignore
```

<div class="cs-alt"></div>

<!-- sample: http-csharp Program.cs query -->
```csharp
app.MapGet("/accounts/{id}", (string id) =>
{
    if (!Valid(id)) return Results.BadRequest();
    return users.TryGet(id, out var name)
        ? Results.Ok(new { id, name })
        : Results.NotFound();
});
```

`UserView.Project` stores each saved name and signals waiting requests. Its per-account signal also
handles repeats: the saved event may have been projected earlier or replayed after a restart.
This works because a registration never changes; [views with updates need coordination for the particular write](../how-to/read-your-writes.html).

Stop with **Ctrl+C** and run again. SQLite keeps events in `bin/Debug/net10.0/registration-http.db`
inside the HTTP sample folder; the in-memory view rebuilds from those events.

- **400:** the ID or name is blank or longer than 255 characters, or the JSON body is invalid.
- **404:** this account is absent from the current view. During startup, replay may still be catching up.
- **503:** a command or the 30-second projection wait timed out. Registration may have completed; retry with the same ID.

This sample stores profiles. Passwords and login sessions belong to an authentication provider.

Complete source: [F#](https://github.com/OnurGumus/FCQRS/tree/main/samples/registration-http-fsharp) ·
[C#](https://github.com/OnurGumus/FCQRS/tree/main/samples/registration-http-csharp).
For the HTTP binding rules, see [ASP.NET Core parameter binding](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/parameter-binding?view=aspnetcore-10.0).
