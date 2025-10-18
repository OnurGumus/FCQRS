module Bootstrap
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open Hocon.Extensions.Configuration
open System.IO
open FCQRS.Common
open FCQRS.Actor
open FCQRS.Model.Data
open System
open FCQRS.Scheduler

// Load scheduler configuration from hocon file
let configBuilder =
    ConfigurationBuilder()
        .AddHoconFile(Path.Combine(__SOURCE_DIRECTORY__, "config.hocon"))

let config = configBuilder.Build()

let loggerFactory =
    LoggerFactory.Create(fun builder -> builder.AddConsole() |> ignore)

// Create connection for SQLite - this will be merged with the hocon file config
let connectionString: ShortString =
    "Data Source=demo.db;" |> ValueLens.TryCreate |> Result.value

let connection =
    Some {
        ConnectionString = connectionString
        DBType = DBType.Sqlite
    }

// Keep env for Query module and other modules that still use wrapper pattern

// The api will merge the connection (default.hocon) with the existing config (scheduler)
let actorApi = FCQRS.Actor.api config loggerFactory connection

// Initialize the scheduler controller with the target task name
// Replace "YOUR_SPECIFIC_TASK_NAME_HERE" with your actual task name
FCQRS.SchedulerController.start actorApi.System.Scheduler

let userSagaShard = UserSaga.factory actorApi
let sagaCheck (o: obj) =
    match o with
    | :? (FCQRS.Common.Event<User.Event>) as e ->
        match e.EventDetails with
        | User.VerificationRequested _ -> [ userSagaShard, id |> Some |> PrefixConversion, o ]
        | _ -> []
    | _ -> []

actorApi.InitializeSagaStarter sagaCheck

let userShard = User.factory actorApi

User.init actorApi |> ignore
UserSaga.init actorApi |> ignore

let userSubs cid actorId command filter metadata =
    actorApi.CreateCommandSubscription userShard cid actorId command filter metadata

let sub handleEventWrapper offsetCount =
    FCQRS.Query.init actorApi offsetCount handleEventWrapper

