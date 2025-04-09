(**
---
title: User Aggregate Modifications
category: Saga Walkthrough 
categoryindex: 3
index: 2
---
*)
(*** hide ***)
#r  "nuget: FCQRS, *"
#r  "nuget: Hocon.Extensions.Configuration, *"
#r  "../../saga_sample/bin/Debug/net9.0/saga_sample.dll"
open System.IO
open Microsoft.Extensions.Configuration
open Hocon.Extensions.Configuration
open FCQRS.Common
open System

(**


##  Modifications to User Aggregate
We have to modify the user aggregate such that instead of register succeeded event,  we will issue a verification requested event which in turn will start a saga.
Relevant changes are below
*)

type Event =
    | LoginSucceeded
    | LoginFailed
    | AlreadyRegistered
    | VerificationRequested of string * string  * string
    | Verified

type Command =
    | Login of string
    | Verify of string
    | Register of string * string

type State =
    { Username: string option
      VerificationCode: string option
      Password: string option }

let applyEvent event state =
    match event.EventDetails with
    | VerificationRequested(userName, password, code) ->
        { state with
            VerificationCode= Some code
            Username = Some userName
            Password = Some password }
    | _ -> state

let handleCommand (cmd: Command<_>) state =
    match cmd.CommandDetails, state with
    | Register(userName, password), { Username = None } -> 
        VerificationRequested(userName, password, 
            Random.Shared.Next(1_000_000).ToString()) |> PersistEvent

    | Register _, { Username = Some _ } -> AlreadyRegistered |> DeferEvent
    | Verify code, { VerificationCode = Some vcode } when code = vcode -> 
        Verified |> PersistEvent
    | Login password1,
      { Username = Some _
        Password = Some password2 } when password1 = password2 -> 
            LoginSucceeded |> PersistEvent
    | Verify _,  _ 
    | Login _, _ -> LoginFailed |> DeferEvent