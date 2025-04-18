(**
---
title: Registering Saga Starter
category: Saga Walkthrough 
categoryindex: 3
index: 4
---
*)
(*** hide ***)
//#load "../../saga_references.fsx"
#r  "nuget: FCQRS, *"
#r  "nuget: Hocon.Extensions.Configuration, *"
#r  "../../saga_sample/bin/Debug/net9.0/saga_sample.dll"

open System.IO
open Microsoft.Extensions.Configuration
open Hocon.Extensions.Configuration
open Bootstrap
open FCQRS.Common


(**


##  Registering Saga Starter
We just have to make a small change in our bootstrapper. So that VerificationRequested event will start the saga.
We only need the below change.
*)
let userSagaShard = UserSaga.factory env actorApi
let sagaCheck (o: obj) =
    match o with
    | :? (FCQRS.Common.Event<User.Event>) as e ->
        match e.EventDetails with
        | User.VerificationRequested _ ->
            [ userSagaShard, id |> Some |> PrefixConversion, o ]
        | _ -> []
    | _ -> []

actorApi.InitializeSagaStarter sagaCheck

(** 
We also need to initialize the user and saga actors.
*)
UserSaga.init env actorApi |> ignore
