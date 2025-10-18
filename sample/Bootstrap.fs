module Bootstrap

open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open Hocon.Extensions.Configuration
open System.IO
open FCQRS.Actor
open FCQRS.Model.Data


// Create configuration for connection-string (used by query side)
let configBuilder =
    ConfigurationBuilder()
        .AddHoconFile(Path.Combine(__SOURCE_DIRECTORY__, "config.hocon"))

let config = configBuilder.Build()

// Create logger factory
let loggerF =
    LoggerFactory.Create(fun builder -> builder.AddConsole() |> ignore)

// Create connection for SQLite - this will be merged with the hocon file config
let connectionString: ShortString =
    "Data Source=demo.db;" |> ValueLens.TryCreate |> Result.value

let connection =
    Some {
        ConnectionString = connectionString
        DBType = DBType.Sqlite
    }

// Composition root for the application environment. While being a god object is an anti-pattern, it plays nicely with F# partial application.

// Bootstrap command side with Connection parameter
let actorApi = FCQRS.Actor.api config loggerF connection

// We don't use sagas (yet) so we just return an empty list.
let sagaCheck  _ = []

// Still necessary.
actorApi.InitializeSagaStarter sagaCheck

// Create a shard for the User aggregate. A shard is like parent of aggregate actrors.
let userShard = User.factory actorApi

// Not necessary but prevents first time hit latency.
User.init actorApi |> ignore

// helper function to send commands to the actor. cid means corralation id and it is used to track the command.
// essentially it could be any string typically uuid/guid.
let userSubs cid actorId command filter metadata =  actorApi.CreateCommandSubscription userShard cid actorId command filter metadata

// Initializes the query side.But also gets subscription for the query side. 
// The only use case for subscription is to wait for the query side to catch up with the command side.
let sub handleEventWrapper offsetCount= FCQRS.Query.init actorApi offsetCount handleEventWrapper