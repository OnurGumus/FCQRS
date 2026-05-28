(**
---
title: Quickstart
category: Quickstart
categoryindex: 2
index: 1
---
*)

(*** hide ***)
#r "nuget: FCQRS, *"

(**
# Quickstart

This page builds a complete FCQRS write-and-read loop — a `User` that can register and log in — in a
single file, using only the **`FCQRS`** package. There is no HOCON file and no configuration ceremony:
the framework ships with sensible Akka.NET defaults and you tell it just one thing, which database to
use.

If you want the *why* behind each piece — aggregates, events, the read side, sagas, correlation ids —
read the **[workshop](workshop/README.html)**, which teaches the whole model from first principles
in F# and C#. This page is the "get it running" companion; the fleshed-out version of everything here
is the [`sample/`](https://github.com/onurgumus/FCQRS/tree/main/sample) project in the repository.

Every code block below is compiled against the current `FCQRS` package as part of building this site,
so it cannot quietly drift out of date.

## Install

```
dotnet new console -lang F# -n MyApp
cd MyApp
dotnet add package FCQRS
```
*)

(*** hide ***)
open System
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Common
open FCQRS.Model.Data
open FCQRS.Actor

(**
## 1. The aggregate

An aggregate takes **commands** (requests) and emits **events** (facts); its state is folded from
those events and is never stored directly. The whole `User` is two types, a piece of state, and two
pure functions:
*)

module User =
    type State = { Username: string option; Password: string option }

    type Command =
        | Register of username: string * password: string
        | Login of password: string

    type Event =
        | RegisterSucceeded of string * string
        | AlreadyRegistered
        | LoginSucceeded
        | LoginFailed

    /// decide: look at the command and the current state, return what happened
    let handleCommand (cmd: Command<Command>) state =
        match cmd.CommandDetails, state with
        | Register(u, p), { Username = None } -> RegisterSucceeded(u, p) |> PersistEvent
        | Register _, { Username = Some _ } -> AlreadyRegistered |> DeferEvent
        | Login p, { Username = Some _; Password = Some stored } when p = stored ->
            LoginSucceeded |> PersistEvent
        | Login _, _ -> LoginFailed |> DeferEvent

    /// fold: apply one event to the state
    let applyEvent (event: Event<Event>) state =
        match event.EventDetails with
        | RegisterSucceeded(u, p) -> { state with Username = Some u; Password = Some p }
        | _ -> state

    let initialState = { Username = None; Password = None }

    /// bind the pure functions to a sharded, event-sourced actor
    let init (actorApi: IActor) =
        actorApi.InitializeActor initialState "User" handleCommand applyEvent

    let factory actorApi entityId =
        (init actorApi).RefFor DEFAULT_SHARD entityId

(**
`PersistEvent` stores the event, applies it to the state, and publishes it. `DeferEvent` publishes a
rejection *without* storing it — so a caller hears "already registered" without that non-event
polluting the journal. Both `handleCommand` and `applyEvent` are pure and have no dependency on
Akka.NET, so they are trivially unit-testable. (Walked through in detail in
[workshop Part 3](workshop/part-3-your-first-aggregate.html).)

## 2. Wiring — no HOCON required

You only have to supply a database `Connection`; the rest of the Akka configuration comes from the
framework's built-in defaults. An empty `IConfiguration` is perfectly fine. A `.hocon` file is
*optional* — reach for it only when you want to override those defaults (see
[HOCON configuration](hocon.html)).
*)

let buildActorApi () : IActor =
    // An empty configuration is fine — FCQRS supplies the Akka defaults.
    let config = ConfigurationBuilder().Build()
    // No logging providers, to keep this to the FCQRS package alone. Add the
    // Microsoft.Extensions.Logging.Console package and b.AddConsole() for console logs.
    let loggerFactory = LoggerFactory.Create(fun _ -> ())

    let connection =
        Some
            { ConnectionString = "Data Source=quickstart.db;" |> ValueLens.TryCreate |> Result.value
              DBType = DBType.Sqlite }

    let clusterName = "quickstart" |> ValueLens.TryCreate |> Result.value
    FCQRS.Actor.api config loggerFactory connection clusterName

(**
## 3. A minimal read side

A projection is a function called once per event, in order. This one does the simplest possible
thing — it forwards each `User` event to subscribers so a caller can be told when the read side has
caught up. In a real app you would write rows to a database here and record the offset in the same
transaction (see [workshop Part 5](workshop/part-5-the-read-side.html)).
*)

let handleEventWrapper (_loggerFactory: ILoggerFactory) (_offset: int64) (event: obj) =
    match event with
    | :? Event<User.Event> as e -> [ e :> IMessageWithCID ]
    | _ -> []

(**
## 4. Send a command and read your write

The client mints a correlation id (CID), **subscribes to it before sending the command**, fires the
command, and waits. By the time the wait returns, the read side has processed the event — so the next
query cannot be stale. (The full story is in
[workshop Part 6](workshop/part-6-client-coordination.html).)
*)

let run () =
    async {
        let actorApi = buildActorApi ()

        // This quickstart has no sagas, so the saga starter does nothing.
        let sagaCheck (_: obj) : (string -> Akkling.Cluster.Sharding.IEntityRef<obj>) list = []
        actorApi.InitializeSagaStarter sagaCheck

        let userShard = User.factory actorApi
        let subs = FCQRS.Query.init actorApi 0L (handleEventWrapper actorApi.LoggerFactory)

        let cid: CID = Guid.NewGuid().ToString() |> ValueLens.CreateAsResult |> Result.value
        let actorId: AggregateId = "alice" |> ValueLens.CreateAsResult |> Result.value

        // Subscribe to this CID *before* sending, so the confirmation can't be missed.
        use awaiter = subs.Subscribe((fun (e: IMessageWithCID) -> e.CID = cid), 1)

        let! event =
            actorApi.CreateCommandSubscription
                userShard cid actorId
                (User.Register("alice", "s3cret"))
                (fun (e: User.Event) ->
                    match e with
                    | User.RegisterSucceeded _ | User.AlreadyRegistered -> true
                    | _ -> false)
                None

        do! awaiter.Task |> Async.AwaitTask // read side is now up to date
        printfn "registered %A at version %A" event.EventDetails event.Version
    }

(**
Call it from your program's entry point:

```
[<EntryPoint>]
let main _ =
    run () |> Async.RunSynchronously
    0
```

## Where to go next

- **The concepts, in depth** — the [workshop](workshop/README.html), in F# and C#.
- **Reliable side effects with sagas** — [workshop Part 7](workshop/part-7-sagas.html).
- **Overriding the Akka defaults** — [HOCON configuration](hocon.html) (optional).
- **The full sample** — [`sample/`](https://github.com/onurgumus/FCQRS/tree/main/sample).
*)
