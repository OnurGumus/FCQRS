open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open FCQRS.Common
open FCQRS.FSharp
open Account
open UserView

[<CLIMutable>]
type RegistrationRequest = { Name: string }

let valid (value: string) = not (String.IsNullOrWhiteSpace(value)) && value.Length <= 255

[<EntryPoint>]
let main args =
    let builder = WebApplication.CreateBuilder(args)
    builder.Logging.SetMinimumLevel(LogLevel.Warning) |> ignore
    use app = builder.Build()
    let database = Path.Combine(AppContext.BaseDirectory, "registration-http.db")
    let users = UserView()
    let api = Fcqrs.actor builder.Configuration (app.Services.GetRequiredService<ILoggerFactory>())
                  (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={database};"))
                  "registration-http-fsharp"
    try
        let accounts = Fcqrs.aggregate api
                           { Name = "RegistrationFSharpAccount"; Initial = None
                             Decide = decide; Fold = fold; Snapshots = Default
                             Passivation = PassivationPolicy.Default }
        Fcqrs.wireSagaStarters api []
        Fcqrs.projection api (Projection.single 0 (fun offset message -> users.Project(offset, message)))
        |> ignore

        // docs:register
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
        // docs:end

        // docs:query
        let query (id: string) =
            if not (valid id) then Results.BadRequest()
            else
                match users.TryGet(id) with
                | true, name -> Results.Ok({| id = id; name = name |})
                | _ -> Results.NotFound()
        app.MapGet("/accounts/{id}", Func<string, IResult>(fun id -> query id)) |> ignore
        // docs:end

        app.Lifetime.ApplicationStarted.Register(fun () ->
            printfn "Listening on %s" (String.Join(", ", app.Urls))) |> ignore
        app.Run()
        0
    finally
        api.Stop().GetAwaiter().GetResult()
