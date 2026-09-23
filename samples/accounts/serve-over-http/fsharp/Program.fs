open System
open System.Data.Common
open System.IO
open System.Threading
open System.Threading.Tasks
open Dapper
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Data.Sqlite
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open FCQRS.Actor
open FCQRS.Common
open FCQRS.FSharp
open FCQRS.ProjectionStorage
open FCQRS.Projections
open Account

// docs:requests
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
// docs:end

// An ID or an owner must be non-blank and at most 255 characters.
let valid (value: string) =
    not (String.IsNullOrWhiteSpace value) && value.Length <= 255

let invalid =
    Results.BadRequest(
        {| error = "The ID and owner must be non-blank, at most 255 characters." |})

[<EntryPoint>]
let main args =
    let builder = WebApplication.CreateBuilder(args)
    builder.Logging.SetMinimumLevel(LogLevel.Warning) |> ignore
    use app = builder.Build()

    // Startup is the same as in step 4: the accounts and their statement.
    let database = Path.Combine(AppContext.BaseDirectory, "accounts.db")
    let connectionString = $"Data Source={database}"
    do
        use connection = new SqliteConnection(connectionString)
        Statement.createTable connection
    let logging = app.Services.GetRequiredService<ILoggerFactory>()
    let connection = Fcqrs.connect DBType.Sqlite connectionString
    let api = Fcqrs.actor builder.Configuration logging (Some connection) "accounts"
    try
        let accounts =
            Fcqrs.aggregate api
                { Name = "Account"
                  Initial = initial
                  Decide = decide
                  Fold = fold
                  Snapshots = Default
                  Passivation = PassivationPolicy.Default }
        Fcqrs.wireSagaStarters api []
        let store =
            SqlProjectionStore(
                ProjectionSqlDialect.Sqlite,
                Func<DbConnection>(fun () -> new SqliteConnection(connectionString)))
        let options = TransactionalProjectionOptions("Statement", store)
        use statement = Fcqrs.transactionalProjection api options Statement.handle

        // docs:send
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
        // docs:end

        // docs:endpoints
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
        // docs:end

        // docs:query
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
        // docs:end

        app.Lifetime.ApplicationStarted.Register(fun () ->
            printfn "Listening on %s" (String.Join(", ", app.Urls))) |> ignore
        app.Run()
        0
    finally
        api.Stop().Wait()
