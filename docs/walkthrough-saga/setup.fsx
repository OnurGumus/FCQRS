(**
---
title: Intro
category: Saga Walkthrough 
categoryindex: 3
index: 1
---
*)
(*** hide ***)
#r  "nuget: FCQRS, *"
#r  "nuget: Hocon.Extensions.Configuration, *"
#r  "../../saga_sample/bin/Debug/net9.0/saga_sample.dll"
open System.IO
open Microsoft.Extensions.Configuration
open Hocon.Extensions.Configuration

(**


##  Adding Sagas
So far our aggregate was simple and didn't have side effects. But what happens if we want to send an email or make a call to an external service? Or 
what happens if we want to send a command to another aggregate? In this case we need to use a saga. A saga manages a potentionally long-running 
business process which interacts with one or more external services or resources it can also act as a Distributed Transaction Manager.  
Sagas can include logic for timers and retries as well.

We will revise our app such that it will now send a verification email to the user when they register.
For that let's add a Send Mail actor. Note that this can be done without an actor, but our sagas nicely support sending messages to actors. So far
our user aggregate was a cluster sharded actor. Whereas sagas will also be cluster sharded actors with auto start mode thanks to `remember-entities` feature of Akka.NET.
Akka.net would auto restart any actor makred as rembember entities which we specify while initializing the saga. This is set for you when you initialize the saga.
You can find the full sample code in the sample folder of repo.

The mail sending actor however will  be a normal actor. Inside send mail module:
*)
open Akkling

type Mail =
    { To: string
      Subject: string
      Body: string }


let behavior (m: Actor<_>) =
    let rec loop () =
        actor {
            let! (mail: obj) = m.Receive()
            match mail with
            | :? Mail as mail ->
                printfn "Sending mail to %A !!" mail
                m.Sender().Tell("sent", m.Self.Underlying 
                    :?> Akka.Actor.IActorRef)
                return! loop ()
                
            | _ ->
                return! loop ()
        }

    loop ()