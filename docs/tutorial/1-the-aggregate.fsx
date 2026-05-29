(**
---
title: 1. The aggregate
category: Tutorial
categoryindex: 3
index: 2
---
*)

(*** hide ***)
#r "nuget: FCQRS, *"
open FCQRS.Common
open FCQRS.Model.Data
open FCQRS.Actor

(**
# 1. The aggregate

An [aggregate](../concepts/aggregates.html) is the write side's unit of consistency — one actor per
entity, processing one command at a time. Writing one is three types and two pure functions. Let's do
the `User`.

## State

State is what we fold events into. A user we know about has a username and a password; a brand-new
one has neither, so both are optional.
*)

type State = { Username: string option; Password: string option }
let initialState = { Username = None; Password = None }

(**
## Commands and events

A **command** is a request (imperative); an **event** is a fact (past tense). They are not the same
set — a `Register` command might produce a `RegisterSucceeded` fact *or* an `AlreadyRegistered` one.
*)

type Command =
    | Register of username: string * password: string
    | Login of password: string

type Event =
    | RegisterSucceeded of string * string
    | AlreadyRegistered
    | LoginSucceeded
    | LoginFailed

(**
## Decide: `handleCommand`

Given the command and the current state, return an **action**. `PersistEvent` stores the event,
applies it, and publishes it; `DeferEvent` publishes a rejection without storing it (so the caller
hears "no" without that non-event polluting history).
*)

let handleCommand (cmd: Command<Command>) state =
    match cmd.CommandDetails, state with
    | Register(u, p), { Username = None } -> RegisterSucceeded(u, p) |> PersistEvent
    | Register _, { Username = Some _ } -> AlreadyRegistered |> DeferEvent
    | Login p, { Username = Some _; Password = Some stored } when p = stored ->
        LoginSucceeded |> PersistEvent
    | Login _, _ -> LoginFailed |> DeferEvent

(**
## Fold: `applyEvent`

Apply one event to the state. This runs both when a new event is persisted and when old events are
replayed on recovery, so it must be pure and do nothing but fold.
*)

let applyEvent (event: Event<Event>) state =
    match event.EventDetails with
    | RegisterSucceeded(u, p) -> { state with Username = Some u; Password = Some p }
    | _ -> state

(**
## Bind it to an actor

`InitializeActor` ties the two functions to a sharded, event-sourced actor. `factory` gives you a
reference to a specific user by id. That is the entire aggregate — no base class, no attributes.
*)

let init (actorApi: IActor) =
    actorApi.InitializeActor initialState "User" handleCommand applyEvent

let factory actorApi entityId =
    (init actorApi).RefFor DEFAULT_SHARD entityId

(**
Both `handleCommand` and `applyEvent` are pure F# — no Akka in sight — so you can unit-test them
directly. Next: [wiring and running it](2-running-it.html).
*)
