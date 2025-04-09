(**
---
title: Bootstrap
category: Walkthrough
categoryindex: 2
index: 3
---
*)
(*** hide ***)
#r  "nuget: FCQRS, *"
#r  "nuget: Hocon.Extensions.Configuration, *"
#r  "nuget: Microsoft.Extensions.Logging.Console, *"
#r  "../../sample/bin/Debug/net9.0/sample.dll"

(**
## Bootstrapping
The ultimate objective of write site boottrapping is to make a call to FCQRS.Actor.api which initializes the actor system and starts the write site.
This API requires a configuration and a logger factory. The configuration is used to configure the actor system and the logger factory is used to log messages from the actor system.

*)
open System.IO
open Microsoft.Extensions.Configuration
open Hocon.Extensions.Configuration
open Microsoft.Extensions.Logging

let configBuilder =
    ConfigurationBuilder()
        .AddHoconFile(Path.Combine(__SOURCE_DIRECTORY__, "config.hocon"))

let config = configBuilder.Build()

let loggerFactory =
    LoggerFactory.Create(fun builder -> builder.AddConsole() |> ignore) 
(**
You don't have to use HOCON configuration. You can use any configuration system you like , as long as you can build a configuration object that implements `IConfiguration`.

*)


let actorApi = FCQRS.Actor.api config loggerFactory

actorApi.InitializeSagaStarter (fun _ -> [])

(**
Next we create a shard for the User aggregate. A shard is like parent of aggregate actors. And initilize the shard.
*)

let env = new Environments.AppEnv(config,loggerFactory)

let userShard = User.factory env actorApi

User.init env actorApi |> ignore


(** 
We create a helper function to send commands to the actor. cid means corralation id and it is used to track the command.
*)
let userSubs cid =  actorApi.CreateCommandSubscription userShard cid

(** 
Finally we initialize the query side. But also gets subscription for the query side. *)
let sub handleEventWrapper offsetCount = 
    FCQRS.Query.init actorApi offsetCount (handleEventWrapper env)