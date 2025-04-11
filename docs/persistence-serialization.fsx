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
The `FCQRS.Serialization` module provides the following functions for JSON serialization:

* `encode` - Serializes an object to a JSON string
* `decode` - Deserializes a JSON element to a strongly-typed object
* `encodeToBytes` - Serializes an object to a UTF-8 byte array
* `decodeFromBytes` - Deserializes a byte array to a strongly-typed object

Example usage:
*)

(*** hide ***)
let exampleObject = {| Name = "Test"; Value = 42 |}

(**
```fsharp
// Serialize to JSON string
let json = Serialization.encode exampleObject

// Deserialize from JSON string
let decoded = Serialization.decode<{| Name: string; Value: int |}> json
```
*)

(**
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