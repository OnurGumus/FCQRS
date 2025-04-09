(**
---
title: User Aggregate
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
### User Aggregate
The `User` aggregate is responsible for handling user registration and login commands. It maintains the state of the user, including the username and password.
In this example we will try to implement below flows <br>
<img src="../img/flows.png" alt="User Flow" width="800"/> <br>
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
For each aggregate, we define State, Command, and Event types. The `State` type represents the current state of the aggregate, while the `Command` type defines the commands that can be sent to the aggregate. 
The `Event` type defines the events that can be emitted by the aggregate.
*)

(** 
#### Command Handler
Next we define the command handler which these commands will come from the outside world.
*)

let handleCommand (cmd: Command<_>) state =
    match cmd.CommandDetails, state with
    | Register(userName, password), { Username = None } ->
         RegisterSucceeded(userName, password) |> PersistEvent

    | Register _, { Username = Some _ } -> AlreadyRegistered |> DeferEvent

    | Login password1,
        {   Username = Some _
            Password = Some password2 } when password1 = password2 -> 
                LoginSucceeded |> PersistEvent

    | Login _, _ -> LoginFailed |> DeferEvent


(**

 It handles the `Register` command by checking if the username is already registered. If not, it emits a `RegisterSucceeded` event. If the username is already registered, it emits an `AlreadyRegistered` event.
 The `Login` command is handled by checking if the username and password match. If they do, it emits a `LoginSucceeded` event. If not, it emits a `LoginFailed` event.

 *)

(** 
#### Event Handler
*)

let applyEvent event state =
    match event.EventDetails with
    | RegisterSucceeded(userName, password) ->
        { state with
            Username = Some userName
            Password = Some password }
    | _ -> state

(**
Not much going on here. By the time applyEvent is called, the event is already persisted. So we just update the state based on the event.
And the cycle continues with  the new state when a future command is received.
*)


(** 
#### Wiring to Akka.net
*)

(**Finally some boilerplate code to bind the above functions to the actor. Also initiates a shard. 
"User" here is the shard region name acts like a type
*)
let init (env: _) (actorApi: IActor) =
    let initialState = { Username = None; Password = None }
    actorApi.InitializeActor env initialState "User" handleCommand applyEvent

let factory (env: _) actorApi entityId =
    (init env actorApi).RefFor DEFAULT_SHARD entityId

(** You will call factory to create or resume an actor. Actually clusterd sharded actors are eternal. They are never born and never die. You can pretend
they exist since the beginning of time and will exist till the end of time. But you can stop them or after 2 minutes they  will be passivated to save memory.
*)