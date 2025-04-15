(**
---
title: Commands and Events
category: Home
categoryindex: 1
index: 3
---
*)

(*** hide ***)
#r  "nuget: FCQRS, *"
#r  "nuget: Hocon.Extensions.Configuration, *"

open System.IO
open Microsoft.Extensions.Configuration
open Hocon.Extensions.Configuration
open System
open FCQRS.Common
open FCQRS.Model.Data

(**
## Commands

Both Commands and Events are actor messages. The difference is semantic whereas a command represents something
that is requested to be done, an event represents something that has already happened. We don't persist the commands but only events.
In your aggregates you define your commands as plain discriminated unions. But they they are auto wrapped
into FCQRS commands which have additional metadata.
*)

type Command<'CommandDetails> =
    { CommandDetails: 'CommandDetails
      CreationDate: DateTime
      Id: MessageId option
      Sender: ActorId option
      CorrelationId: CID }


(**
 You typically provide the CID which is a guid-like string and the command details which is your command object.
 Rest of the details are filled in by the framework. The command is then sent to the actor. 
*)


(**
## Events
Events are also actor messages. They are also auto wrapped in to FCQRS events which have additional metadata.
The events are persisted in the event store. The events are also wrapped in a discriminated union. Events represent
something that has already happened. Their CID's and Id's auto copied from the commands. You can also see they
have a version property. Everytime an event is persisted, the aggregates version is incremented. 
*)
type Event<'EventDetails> =
    { EventDetails: 'EventDetails
      CreationDate: DateTime
      Id: MessageId option
      Sender: ActorId option
      CorrelationId: CID
      Version: Version }
