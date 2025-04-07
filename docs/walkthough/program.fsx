(**
---
title:  Running the Example
category: Walkthrough
categoryindex: 2
index: 6
---
*)
(*** hide ***)
#load "../../references.fsx"

open FCQRS.Model.Data
open BootStrap
open Command

(**

## Running the example
 Above code is where we Bootstrap the read side and aquire some handle to subscribe to the events. 
*)
let sub = BootStrap.sub Query.handleEventWrapper 0L

(**
Then we create a function to generate a correlation id. This is used to track the command. We will explain ValueLens later. For now, just know that it is a way to create a value from a function.
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
let result = (register cid1 userName password) |> Async.RunSynchronously

(** Wait for the event to happen. Means read-side is completed *)
(s |> Async.RunSynchronously).Dispose()
printfn "%A" result