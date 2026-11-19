open System
open System.IO
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Actor
open FCQRS.Common
open FCQRS.FSharp
open FCQRS.Workflows
open Account
open Welcome

// Events go to a SQLite file next to the program. A second run continues
// from it.
let database = Path.Combine(AppContext.BaseDirectory, "accounts.db")
let logging = LoggerFactory.Create(fun _ -> ())
let configuration = ConfigurationBuilder().Build()
let connection = Fcqrs.connect DBType.Sqlite $"Data Source={database}"
let api = Fcqrs.actor configuration logging (Some connection) "accounts"

let accounts =
    Fcqrs.aggregate api {
        Name = "Account"
        Initial = initial
        Decide = decide
        Fold = fold
        Snapshots = Default
        Passivation = PassivationPolicy.Default
    }
Fcqrs.wireSagaStarters api []

// A stand-in for the service that sends welcomes.
let welcomeService =
    { new IWelcomes with
        member _.Send(_, owner) =
            printfn $"Sending a welcome to {owner}"
            Task.FromResult true
    }

let welcomes = Workflows.register api (Welcome.definition welcomeService)

// docs:projection
// Starts a welcome for each stored Opened event. The workflow ID comes from
// the event, so a start for an event the projection hands over again stores
// nothing. The projection stores how far it has read after the handler
// returns, so a crash before that hands the event over again.
let startWelcome (event: obj) =
    match event with
    | :? Event<AccountEvent> as stored ->
        match stored.EventDetails with
        | Opened owner ->
            let id = $"welcome-{stored.Sender.Value}"
            welcomes.Start id owner |> Async.RunSynchronously |> ignore
        | _ -> ()
    | _ -> ()

let starter =
    Projection.single (Named "welcome-starter") startWelcome
    |> Projection.ofEntities [ "Account" ]
    |> Fcqrs.projection api
// docs:end

let alice = Fcqrs.aggregateId "alice"
let reply =
    accounts.Send (Fcqrs.newCid ()) alice (Open "Alice") (fun _ -> true)
    |> Async.RunSynchronously
match reply.EventDetails with
| Opened owner -> printfn $"Opened for {owner} (version {reply.Version}, stored)"
| Rejected reason ->
    printfn $"Rejected: {reason} (version {reply.Version}, not stored)"

// Wait until the projection has handled every stored event, so the welcome
// has started, then wait for it to end.
starter.CatchUpAsync().Wait()
match welcomes.Result "welcome-alice" (TimeSpan.FromSeconds 30.)
      |> Async.RunSynchronously with
| WorkflowOutcome.Finished result -> printfn $"welcome-alice: {result}"
| other -> printfn $"welcome-alice: %A{other}"

api.Stop().Wait()
