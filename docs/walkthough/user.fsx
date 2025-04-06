(**
---
title: User Aggregate
category: Walkthrough
categoryindex: 2
index: 3
---
*)

(*** hide ***)
#load "../../references.fsx"

(** 
### User Aggregate
The `User` aggregate is responsible for handling user registration and login commands. It maintains the state of the user, including the username and password.
*)

(** 
#### State, Command, and Event Types
*)

open FCQRS.Common

type State =
    { Username: string option
      Password: string option }

type Command =
    | Login of string
    | Register of string * string

type Event =
    | LoginSucceeded
    | LoginFailed
    | RegisterSucceeded of string * string
    | AlreadyRegistered

(**
For each aggregate, we define State, Command, and Event types. The `State` type represents the current state of the aggregate, while the `Command` type defines the commands that can be sent to the aggregate. The `Event` type defines the events that can be emitted by the aggregate.
*)
