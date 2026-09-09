open System
open System.Collections.Concurrent
open System.IO
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Common
open FCQRS.FSharp
open Account

let run () = async {
    let accountId = "alice"
    let id = Fcqrs.aggregateId accountId
    let database = Path.Combine(AppContext.BaseDirectory, "registration.db")
    // docs:projection
    let users = ConcurrentDictionary<string, string>()
    let ready = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let project (_offset: int64) (message: obj) =
        match message with
        | :? Event<UserRegistered> as event when event.Sender = Some id ->
            let (UserRegistered name) = event.EventDetails
            users[accountId] <- name
            ready.TrySetResult() |> ignore
        | _ -> ()
    // docs:end
    use logger = LoggerFactory.Create(fun _ -> ())
    let api = Fcqrs.actor (ConfigurationBuilder().Build()) logger
                  (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={database};")) "registration-fsharp"
    try
        // docs:startup
        let accounts = Fcqrs.aggregate api
                           { Name = "RegistrationFSharpAccount"; Initial = None
                             Decide = decide; Fold = fold; Snapshots = Default
                             Passivation = PassivationPolicy.Default }
        Fcqrs.wireSagaStarters api []
        // Register the observer before sending. Offset 0 also reads earlier registrations.
        Fcqrs.projection api (Projection.single 0 project) |> ignore
        // docs:end
        // docs:send
        let! reply = accounts.Send (Fcqrs.newCid ()) id (RegisterUser "Alice") (fun _ -> true)
        // docs:end
        let (UserRegistered name) = reply.EventDetails
        let result = if reply.Journaled = Some true then "Registered" else "Already registered"
        printfn "%s: %s (version %A)" result name reply.Version
        do! ready.Task.WaitAsync(TimeSpan.FromSeconds 30.) |> Async.AwaitTask
        printfn "Query: %s" users[accountId]
    finally
        api.Stop().GetAwaiter().GetResult()
}

run () |> Async.RunSynchronously
