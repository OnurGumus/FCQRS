(**
---
title: Saga
category: Saga Walkthrough 
categoryindex: 3
index: 3
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
open FCQRS
open Common
open Common.SagaStarter
open Akkling
open SendMail

(**

##   Saga
 Sagas also like aggregates have a state. They work the same way but instead they take events and issue commands whereas aggregates take commands and issue events.
 Sagas autostart and don't passivate, also need to implement an additional function called apply side effects.
 Below image denotes saga starting process <br/>
 <img src="../img/saga-starter.png" alt="Saga Starter" width="800"/> <br>

### Defining states

 First thing we do is to define the states. There are some constraints though. The first two states must be NotStarted with initial event and Started. This ensures
 in any crash we can rescover and land in the right state. The rest of the states can be any name you like.
*)

type State =
    | NotStarted
    | Started of SagaStartingEvent<Event<User.Event>>
    | SendingMail of Mail
    | Completed

(** 
 We can also define a data that is accross state. Here it won't be used but we have to define it.

*)

type SagaData = NA

(**
   And the initial state. Apply function doesn't do anything. It is just a placeholder for this particular example. In general , you can use 
   Data as a cross state data, you want to share between states.
*)

let initialState =
    { State = NotStarted
      Data = (NA: SagaData) }


      
let apply (sagaState: SagaState<SagaData, State>) = sagaState

(**

### Define event handler
The event handler is the function that will be called when an event is received. 
These events will come from other aggregates or services. 
The event handler will be called with the event and the current state of the saga. 
Then event handler can decide the next state of the saga which issues the side effects.

*)

let handleEvent (event: obj) (state: SagaState<SagaData, State>) = 
    match event, state with

    | :? string as str, _ when str = "mail sent" ->
        Completed |> StateChangedEvent

    | :? (Common.Event<User.Event>) as { EventDetails = userEvent }, state ->
        match userEvent, state with
        | User.VerificationRequested(email, _, code), _ ->
            SendingMail
                { To = email
                  Subject = "Your code"
                  Body = $"Your code is {code} !!" }
            |> StateChangedEvent
        | _ -> UnhandledEvent


    | _ -> UnhandledEvent

(**
The first event we handle is for demo cases is a simple string showing that the mail was sent. The second event is the verification requested event. This is the event that will be sent from the user aggregate.
 It will contain the email address and the code. We will use this information to send the email. The third event is the unhandled event. This is just a catch all for any other events that are not handled.
Observe that for state changes we use StateChangedEvent. These state changes are persisted.
 *)

(**
### Define side effects handler 

This is probably the most complex part of the saga. This is where you issue commands also can switch to next state.
The first three parameters aren't required in general but here they denote external dependencies.
*)

let applySideEffects
    (actorRef: unit -> IActorRef<obj>)
    env
    userFactory
    (sagaState: SagaState<SagaData, State>)
    (startingEvent: option<SagaStartingEvent<_>>)
    recovering =
(**
The originator is the actor that started the saga. This is used to send messages back to the actor that started the saga.
*)
    let originator =
            FactoryAndName
                { Factory = userFactory; Name = Originator }

    match sagaState.State with
(** We must start with NotStarted and then move to Started. This is the first state of the saga. 
Note that at each stage we return a tuple of three values. The first value is saga's own internal side effect, the second value is the next state and the third value is the commands to be sent.
The rest of the states can be any name you like. *)

     NotStarted -> NoEffect, Some(Started startingEvent.Value), []
(** 
Below section is almost boilerplate. We check if recovering is true. If it is a rare case where the saga is recovering from a crash.
Therefore we check if actually Aggregate resumed or not. Depending on the case we abort the saga or continue. More on this later.
*)
    | Started _ ->
        if recovering then
            let startingEvent = startingEvent.Value.Event

            NoEffect,
            None,
            [ { TargetActor = originator
                Command = ContinueOrAbort startingEvent
                DelayInMs = None } ]
        else
            ResumeFirstEvent, None, []

(** Then we define what is going to happen when we switch to SendingMail state. Essentially sending a mail command to actorRef 
which is coming from SendMail *)

    | SendingMail mail ->
        NoEffect,
        None,
        [ { TargetActor = ActorRef(actorRef ())
            Command = mail
            DelayInMs = None } ]

(** 
Finally we define the completed state. This is the final state of the saga. It stops the saga . Notice the usage of StopActor internal effect.
*)
    | Completed ->
        StopActor, None, []


(**
### Initilizers
In the init function we create a factory for our mail sender. Then pass it through the initialize Saga.
*)

let init (env: _) (actorApi: IActor) =
    let userFactory = User.factory env actorApi

    let mailSenderRef =
        fun () -> spawnAnonymous actorApi.System (props behavior) |> retype

    actorApi.InitializeSaga
        env
        initialState
        handleEvent
        (applySideEffects mailSenderRef env userFactory)
        apply
        "UserSaga"


let factory (env: _) actorApi entityId =
    (init env actorApi).RefFor DEFAULT_SHARD entityId