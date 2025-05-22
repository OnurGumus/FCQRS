module Bootstrap

open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open Hocon.Extensions.Configuration
open System.IO


// Create configuration. We don't need hocon here. It isn't used in the conventional sense.
// Any configuration could be used like appsettings.json. But I like hocon because it is super set of json.
let configBuilder =
    ConfigurationBuilder()
        .AddHoconFile(Path.Combine(__SOURCE_DIRECTORY__, "config.hocon"))

let config = configBuilder.Build()

// Create logger factory
let loggerF =
    LoggerFactory.Create(fun builder -> builder.AddConsole() |> ignore) 

// Composition root for the application environment. While being a god object is an anti-pattern, it plays nicely with F# partial application. 
let env = new Environments.AppEnv(config,loggerF)

// Bootstrap command side.
let actorApi = FCQRS.Actor.api env

// We don't use sagas (yet) so we just return an empty list.
let sagaCheck  _ = []

// Still necessary.
actorApi.InitializeSagaStarter sagaCheck

// Create a shard for the User aggregate. A shard is like parent of aggregate actrors.
let userShard = User.factory env actorApi

// Not necessary but prevents first time hit latency.
User.init env actorApi |> ignore

// helper function to send commands to the actor. cid means corralation id and it is used to track the command.
// essentially it could be any string typically uuid/guid.
let userSubs cid =  actorApi.CreateCommandSubscription userShard cid

// Initializes the query side.But also gets subscription for the query side. 
// The only use case for subscription is to wait for the query side to catch up with the command side.
let sub handleEventWrapper offsetCount= FCQRS.Query.init actorApi offsetCount (handleEventWrapper env)