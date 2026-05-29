(**
---
title: 2. Wiring and running it
category: Tutorial
categoryindex: 3
index: 3
---
*)

(*** hide ***)
#r "nuget: FCQRS, *"
open System
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Common
open FCQRS.Model.Data
open FCQRS.Actor

module User =
    type State = { Username: string option; Password: string option }
    type Command = Register of string * string | Login of string
    type Event = RegisterSucceeded of string * string | AlreadyRegistered | LoginSucceeded | LoginFailed
    let initialState = { Username = None; Password = None }
    let handleCommand (cmd: Command<Command>) state =
        match cmd.CommandDetails, state with
        | Register(u, p), { Username = None } -> RegisterSucceeded(u, p) |> PersistEvent
        | Register _, { Username = Some _ } -> AlreadyRegistered |> DeferEvent
        | Login p, { Username = Some _; Password = Some s } when p = s -> LoginSucceeded |> PersistEvent
        | Login _, _ -> LoginFailed |> DeferEvent
    let applyEvent (event: Event<Event>) state =
        match event.EventDetails with
        | RegisterSucceeded(u, p) -> { state with Username = Some u; Password = Some p }
        | _ -> state
    let init (actorApi: IActor) = actorApi.InitializeActor initialState "User" handleCommand applyEvent
    let factory actorApi entityId = (init actorApi).RefFor DEFAULT_SHARD entityId

(**
# 2. Wiring and running it

We have a [`User` aggregate](1-the-aggregate.html). Now we create the actor system, send it a command,
and read the result back — with no HOCON file.

## Create the actor system

You supply a database `Connection`; the framework's built-in defaults handle the rest, and an empty
`IConfiguration` is fine. (Details and overrides: [Configuration](../configuration.html).)
*)

let buildActorApi () : IActor =
    let config = ConfigurationBuilder().Build()
    let loggerFactory = LoggerFactory.Create(fun _ -> ())
    let connection =
        Some { ConnectionString = "Data Source=tutorial.db;" |> ValueLens.TryCreate |> Result.value
               DBType = DBType.Sqlite }
    let clusterName = "tutorial" |> ValueLens.TryCreate |> Result.value
    FCQRS.Actor.api config loggerFactory connection clusterName

(**
## A read side to listen on

A projection runs once per event. This minimal one forwards each `User` event to subscribers, which
is enough to let a caller know when its command has been processed. (Concept:
[the read side](../concepts/read-models.html).)
*)

let handleEventWrapper (_lf: ILoggerFactory) (_offset: int64) (event: obj) =
    match event with
    | :? Event<User.Event> as e -> [ e :> IMessageWithCID ]
    | _ -> []

(**
## Send a command, read your write

The key move is **subscribe to the correlation id before sending the command**. Then the confirmation
can't slip past, and once the wait returns the read side is current. (Concept:
[consistency and recovery](../concepts/consistency-and-recovery.html).)
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

        do! awaiter.Task |> Async.AwaitTask
        printfn "registered %A at version %A" event.EventDetails event.Version
    }

(**
Call `run () |> Async.RunSynchronously` from your entry point and you have a working CQRS loop. Next,
we react to that registration with a [saga](3-adding-a-saga.html).
*)
