(**
---
title: Get started
category: Get started
categoryindex: 2
index: 1
---
*)

(*** hide ***)
#r "nuget: FCQRS, *"

(**
# Get started

This page builds a complete FCQRS write-and-read loop — a `User` that can register and log in — in a
single file, using only the **`FCQRS`** package. No HOCON file, no configuration ceremony: the
framework ships with sensible Akka.NET defaults, and you tell it just one thing — which database to
use.

Want the *why* behind each piece first? Read [Concepts](concepts/index.html). Want to build it up
gradually with explanation at each step? Follow the [Tutorial](tutorial/index.html). This page is the
five-minute version, and the fleshed-out original lives in the
[`sample/`](https://github.com/onurgumus/FCQRS/tree/main/sample) project.

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

An aggregate takes **commands** and emits **events**; its state is folded from those events and is
never stored directly. The whole `User` is two types, a piece of state, and two pure functions:
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
`PersistEvent` stores the event, applies it, and publishes it. `DeferEvent` publishes a rejection
*without* storing it. Both functions are pure and Akka-free, so they are trivially testable. (Concept:
[Aggregates and the write side](concepts/aggregates.html).)

## 2. Wiring — no HOCON required

You only supply a database `Connection`; the rest of the Akka configuration comes from built-in
defaults, and an empty `IConfiguration` is fine. A `.hocon` file is *optional* — see
[Configuration](configuration.html).
*)

let buildActorApi () : IActor =
    let config = ConfigurationBuilder().Build()
    // No logging providers, to keep this to the FCQRS package alone. Add the
    // Microsoft.Extensions.Logging.Console package and b.AddConsole() for console logs.
    let loggerFactory = LoggerFactory.Create(fun _ -> ())

    let connection =
        Some
            { ConnectionString = "Data Source=getstarted.db;" |> ValueLens.TryCreate |> Result.value
              DBType = DBType.Sqlite }

    let clusterName = "getstarted" |> ValueLens.TryCreate |> Result.value
    FCQRS.Actor.api config loggerFactory connection clusterName

(**
## 3. A minimal read side

A projection is called once per event, in order. This one just forwards each `User` event to
subscribers so a caller can be told when the read side has caught up. (Concept:
[The read side](concepts/read-models.html).)
*)

let handleEventWrapper (_loggerFactory: ILoggerFactory) (_offset: int64) (event: obj) =
    match event with
    | :? Event<User.Event> as e -> [ e :> IMessageWithCID ]
    | _ -> []

(**
## 4. Send a command and read your write

Mint a correlation id, **subscribe to it before sending**, fire the command, and wait. By the time
the wait returns, the read side has processed the event. (Concept:
[Consistency and recovery](concepts/consistency-and-recovery.html).)
*)

let run () =
    async {
        let actorApi = buildActorApi ()

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

## Next steps

- **Build it up with explanation** — the [Tutorial](tutorial/index.html).
- **Understand the model** — [Concepts](concepts/index.html).
- **Do specific tasks** — the [How-to guides](how-to/index.html).
- **From C#** — [Use FCQRS from C#](how-to/use-from-csharp.html).
*)
