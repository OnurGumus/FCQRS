module SagaHandshakeTests

open System
open System.Collections.Concurrent
open System.IO
open System.Reflection
open System.Threading.Tasks
open Akka.Actor
open Akka.Cluster
open Akkling
open Akkling.Cluster.Sharding
open Expecto
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging.Abstractions
open FCQRS.Common
open FCQRS.FSharp

// An aggregate waits for the sagas an event starts before storing that event. These tests
// drive a real aggregate against fake saga shards, so readiness can be withheld, repeated,
// or sent from a chosen saga identity.
let private flags = BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static
let private required what (value: 'T | null) : 'T =
    match value with
    | null -> failwithf "Missing %s" what
    | value -> value

let private assembly = typeof<IActor>.Assembly
let private findType (source: Assembly) name = source.GetType(name) |> required name
let private findMethod (source: Type) name = source.GetMethod(name, flags) |> required name
let private starterModule = findType assembly "FCQRS.Common+SagaStarter+Internal"
let private commandType = findType assembly "FCQRS.Common+SagaStarter+Internal+Command"
let private messageType = findType assembly "FCQRS.Common+SagaStarter+Internal+Message"

let private command value =
    (findMethod messageType "NewCommand").Invoke(null, [| value |]) |> required "command wrapper"

let private continueMessage =
    command ((findMethod commandType "get_Continue").Invoke(null, [||]))

let private acknowledge saga coordinators persisted acked =
    (findMethod starterModule "acknowledgeReady").Invoke(
        null, [| box saga; box coordinators; box persisted; box acked |]) |> ignore

let private timeout = TimeSpan.FromSeconds 10.0
let private localConfig = Akka.Configuration.ConfigurationFactory.ParseString("akka.loglevel = WARNING")

let private receiver (mailbox: Actor<obj>) =
    let rec loop () = actor {
        let! _ = mailbox.Receive()
        return! loop ()
    }
    loop ()

/// A fake saga at <type>/0/<entity>, the path shape of a sharded entity.
let private makeSaga (system: ActorSystem) (typeName: string) (entityId: string) =
    let created = TaskCompletionSource<IActorRef<obj>>(TaskCreationOptions.RunContinuationsAsynchronously)
    spawn system (Uri.EscapeDataString typeName) (props (fun regionMailbox ->
        spawn regionMailbox "0" (props (fun shardMailbox ->
            let saga = spawn shardMailbox (Uri.EscapeDataString entityId) (props receiver)
            created.SetResult saga
            receiver shardMailbox)) |> ignore
        receiver regionMailbox)) |> ignore
    Expect.isTrue (created.Task.Wait timeout) "the fake shard created its entity"
    created.Task.Result

/// Records each starting message sent to a fake saga shard, with its sender.
let private recordingRegion (system: ActorSystem) (starts: ConcurrentQueue<obj * Akka.Actor.IActorRef>) =
    spawn system "recorded-saga-starts" (props (fun (mailbox: Actor<obj>) ->
        let rec loop () = actor {
            let! message = mailbox.Receive()
            match message with
            | :? ShardEnvelope -> starts.Enqueue((message, untyped (mailbox.Sender())))
            | _ -> ()
            return! loop ()
        }
        loop ()))

let private entityRef (region: Akka.Actor.IActorRef) (typeName: string) (entityId: string) : IEntityRef<obj> =
    let t =
        (findType typeof<IEntityRef<obj>>.Assembly "Akkling.Cluster.Sharding.EntityRef`1")
            .MakeGenericType [| typeof<obj> |]
    Activator.CreateInstance(t, [| box region; box typeName; box "0"; box entityId |])
    |> required "entity reference"
    :?> IEntityRef<obj>

let private waitFor (condition: unit -> bool) what =
    let deadline = DateTime.UtcNow + timeout
    while not (condition ()) && DateTime.UtcNow < deadline do
        Threading.Thread.Sleep 20
    Expect.isTrue (condition ()) what

let private boot name =
    let db = Path.Combine(Path.GetTempPath(), $"fcqrs_handshake_{Guid.NewGuid():N}.db")
    let configuration =
        VerifySerialization.configuration()
            .AddInMemoryCollection(
                [ Collections.Generic.KeyValuePair<string, string | null>("config:akka:fcqrs:saga-start-timeout", "10") ])
            .Build()
    Fcqrs.actor configuration NullLoggerFactory.Instance
        (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={db};")) name

type OriginCommand = Begin
type OriginEvent = Begun

/// An aggregate whose every event starts the given fake sagas. Returns the aggregate and
/// the recorded starting messages.
let private originator (api: IActor) (sagas: (string * string) list) =
    let starts = ConcurrentQueue<obj * Akka.Actor.IActorRef>()
    let region = recordingRegion api.System starts
    let origins =
        Fcqrs.aggregate api
            { Name = "HandshakeOrigin"
              Initial = 0
              Decide = fun (_: Command<OriginCommand>) _ -> PersistEvent Begun
              Fold = fun (_: Event<OriginEvent>) state -> state + 1
              Snapshots = NoSnapshots
              Passivation = PassivationPolicy.Default }
    api.InitializeSagaStarter(fun (_: obj) ->
        [ for typeName, entityId in sagas -> (fun (_: string) -> entityRef (untyped region) typeName entityId), PrefixConversion None, Unchecked.nonNull (box "start") ])
    origins, starts

let private begin' (origins: AggregateHandle<OriginCommand, OriginEvent>) =
    origins.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "origin") Begin (fun _ -> true) |> Async.StartAsTask

let private typeIdentityTest =
    testCase "saga handshake: duplicate readiness cannot satisfy a different saga type"
    <| fun _ ->
        let api = boot "SagaTypeIdentity"
        try
            let entityId = "order 42~Saga~same-cid"
            let fastType = "Fast Saga/100%"
            let fast = makeSaga api.System fastType entityId
            let slow = makeSaga api.System "SlowSaga" entityId
            let origins, starts = originator api [ fastType, entityId; "SlowSaga", entityId ]
            let reply = begin' origins
            waitFor (fun () -> starts.Count >= 2) "the aggregate sent both starting messages"
            let aggregate = snd (Seq.head starts)
            aggregate.Tell(continueMessage, untyped fast)
            aggregate.Tell(continueMessage, untyped fast)
            Expect.isFalse (reply.Wait(TimeSpan.FromMilliseconds 300.0))
                "two replies from FastSaga leave SlowSaga pending, so the event is not stored"
            aggregate.Tell(continueMessage, untyped slow)
            Expect.isTrue (reply.Wait timeout) "SlowSaga's own readiness lets the aggregate store the event"
        finally
            api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let private duplicateRegistrationTest =
    testCase "saga handshake: one readiness satisfies duplicate registrations of the same saga"
    <| fun _ ->
        let api = boot "SagaDuplicateRegistration"
        try
            let entityId = "42~Saga~cid"
            let saga = makeSaga api.System "SameSaga" entityId
            let origins, starts = originator api [ "SameSaga", entityId; "SameSaga", entityId ]
            let reply = begin' origins
            waitFor (fun () -> starts.Count >= 1) "the aggregate sent the starting message"
            (snd (Seq.head starts)).Tell(continueMessage, untyped saga)
            Expect.isTrue (reply.Wait timeout) "one physical saga contributes one readiness requirement"
        finally
            api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let private readinessBoundaryTest =
    testCase "saga handshake: readiness requires both journal persistence and subscription acknowledgement"
    <| fun _ ->
        let api = boot "SagaReadinessBoundary"
        try
            let entityId = "42~Saga~cid"
            let saga = makeSaga api.System "ReadySaga" entityId
            let origins, starts = originator api [ "ReadySaga", entityId ]
            let reply = begin' origins
            waitFor (fun () -> starts.Count >= 1) "the aggregate sent the starting message"
            let coordinators = [ snd (Seq.head starts) ]
            acknowledge (untyped saga) coordinators false true
            acknowledge (untyped saga) coordinators true false
            Expect.isFalse (reply.Wait(TimeSpan.FromMilliseconds 300.0))
                "neither an unjournaled saga nor an unacknowledged subscription is ready"
            acknowledge (untyped saga) coordinators true true
            Expect.isTrue (reply.Wait timeout) "readiness is released once both boundaries are satisfied"
        finally
            api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let private resendTest =
    testCase "saga handshake: a saga that missed its starting message receives it again"
    <| fun _ ->
        let api = boot "SagaStartResend"
        try
            let entityId = "42~Saga~resent"
            let saga = makeSaga api.System "RestartedSaga" entityId
            let origins, starts = originator api [ "RestartedSaga", entityId ]
            let reply = begin' origins
            // The first starting message goes unanswered, as when the saga restarts
            // before replying and loses the aggregate's reference.
            waitFor (fun () -> starts.Count >= 2) "the aggregate sent the starting message again"
            (snd (Seq.head starts)).Tell(continueMessage, untyped saga)
            Expect.isTrue (reply.Wait timeout) "the answer to the repeated message completes the handshake"
        finally
            api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

type LedgerCommand =
    | Deposit
    | Start
    | OpenLedger

type LedgerEvent =
    | Deposited
    | Started
    | Opened

let private userStashTest =
    testCase "saga handshake: a command the user stashed stays stashed through a handshake"
    <| fun _ ->
        let api = boot "SagaStartUserStash"
        try
            let entityId = "ledger~Saga~stash"
            let saga = makeSaga api.System "LedgerSaga" entityId
            let starts = ConcurrentQueue<obj * Akka.Actor.IActorRef>()
            let region = recordingRegion api.System starts
            let ledgers =
                Fcqrs.aggregate api
                    { Name = "StashingLedger"
                      Initial = false
                      Decide =
                        fun (command: Command<LedgerCommand>) isOpen ->
                            match command.CommandDetails with
                            // A deposit waits until the ledger opens.
                            | Deposit when not isOpen -> Stash IgnoreEvent
                            | Deposit -> PersistEvent Deposited
                            | Start -> PersistEvent Started
                            | OpenLedger -> UnstashAll(PersistEvent Opened)
                      Fold =
                        fun (event: Event<LedgerEvent>) isOpen ->
                            // Starting opens the ledger too, so a deposit released early by
                            // the handshake would be stored at once.
                            match event.EventDetails with
                            | Opened
                            | Started -> true
                            | Deposited -> isOpen
                      Snapshots = NoSnapshots
                      Passivation = PassivationPolicy.Default }
            api.InitializeSagaStarter(fun (event: obj) ->
                match event with
                | :? Event<LedgerEvent> as event when event.EventDetails = Started ->
                    [ (fun (_: string) -> entityRef (untyped region) "LedgerSaga" entityId), PrefixConversion None, Unchecked.nonNull (box "start") ]
                | _ -> [])
            let send command =
                ledgers.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "ledger") command (fun _ -> true) |> Async.StartAsTask

            let deposit = send Deposit
            Expect.isFalse (deposit.Wait(TimeSpan.FromMilliseconds 300.0)) "the closed ledger stashed the deposit"
            let start = send Start
            waitFor (fun () -> starts.Count >= 1) "the ledger is waiting for its saga"
            (snd (Seq.head starts)).Tell(continueMessage, untyped saga)
            Expect.isTrue (start.Wait timeout) "the ledger stored Started once the saga was ready"
            Expect.isFalse (deposit.Wait(TimeSpan.FromMilliseconds 500.0))
                "the handshake released only what it stashed itself; the deposit is still stashed"
            let opened = send OpenLedger
            Expect.isTrue (opened.Wait timeout) "the ledger opened"
            Expect.isTrue (deposit.Wait timeout) "opening the ledger released the deposit"
            Expect.equal deposit.Result.EventDetails Deposited "the released deposit was stored"
        finally
            api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

type RelayCommand =
    | Ping of target: string
    | Pong

type RelayEvent =
    | Pinged of target: string
    | Ponged

type PingState =
    | AwaitingPong of target: string
    | Answered

type PongState = Seen

let private senderTest =
    testCase "saga handshake: an event that starts a saga still reaches the saga that commanded it"
    <| fun _ ->
        let api = boot "SagaStartSender"
        try
            let answered = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            let pongStarted = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            let relays =
                Fcqrs.aggregate api
                    { Name = "Relay"
                      Initial = 0
                      Decide =
                        fun (command: Command<RelayCommand>) _ ->
                            match command.CommandDetails with
                            | Ping target -> PersistEvent(Pinged target)
                            | Pong -> PersistEvent Ponged
                      Fold = fun (_: Event<RelayEvent>) state -> state + 1
                      Snapshots = NoSnapshots
                      Passivation = PassivationPolicy.Default }
            // The ping saga asks another relay for a Pong and waits for its Ponged, which that
            // relay sends back to the saga that commanded it.
            let pings =
                Fcqrs.saga api
                    { Name = "PingSaga"
                      InitialData = ()
                      Originator = relays.Factory
                      HandleEvent =
                        fun message saga ->
                            match message, saga.State with
                            | (:? Event<RelayEvent> as event), None ->
                                match event.EventDetails with
                                | Pinged target -> StateChangedEvent(AwaitingPong target)
                                | Ponged -> UnhandledEvent
                            | (:? Event<RelayEvent> as event), Some(AwaitingPong _) when event.EventDetails = Ponged ->
                                StateChangedEvent Answered
                            | _ -> UnhandledEvent
                      ApplySideEffects =
                        fun saga _ ->
                            match saga.State with
                            | AwaitingPong target -> Stay, [ toAggregate relays.Factory target Pong ]
                            | Answered ->
                                answered.TrySetResult() |> ignore
                                StopSaga, []
                      StartOn = fun (event: Event<RelayEvent>) -> match event.EventDetails with Pinged _ -> true | Ponged -> false
                      Snapshots = NoSnapshots }
            // Ponged starts a saga too, so the target relay stores it only after a handshake.
            let pongs =
                Fcqrs.saga api
                    { Name = "PongSaga"
                      InitialData = ()
                      Originator = relays.Factory
                      HandleEvent =
                        fun message saga ->
                            match message, saga.State with
                            | :? Event<RelayEvent>, None -> StateChangedEvent Seen
                            | _ -> UnhandledEvent
                      ApplySideEffects =
                        fun _ _ ->
                            pongStarted.TrySetResult() |> ignore
                            StopSaga, []
                      StartOn = fun (event: Event<RelayEvent>) -> event.EventDetails = Ponged
                      Snapshots = NoSnapshots }
            Fcqrs.wireSagaStarters api [ pings; pongs ]
            relays.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "x") (Ping "y") (fun _ -> true)
            |> Async.RunSynchronously
            |> ignore
            Expect.isTrue (pongStarted.Task.Wait timeout) "Ponged started its own saga"
            Expect.isTrue (answered.Task.Wait timeout) "the ping saga received the Ponged it asked for"
        finally
            api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let private continuationFixtureTest =
    testCase "saga handshake: Continue retains the published 6.3.0 reader shape"
    <| fun _ ->
        let system = ActorSystem.Create("SagaContinuationFixture", localConfig)
        try
            let serializer = FCQRS.ActorSerialization.STJSerializer(system :?> ExtendedActorSystem)
            let expected = """{"Case":"Command","Item":{"Case":"Continue"}}"""
            let bytes = serializer.ToBinary(continueMessage)
            Expect.equal (Text.Encoding.UTF8.GetString bytes) expected
                "the readiness payload has the existing union cases and fields"
            let manifest = serializer.Manifest(continueMessage)
            Expect.equal manifest "FCQRS.Common+SagaStarter+Internal+Message+Command, FCQRS"
                "existing readers resolve the message without requiring the sender's newer assembly version"
            let fixture = Text.Encoding.UTF8.GetBytes(expected)
            Expect.equal (serializer.FromBinary(fixture, manifest)) continueMessage
                "the current reader accepts the unchanged published-reader fixture"
            let legacyManifest =
                "FCQRS.Common+SagaStarter+Internal+Message+Command, FCQRS, Version=6.3.0.0, Culture=neutral, PublicKeyToken=null"
            Expect.equal (serializer.FromBinary(fixture, legacyManifest)) continueMessage
                "the current reader also accepts the previous version-qualified manifest"
        finally
            system.Terminate().Wait(TimeSpan.FromSeconds 15.0) |> ignore

let private crossNodeTest =
    testCase "saga handshake: readiness reaches an aggregate on another node"
    <| fun _ ->
        let config = Akka.Configuration.ConfigurationFactory.ParseString("""
akka.actor.provider = cluster
akka.actor.serializers.stj = "FCQRS.ActorSerialization+STJSerializer, FCQRS"
akka.actor.serialization-bindings."FCQRS.Common+ISerializable, FCQRS" = stj
akka.remote.dot-netty.tcp.hostname = "127.0.0.1"
akka.remote.dot-netty.tcp.port = 0
akka.loglevel = WARNING
akka.cluster.gossip-interval = 100ms
        """)
        let nodeA = ActorSystem.Create("SagaReadinessRouting", config)
        let nodeB = ActorSystem.Create("SagaReadinessRouting", config)
        try
            let clusterA, clusterB = Cluster.Get(nodeA), Cluster.Get(nodeB)
            clusterA.Join clusterA.SelfAddress
            clusterB.Join clusterA.SelfAddress
            waitFor
                (fun () -> clusterA.State.Members |> Seq.filter (fun m -> m.Status = MemberStatus.Up) |> Seq.length = 2)
                "both cluster nodes are Up"
            let saga = makeSaga nodeB "RemoteSaga" "42~Saga~cross-node-cid"
            // Stands in for the waiting aggregate on node A.
            let received = TaskCompletionSource<obj>(TaskCreationOptions.RunContinuationsAsynchronously)
            spawn nodeA "waiting-origin" (props (fun (mailbox: Actor<obj>) ->
                let rec loop () = actor {
                    let! message = mailbox.Receive()
                    match message with
                    | :? Akkling.Actors.LifecycleEvent -> ()
                    | message -> received.TrySetResult message |> ignore
                    return! loop ()
                }
                loop ())) |> ignore
            // Resolve through B to exercise a real RemoteActorRef and the unchanged
            // Continue wire payload, not a process-local object ref.
            let origin = nodeB.ActorSelection(string clusterA.SelfAddress + "/user/waiting-origin").ResolveOne(timeout).Result
            acknowledge (untyped saga) [ origin ] true true
            Expect.isTrue (received.Task.Wait timeout) "the readiness reply crossed to node A"
            Expect.equal received.Task.Result continueMessage "node A read the readiness message"
        finally
            nodeB.Terminate().Wait(TimeSpan.FromSeconds 15.0) |> ignore
            nodeA.Terminate().Wait(TimeSpan.FromSeconds 15.0) |> ignore

let tests =
    testSequenced (testList "saga handshake"
        [ typeIdentityTest
          duplicateRegistrationTest
          readinessBoundaryTest
          resendTest
          userStashTest
          senderTest
          continuationFixtureTest
          crossNodeTest ])
