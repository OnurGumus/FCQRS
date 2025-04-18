(**
---
title:  Running the Example
category: Walkthrough
categoryindex: 2
index: 6
---
*)
(*** hide ***)
#r  "nuget: FCQRS, *"
#r  "nuget: Hocon.Extensions.Configuration, *"
#r  "nuget: Microsoft.Extensions.Logging.Console, *"
#r  "../../sample/bin/Debug/net9.0/sample.dll"

open FCQRS.Model.Data
open Command

(**

## Running the example
 Above code is where we Bootstrap the read side and aquire some handle to subscribe to the events. 
 Last known offset is the last event that was processed. 
 Here for demo purposes, we are using 0L. In production, you should get the last known offset from the database or persistent storage.
*)
let lastKnownOffset = 0L
let sub = Bootstrap.sub Query.handleEventWrapper lastKnownOffset

(**
Then we create a function to generate a correlation id. This is used to track the command. We will explain ValueLens later. 
For now, just know that it is a way to create a value from a function.
*)

let cid (): CID =
    System.Guid.NewGuid().ToString() |> ValueLens.CreateAsResult |> Result.value

// user name and password for testing.
let userName = "testuser"

let password = "password"

let cid1 = cid()

(** 
 We create a subscription BEFORE sending the command.
This is to ensure that we don't miss the event.
We are interested in the first event that has the same CID as the one we are sending. 
*)
let s = sub.Subscribe((fun e -> e.CID = cid1), 1)

(** Send the command to register a new user. You can also use async block here *)
let result = register cid1 userName password |> Async.RunSynchronously

(** Wait for the event to happen. Means read-side is completed *)
(s |> Async.RunSynchronously).Dispose()
printfn "%A" result