module Bootstrap
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open Hocon.Extensions.Configuration
open System.IO
open FCQRS.Common

let configBuilder =
    ConfigurationBuilder()
        .AddHoconFile(Path.Combine(__SOURCE_DIRECTORY__, "config.hocon"))

let config = configBuilder.Build()

let loggerFactory =
    LoggerFactory.Create(fun builder -> builder.AddConsole() |> ignore) 

let actorApi = FCQRS.Actor.api config loggerFactory

let env = new Environments.AppEnv(config, loggerFactory)

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

let userSubs cid =
    actorApi.CreateCommandSubscription userShard cid

let sub handleEventWrapper offsetCount =
    FCQRS.Query.init actorApi offsetCount (handleEventWrapper env)