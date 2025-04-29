(**
---
title: Wiring Commands
category: Saga Walkthrough 
categoryindex: 3
index: 5
---
*)
(*** hide ***)
#r  "nuget: FCQRS, *"
#r  "nuget: Hocon.Extensions.Configuration, *"
#r  "../../saga_sample/bin/Debug/net8.0/saga_sample.dll"
open System.IO
open Microsoft.Extensions.Configuration
open Hocon.Extensions.Configuration
open Bootstrap
open FCQRS.Common
open FCQRS.Model.Data


(**


##  Wiring Commands
We wire up the commands as usual. Nothing interesting here.
*)


let register cid userName password =

    let actorId: ActorId = userName |> ValueLens.CreateAsResult |> Result.value

    let command = User.Register(userName, password)

    let condition (e: User.Event) =
        e.IsAlreadyRegistered || e.IsVerificationRequested

    let subscribe = userSubs cid actorId command condition

    async {
        match! subscribe with
        | { EventDetails = User.VerificationRequested _
            Version = v } -> return Ok v

        | { EventDetails = User.AlreadyRegistered
            Version = _ } -> return Error [ sprintf "User %s is already registered" <| actorId.ToString() ]
        | _ -> return Error [ sprintf "Unexpected event for registration %s" <| actorId.ToString() ]
    }


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
            Version = _ } -> return Error [ sprintf "User %s Login failed" <| actorId.ToString() ]
        | _ -> return Error [ sprintf "Unexpected event for user %s" <| actorId.ToString() ]
    }

let verify cid userName code =

    let actorId: ActorId = userName |> ValueLens.CreateAsResult |> Result.value

    let command = User.Verify code

    let condition (e: User.Event) = e.IsVerified || e.IsLoginFailed

    let subscribe = userSubs cid actorId command condition

    async {
        match! subscribe with
        | { EventDetails = User.Verified
            Version = v } -> return Ok v
        | { EventDetails = User.LoginFailed
            Version = _ } -> return Error [ sprintf "User %s Login failed" <| actorId.ToString() ]

        | _ -> return Error [ sprintf "Unexpected event for user %s" <| actorId.ToString() ]
    }