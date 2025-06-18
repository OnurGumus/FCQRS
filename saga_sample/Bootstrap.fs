module Bootstrap
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open Hocon.Extensions.Configuration
open System.IO
open FCQRS.Common
open System
open FCQRS.Scheduler

let configBuilder =
    ConfigurationBuilder()
        .AddHoconFile(Path.Combine(__SOURCE_DIRECTORY__, "config.hocon"))

let config = configBuilder.Build()

let loggerFactory =
    LoggerFactory.Create(fun builder -> builder.AddConsole() |> ignore) 
let env = new Environments.AppEnv(config, loggerFactory)

let actorApi = FCQRS.Actor.api env

// Initialize the scheduler controller with the target task name
// Replace "YOUR_SPECIFIC_TASK_NAME_HERE" with your actual task name
FCQRS.SchedulerController.start actorApi.System.Scheduler


let userSagaShard = UserSaga.factory env actorApi
let sagaCheck (o: obj) =
    match o with
    | :? (FCQRS.Common.Event<User.Event>) as e ->
        match e.EventDetails with
        | User.VerificationRequested _ -> [ userSagaShard, id |> Some |> PrefixConversion, o ]
        | _ -> []
    | _ -> []

actorApi.InitializeSagaStarter sagaCheck

let userShard = User.factory env actorApi

User.init env actorApi |> ignore
UserSaga.init env actorApi |> ignore

let userSubs cid actorId command filter metadata =
    actorApi.CreateCommandSubscription userShard cid actorId command filter metadata

let sub handleEventWrapper offsetCount =
    FCQRS.Query.init actorApi offsetCount (handleEventWrapper env)

