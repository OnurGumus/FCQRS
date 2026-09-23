---
title: Serve an aggregate over HTTP
category: Apply
categoryindex: 4
index: 15
---

# Serve an aggregate over HTTP

Put the tutorial's account behind ASP.NET Core endpoints: open an account, deposit, withdraw, and read
the statement. The sample builds on [Show a statement](../tutorial/show-a-statement.html) and compiles
that step's `Account` and `Statement` files unchanged. [Jump to the requests](#Run-the-API).

## Send a command and wait for the statement

Each POST sends one command to the account. When the account stores an event, the endpoint waits for
the statement projection to handle it, as [Read your writes](read-your-writes.html) describes, and then
reads the balance from the statement. The response therefore includes the caller's own change. It can
also include a later change: two concurrent deposits can both report the balance after the second.

<!-- sample: accounts/serve-over-http/fsharp Program.fs send -->
```fsharp
// Sends a command and waits until the statement includes the event it
// stored, so the balance in the response includes this change.
let send (id: string) (command: AccountCommand) (ct: CancellationToken) =
    task {
        try
            let! reply =
                Fcqrs.sendAwaiting statement accounts (Fcqrs.newCid ())
                    (Fcqrs.aggregateId id) command (fun _ -> true)
                |> fun work -> Async.StartAsTask(work, cancellationToken = ct)
            match reply.EventDetails with
            // A rejection is not stored. Report the account's reason.
            | Rejected reason ->
                return Results.UnprocessableEntity({| error = reason |})
            | _ ->
                use connection = new SqliteConnection(connectionString)
                let! balance =
                    connection.ExecuteScalarAsync<decimal>(
                        "SELECT balance FROM statement WHERE account = @Id
                         ORDER BY version DESC LIMIT 1",
                        {| Id = id |})
                return Results.Ok({| balance = balance |})
        with :? TimeoutException ->
            // The account may have stored the event after all.
            return Results.Problem(
                "The command may have completed. "
                + "Read the statement before retrying.",
                statusCode = StatusCodes.Status503ServiceUnavailable)
    }
```

<div class="cs-alt"></div>

<!-- sample: accounts/serve-over-http/csharp Program.cs send -->
```csharp
// Sends a command and waits until the statement includes the event it stored,
// so the balance in the response includes this change.
async Task<IResult> Send(
    string id, AccountCommand command, CancellationToken ct)
{
    var accounts = app.Services
        .GetRequiredService<Handler<AccountCommand, AccountEvent>>();
    var statement = app.Services.GetRequiredService<IProjection>();
    var cid = Values.NewCID();
    // Subscribe first: a notification sent before the subscription is lost.
    using var projected = statement.SubscribeForFirst(cid);
    try
    {
        var account = Values.CreateAggregateId(id);
        var reply = await accounts(_ => true, cid, account, command)
            .WaitAsync(ct);
        // A rejection is not stored. Report the account's reason.
        if (reply.EventDetails is Rejected rejected)
            return Results.UnprocessableEntity(new { error = rejected.Reason });
        await projected.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        using var connection = new SqliteConnection(connectionString);
        var balance = await connection.ExecuteScalarAsync<decimal>(
            """
            SELECT balance FROM statement WHERE account = @Id
            ORDER BY version DESC LIMIT 1
            """,
            new { Id = id });
        return Results.Ok(new { balance });
    }
    catch (TimeoutException)
    {
        // The account may have stored the event after all.
        return Results.Problem(
            "The command may have completed. " +
            "Read the statement before retrying.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
```

A rejection is a reply the account did not store, so no projection waits for it. The endpoint returns
**422 Unprocessable Entity** with the account's reason. A timeout returns **503**: the account may
have stored the event, and the caller cannot tell from the response.

## Map the endpoints

The request bodies and statement rows are plain types:

<!-- sample: accounts/serve-over-http/fsharp Program.fs requests -->
```fsharp
// The JSON bodies the POST endpoints accept.
[<CLIMutable>]
type OpenRequest = { Owner: string }

[<CLIMutable>]
type AmountRequest = { Amount: decimal }

// One statement row, as the GET endpoint returns it.
[<CLIMutable>]
type Row =
    { Version: int64
      Entry: string
      Amount: decimal
      Balance: decimal }
```

<div class="cs-alt"></div>

<!-- sample: accounts/serve-over-http/csharp Program.cs requests -->
```csharp
// The JSON bodies the POST endpoints accept.
record OpenRequest(string? Owner);
record AmountRequest(decimal Amount);

// One statement row, as the GET endpoint returns it.
sealed class Row
{
    public long Version { get; init; }
    public string Entry { get; init; } = "";
    public decimal Amount { get; init; }
    public decimal Balance { get; init; }
}
```

Each POST endpoint validates the account ID and turns its body into one command:

<!-- sample: accounts/serve-over-http/fsharp Program.fs endpoints -->
```fsharp
// Each POST turns its request into one account command.
app.MapPost("/accounts/{id}",
    Func<string, OpenRequest, CancellationToken, Task<IResult>>(
        fun id request ct ->
            if valid id && valid request.Owner then
                send id (Open request.Owner) ct
            else Task.FromResult invalid)) |> ignore
app.MapPost("/accounts/{id}/deposits",
    Func<string, AmountRequest, CancellationToken, Task<IResult>>(
        fun id request ct ->
            if valid id then send id (Deposit request.Amount) ct
            else Task.FromResult invalid)) |> ignore
app.MapPost("/accounts/{id}/withdrawals",
    Func<string, AmountRequest, CancellationToken, Task<IResult>>(
        fun id request ct ->
            if valid id then send id (Withdraw request.Amount) ct
            else Task.FromResult invalid)) |> ignore
```

<div class="cs-alt"></div>

<!-- sample: accounts/serve-over-http/csharp Program.cs endpoints -->
```csharp
// Each POST turns its request into one account command.
app.MapPost("/accounts/{id}",
    (string id, OpenRequest request, CancellationToken ct) =>
        Valid(id) && Valid(request.Owner)
            ? Send(id, new Open(request.Owner), ct)
            : Task.FromResult(invalid));
app.MapPost("/accounts/{id}/deposits",
    (string id, AmountRequest request, CancellationToken ct) =>
        Valid(id) ? Send(id, new Deposit(request.Amount), ct)
            : Task.FromResult(invalid));
app.MapPost("/accounts/{id}/withdrawals",
    (string id, AmountRequest request, CancellationToken ct) =>
        Valid(id) ? Send(id, new Withdraw(request.Amount), ct)
            : Task.FromResult(invalid));
```

The account decides what the command means. The endpoints check only what the account cannot: the
shape of the ID and the owner's name.

## Run the API

From `samples/accounts`, whose `global.json` selects the .NET 11 SDK:

```text
dotnet run --project serve-over-http/fsharp -- --urls http://localhost:5080
```

<div class="cs-alt" data-fs="text" data-cs="text"></div>

```text
dotnet run --project serve-over-http/csharp -- --urls http://localhost:5080
```

Wait for `Listening on http://localhost:5080`. In a second terminal, open Alice's account and deposit
100:

```sh
curl http://localhost:5080/accounts/alice \
  -H 'Content-Type: application/json' -d '{"owner":"Alice"}'
curl http://localhost:5080/accounts/alice/deposits \
  -H 'Content-Type: application/json' -d '{"amount":100}'
```

The responses are `200 OK` with the balance after each command:

```json
{"balance":0}
{"balance":100}
```

Withdraw more than the balance:

```sh
curl -i http://localhost:5080/accounts/alice/withdrawals \
  -H 'Content-Type: application/json' -d '{"amount":500}'
```

The response is `422 Unprocessable Entity` with the account's reason:

```json
{"error":"Insufficient funds: 100 available"}
```

Read the statement:

```sh
curl http://localhost:5080/accounts/alice/statement
```

The response has one row per stored event:

```json
[
  { "version": 1, "entry": "Opened for Alice", "amount": 0, "balance": 0 },
  { "version": 2, "entry": "Deposit", "amount": 100, "balance": 100 }
]
```

The rejected withdrawal has no row: the account did not store it.

## GET the statement

The GET endpoint reads the statement table with SQL:

<!-- sample: accounts/serve-over-http/fsharp Program.fs query -->
```fsharp
// Reads one account's statement with SQL.
let statementOf (id: string) =
    task {
        use connection = new SqliteConnection(connectionString)
        let! rows =
            connection.QueryAsync<Row>(
                "SELECT version, entry, amount, balance FROM statement
                 WHERE account = @Id ORDER BY version",
                {| Id = id |})
        return
            if Seq.isEmpty rows then Results.NotFound()
            else Results.Ok rows
    }
// A lambda keeps the parameter name `id`, which binds the route value.
app.MapGet("/accounts/{id}/statement",
    Func<string, Task<IResult>>(fun id -> statementOf id)) |> ignore
```

<div class="cs-alt"></div>

<!-- sample: accounts/serve-over-http/csharp Program.cs query -->
```csharp
// Reads one account's statement with SQL.
app.MapGet("/accounts/{id}/statement", async (string id) =>
{
    using var connection = new SqliteConnection(connectionString);
    var rows = (await connection.QueryAsync<Row>(
        """
        SELECT version, entry, amount, balance FROM statement
        WHERE account = @Id ORDER BY version
        """,
        new { Id = id })).AsList();
    return rows.Count == 0 ? Results.NotFound() : Results.Ok(rows);
});
```

Stop with **Ctrl+C** and run again. The journal and the statement are both in
`bin/Debug/net11.0/accounts.db` inside the sample's folder, so the statement is still there, and the
projection continues from its stored progress.

- **400:** the ID or owner is blank or longer than 255 characters, or the JSON body is invalid.
- **404:** the statement has no rows for this account, because it was never opened.
- **422:** the account rejected the command. Nothing was stored; `error` holds the reason.
- **503:** the command or the 30-second statement wait timed out. The command may have completed.

A deposit is not idempotent: sending the same POST twice deposits twice. After a 503, read the
statement before retrying. An API that retries automatically needs a request ID from the client and
an account that rejects a repeated one, as the transfer IDs in
[Transfer money](../tutorial/transfer-money.html) do.

The sample has no authentication: any caller can move any account's money. Add ASP.NET Core
authentication and authorization before exposing endpoints like these.

Complete source: [F#](https://github.com/OnurGumus/FCQRS/tree/main/samples/accounts/serve-over-http/fsharp) ·
[C#](https://github.com/OnurGumus/FCQRS/tree/main/samples/accounts/serve-over-http/csharp).
For the HTTP binding rules, see [ASP.NET Core parameter binding](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/parameter-binding?view=aspnetcore-10.0).
