module SagaHandshakeTests

open System
open System.Reflection
open System.Threading.Tasks
open Akka.Actor
open Akka.Cluster
open Akka.Cluster.Tools.PublishSubscribe
open Akkling
open Akkling.Cluster.Sharding
open Expecto
open FCQRS.Common

// Exercise the real coordinator without a journal or a blocking aggregate:
// malformed readiness must fail an assertion, never the process fail-fast policy.
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

let private checkMessage event (originator: Akka.Actor.IActorRef) cid =
    command ((findMethod commandType "NewCheckSagas").Invoke(null, [| event; originator; cid |]))

let private acknowledge mediator saga coordinators persisted acked =
    (findMethod starterModule "acknowledgeReady").Invoke(
        null, [| box mediator; box saga; box coordinators; box persisted; box acked |]) |> ignore

let private timeout = TimeSpan.FromSeconds 5.0
let private localConfig = Akka.Configuration.ConfigurationFactory.ParseString("akka.loglevel = WARNING")

let private receiver (mailbox: Actor<obj>) =
    let rec loop () = actor {
        let! message = mailbox.Receive()
        match message with
        | :? string as s when s = "barrier" -> mailbox.Sender() <! box ()
        | _ -> ()
        return! loop ()
    }
    loop ()

let private makeSaga (system: ActorSystem) (typeName: string) (entityId: string) =
    let created = TaskCompletionSource<IActorRef<obj>>(TaskCreationOptions.RunContinuationsAsynchronously)
    let region =
        spawn system (Uri.EscapeDataString typeName) (props (fun regionMailbox ->
            spawn regionMailbox "0" (props (fun shardMailbox ->
                let saga = spawn shardMailbox (Uri.EscapeDataString entityId) (props receiver)
                created.SetResult saga
                receiver shardMailbox)) |> ignore
            receiver regionMailbox))
    Expect.isTrue (created.Task.Wait timeout) "the fake shard created its entity"
    created.Task.Result, region

let private entityRef (system: ActorSystem) (typeName: string) (entityId: string) : IEntityRef<obj> =
    let t =
        (findType typeof<IEntityRef<obj>>.Assembly "Akkling.Cluster.Sharding.EntityRef`1")
            .MakeGenericType [| typeof<obj> |]
    Activator.CreateInstance(t, [| box system.DeadLetters; box typeName; box "0"; box entityId |])
    |> required "entity reference"
    :?> IEntityRef<obj>

let private startCoordinator (system: ActorSystem) (mediator: Akka.Actor.IActorRef) (factories: (string -> IEntityRef<obj>) list) : IActorRef<obj> =
    let rules (event: obj) =
        match event with
        | :? string as s when s = "barrier" -> []
        | _ -> [ for factory in factories -> factory, PrefixConversion None, box "start" ]
    (findMethod starterModule "init").Invoke(null, [| box system; box mediator; box rules |]) |> ignore
    system.ActorSelection("/user/SagaStarter").ResolveOne(timeout).Result |> typed

let private barrier (starter: IActorRef<obj>) originator =
    starter.Ask<obj>(checkMessage (box "barrier") originator "barrier", Some timeout)
    |> Async.RunSynchronously |> ignore

let private startBatch (starter: IActorRef<obj>) originator cid =
    starter.Ask<obj>(checkMessage (box "start") originator cid, Some timeout) |> Async.StartAsTask

let private terminate (system: ActorSystem) =
    system.Terminate().Wait(TimeSpan.FromSeconds 15.0) |> ignore

let private typeIdentityTest =
    testCase "saga handshake: duplicate readiness cannot satisfy a different saga type"
    <| fun _ ->
        let system = ActorSystem.Create("SagaTypeIdentity", localConfig)
        try
            let entityId = "order 42~Saga~same-cid"
            let fastType = "Fast Saga/100%"
            let fast, _ = makeSaga system fastType entityId
            let slow, _ = makeSaga system "SlowSaga" entityId
            let originator = spawn system "originator" (props receiver)
            let starter =
                startCoordinator system system.DeadLetters
                    [ entityRef system fastType; entityRef system "SlowSaga" ]
            let answer = startBatch starter (untyped originator) entityId
            barrier starter (untyped originator)
            starter.Tell(continueMessage, untyped fast)
            starter.Tell(continueMessage, untyped fast)
            barrier starter (untyped originator)
            Expect.isFalse (answer.Wait(TimeSpan.FromMilliseconds 100.0))
                "two replies from FastSaga leave SlowSaga pending"
            starter.Tell(continueMessage, untyped slow)
            Expect.isTrue (answer.Wait timeout) "SlowSaga's own readiness completes the batch"
        finally
            terminate system

let private duplicateRegistrationTest =
    testCase "saga handshake: one readiness satisfies duplicate registrations of the same saga"
    <| fun _ ->
        let system = ActorSystem.Create("SagaDuplicateRegistration", localConfig)
        try
            let entityId = "42~Saga~cid"
            let saga, _ = makeSaga system "SameSaga" entityId
            let originator = spawn system "originator" (props receiver)
            let factory = entityRef system "SameSaga"
            let starter = startCoordinator system system.DeadLetters [ factory; factory ]
            let answer = startBatch starter (untyped originator) entityId
            barrier starter (untyped originator)
            starter.Tell(continueMessage, untyped saga)
            Expect.isTrue (answer.Wait timeout) "one physical saga contributes one readiness requirement"
        finally
            terminate system

let private readinessBoundaryTest =
    testCase "saga handshake: readiness requires both journal persistence and subscription acknowledgement"
    <| fun _ ->
        let system = ActorSystem.Create("SagaReadinessBoundary", localConfig)
        try
            let entityId = "42~Saga~cid"
            let saga, _ = makeSaga system "ReadySaga" entityId
            let originator = spawn system "originator" (props receiver)
            let starter = startCoordinator system system.DeadLetters [ entityRef system "ReadySaga" ]
            let answer = startBatch starter (untyped originator) entityId
            barrier starter (untyped originator)
            let coordinators = [ untyped starter ]
            acknowledge system.DeadLetters (untyped saga) coordinators false true
            acknowledge system.DeadLetters (untyped saga) coordinators true false
            barrier starter (untyped originator)
            Expect.isFalse (answer.Wait(TimeSpan.FromMilliseconds 100.0))
                "neither an unjournaled saga nor an unacknowledged subscription is ready"
            acknowledge system.DeadLetters (untyped saga) coordinators true true
            Expect.isTrue (answer.Wait timeout) "readiness is released once both boundaries are satisfied"
        finally
            terminate system

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
            terminate system

let private crossNodeTest =
    testCase "saga handshake: readiness replies to the originating node's coordinator"
    <| fun _ ->
        let config = Akka.Configuration.ConfigurationFactory.ParseString("""
akka.actor.provider = cluster
akka.actor.serializers.stj = "FCQRS.ActorSerialization+STJSerializer, FCQRS"
akka.actor.serialization-bindings."FCQRS.Common+ISerializable, FCQRS" = stj
akka.remote.dot-netty.tcp.hostname = "127.0.0.1"
akka.remote.dot-netty.tcp.port = 0
akka.loglevel = WARNING
akka.cluster.gossip-interval = 100ms
akka.cluster.pub-sub.gossip-interval = 100ms
        """)
        let nodeA = ActorSystem.Create("SagaCoordinatorRouting", config)
        let nodeB = ActorSystem.Create("SagaCoordinatorRouting", config)
        try
            let clusterA, clusterB = Cluster.Get(nodeA), Cluster.Get(nodeB)
            clusterA.Join clusterA.SelfAddress
            clusterB.Join clusterA.SelfAddress
            let deadline = DateTime.UtcNow.AddSeconds 15.0
            let ready () =
                clusterA.State.Members |> Seq.filter (fun m -> m.Status = MemberStatus.Up) |> Seq.length = 2
            while not (ready ()) && DateTime.UtcNow < deadline do System.Threading.Thread.Sleep 50
            Expect.isTrue (ready ()) "both cluster nodes are Up"
            let mediatorA, mediatorB = DistributedPubSub.Get(nodeA).Mediator, DistributedPubSub.Get(nodeB).Mediator
            let entityId = "42~Saga~cross-node-cid"
            let saga, _ = makeSaga nodeB "RemoteSaga" entityId
            let originator = spawn nodeA "originator" (props receiver)
            let starterA = startCoordinator nodeA mediatorA [ entityRef nodeB "RemoteSaga" ]
            startCoordinator nodeB mediatorB [] |> ignore
            let answer = startBatch starterA (untyped originator) entityId
            barrier starterA (untyped originator)
            // Resolve through B to exercise a real RemoteActorRef and the
            // unchanged Continue wire payload, not a process-local object ref.
            let coordinatorOnA =
                nodeB.ActorSelection(string clusterA.SelfAddress + "/user/SagaStarter").ResolveOne(timeout).Result
            acknowledge mediatorB (untyped saga) [ coordinatorOnA ] true true
            Expect.isTrue (answer.Wait timeout)
                "the reply reaches A even though B has its own local SagaStarter"
        finally
            terminate nodeB
            terminate nodeA

let tests =
    testSequenced (testList "saga handshake"
        [ typeIdentityTest; duplicateRegistrationTest; readinessBoundaryTest; continuationFixtureTest; crossNodeTest ])
