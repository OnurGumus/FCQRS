(**
---
title: Persistence and Serialization
category: Home
categoryindex: 1
index: 2
---
*)



(**
## Persistence

 FCQRS uses Akka.Persistence to persist the events. The events are stored in a database. The database is configured via IConfiguration
 in our walkthroughs we use HOCON configuration. Also by default it is configured to take auto snapshot at every 30 events.
 The snapshot is used to speed up the recovery of the actor. The events are stored in a journal and the snapshots are stored in a snapshot store.
 Recovery is the process that actor replays the events from the journal and applies them to the state of the actor.
*)

(**
## Serialization
You can configure the serialization of the events and commands. By default FCQRS uses a custom json serializer based on FSharp.SystemText.Json.
In the hocon file you can see it configured 

          serializers {
                    json = "Akka.Serialization.NewtonSoftJsonSerializer"
                    stj = "FCQRS.ActorSerialization+STJSerializer, FCQRS"
               }
               serialization-bindings {
                    "System.Object" = json
                    "FCQRS.Common+ISerializable, FCQRS" = stj
               }

*)