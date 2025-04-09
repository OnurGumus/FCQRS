(**
---
title: Running the program
category: Saga Walkthrough 
categoryindex: 3
index: 6
---
*)
(*** hide ***)
#load "../../saga_references.fsx"
open System.IO
open Microsoft.Extensions.Configuration
open Hocon.Extensions.Configuration
open Bootstrap
open FCQRS.Common
open FCQRS.Model.Data
open Command

(**


##  Running the Program 
We start as usual.
*)


let sub = Bootstrap.sub Query.handleEventWrapper 0L

let cid (): CID =
    System.Guid.NewGuid().ToString() |> ValueLens.CreateAsResult |> Result.value

let userName = "testuser"

let password = "password"

let cid1 = cid()

let s = sub.Subscribe((fun e -> e.CID = cid1), 1)
let result = register cid1 userName password |> Async.RunSynchronously
(s |> Async.RunSynchronously).Dispose()
printfn "%A" result

(** Now we read the code from terminal. We should observe the mail already sent*)
let code = System.Console.ReadLine() 

(** Observe verification *)
let resultVerify = verify (cid()) userName code |> Async.RunSynchronously
printfn "%A" resultVerify

System.Console.ReadKey() |> ignore

(** Try registering the same user again. *)
let resultFailure = register (cid()) userName password |> Async.RunSynchronously
printfn "%A" resultFailure

System.Console.ReadKey() |> ignore

(** Login the user with incorrect password. *)
let loginResultF = login (cid()) userName "wrong pass" |> Async.RunSynchronously
printfn "%A" loginResultF

(** Login the user with correct password. *)
let loginResultS = login (cid()) userName password |> Async.RunSynchronously
printfn "%A" loginResultS
System.Console.ReadKey() |> ignore

(**
## Summary
In this example, we have shown how to use the `FCQRS` library to implement a simple user registration and login system with email verification. 
We have also shown how to use the `FCQRS` library to implement a simple user registration and 
login system with email verification.


*)
