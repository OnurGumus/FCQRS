(**
---
title: Aggregates and Sagas
category: Home
categoryindex: 1
index: 2
---
*)



(**
## Aggregates

An aggregate is a cluster of domain objects that can be treated as a single unit. 
It is a pattern used in Domain-Driven Design (DDD) to manage the complexity of a system by grouping related entities and value objects together. 
An aggregate has a root entity, known as the aggregate root, which is responsible for maintaining the consistency of the aggregate's state.
In FCQRS an aggregate is a cluster sharded actor. Because of this you don't have to construct the actor. 
They offer built in support for event sourcing and snapshotting. They are also thread safe.

Aggregates takes commands and emits events.
The commands are the requests to change the state of the aggregate. When an event is generated it is persisted and
applied to the state of the aggregate. Then the event is published so that it may start a saga.
 Below image shows an aggregate <br/>
 <img src="img/aggregate.png" alt="Aggragtes" width="800"/> <br>
*)

(**
## Sagas

A saga manages a long-running process. Their main purpose is 
to make the aggregates to talk to each other or external services by acting like persistent mediators.
They are also made as cluster sharded actors. But thanks to akka.net they are always auto started. 
They are not passivated. In FCQRS CorrelationID  (CID) is critical in sagas. A sagas name is generated
as originatorId__~Saga~_CID. This ensures that the saga is always started with the same CID and can find 
back the originator. The CID is also used to correlate the events and commands.

Sagas listen events but issue commands.
Below image shows a saga <br/>
<img src="img/saga2.png" alt="Saga" width="800"/> <br>

*)