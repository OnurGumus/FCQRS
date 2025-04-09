(**
---
title: Wiring Commands
category: Walkthrough
categoryindex: 2
index: 4
---
*)
(*** hide ***)
#r  "nuget: FCQRS, *"
#r  "nuget: Hocon.Extensions.Configuration, *"
#r  "nuget: Microsoft.Extensions.Logging.Console, *"
#r  "../../sample/bin/Debug/net9.0/sample.dll"


open FCQRS.Model.Data
open Bootstrap

(**


## Wiring Commands
We wire the command handler to the actor. The command handler is a function that takes a command and a state and returns an event. The event is then persisted to the event store.

First the register command:
*)
let register cid userName password =

    // Create an actor id from the username. Using ValueLens technique to create an ActorId. See my video.
    let actorId: ActorId = userName |> ValueLens.CreateAsResult |> Result.value

    let command = User.Register(userName, password)

    // Condition to wait until what we are interested in happens.
    let condition (e: User.Event) =
        e.IsAlreadyRegistered || e.IsRegisterSucceeded

    // Send the command to the actor.
    let subscribe = userSubs cid actorId command condition

    async {
        // Wait for the event to happen andd decide the outcome.
        match! subscribe with
        | { EventDetails = User.RegisterSucceeded _
            Version = v } -> return Ok v

        | { EventDetails = User.AlreadyRegistered
            Version = _ } -> 
            return Error [ sprintf "User %s is already registered" 
                <| actorId.ToString() ]
        | _ -> 
            return Error [ sprintf "Unexpected event for registration %s" 
                    <| actorId.ToString() ]
    }


(**

Next the login command:
*)
let login cid userName password =

    let actorId: ActorId = userName |> ValueLens.CreateAsResult |> Result.value

    let command = User.Login password

    let condition (e: User.Event) = e.IsLoginFailed || e.IsLoginSucceeded

    let subscribe = userSubs cid actorId command condition

    async {
        match! subscribe with
        | { EventDetails = User.LoginSucceeded
            Version = v } -> return Ok v

        | { EventDetails = User.LoginFailed
            Version = _ } -> 
                return Error [ sprintf "User %s Login failed" 
                    <| actorId.ToString() ]
        | _ -> 
            return Error [ sprintf "Unexpected event for user %s" 
                <| actorId.ToString() ]
    }
