(**
---
title:  Query-side
category: Walkthrough
categoryindex: 2
index: 5
---
*)
(*** hide ***)
#r  "nuget: FCQRS, *"
#r  "nuget: Hocon.Extensions.Configuration, *"
#r  "nuget: Microsoft.Extensions.Logging.Console, *"
#r  "../../sample/bin/Debug/net9.0/sample.dll"


open FCQRS.Model.Data
open Bootstrap
open Command

(**

## Read-side
Here we will have one simple function. Every time an event is persisted, it will  be sent to the read side.
Here you can use whatever you like, be it SQL, Entity Framework, MongoDB, or whatever you like. Make sure you persist the offset value to a table or persistent storage
in the same transaction. This is to ensure that if the query side crashes, it can start from the last offset value. You can also use a transaction to commit them atomically.
<img src="../img/projection.png" alt="User Flow" width="800"/> <br>
*)

open Microsoft.Extensions.Logging
open FCQRS.Common

// All persisted events will come with monotonically increasing offset value.
let handleEventWrapper env (offsetValue: int64) (event:obj)=

        match event with
        | :? FCQRS.Common.Event<User.Event> as  event ->

          // Your SQL statements
          // make sure to update the offset value to a table 
          // or persistent storage in the same transaction.
            [event:> IMessageWithCID]
        |  _ -> []

(**
  Finally, we return a list of IMessageWithCID. This is a list of messages that are sent to the read side. Then optionall you can subribe to these messages.
  Which will be done in the next section. You can also return something completely custom. Your cross bounded context event or a DTO like event is preferred.
*)
