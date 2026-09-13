module ConditionalCommandSerializationTests

open System
open System.IO
open System.Reflection
open System.Threading.Tasks
open Akka.Actor
open Akka.Configuration
open Akka.Serialization
open Expecto
open FCQRS.Common
open FCQRS.FSharp
open FCQRS.Model.Data

let private boxed value : obj = box value |> Unchecked.nonNull

let private wireType name =
    match typeof<Command<string>>.Assembly.GetType("FCQRS.Common+" + name, true) with
    | null -> failwithf "The conditional command protocol type %s was not found." name
    | value -> value

let private wire name (fields: obj array) =
    match Activator.CreateInstance(wireType name, BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic, null, fields, null) with
    | null -> failwithf "The conditional command protocol type %s could not be constructed." name
    | value -> value

let private field<'T> name (value: obj) =
    let property =
        match value.GetType().GetProperty(name, BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance) with
        | null -> failwithf "The conditional command protocol field %s was not found." name
        | property -> property
    match property.GetValue value with
    | null -> failwithf "The conditional command protocol field %s was null." name
    | value -> unbox<'T> value

let private productionConfig () =
    use resource =
        match typeof<Command<string>>.Assembly.GetManifestResourceStream("FCQRS.default.hocon") with
        | null -> failwith "The FCQRS default configuration was not found."
        | stream -> stream
    use reader = new StreamReader(resource)
    let defaults = ConfigurationFactory.ParseString(reader.ReadToEnd()).GetConfig("config")
    ConfigurationFactory.ParseString("akka.actor.provider = local\nakka.extensions = []\nakka.loglevel = OFF\nakka.stdout-loglevel = OFF")
        .WithFallback(defaults)

let private withSystem run =
    use system = ActorSystem.Create("ConditionalSerialization" + Guid.NewGuid().ToString("N"), productionConfig ())
    try run (system :?> ExtendedActorSystem)
    finally system.Terminate().WaitAsync(TimeSpan.FromSeconds 10.0).GetAwaiter().GetResult() |> ignore

let private command () : Command<string> =
    { CommandDetails = "disable-user"
      CreationDate = DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc)
      Id = Guid.CreateVersion7().ToString() |> ValueLens.CreateAsResult |> Result.value
      Sender = Some(Fcqrs.aggregateId "sender")
      CorrelationId = Fcqrs.newCid ()
      Metadata = Map.ofList [ "audit", "conditional-round-trip" ] }

let private boundSerializer (system: ExtendedActorSystem) message =
    match system.Serialization.FindSerializerFor(message) with
    | :? SerializerWithStringManifest as serializer -> serializer
    | _ -> failwith "The conditional protocol was not bound to a string-manifest serializer."

let private roundTripCommand =
    testCase "conditional wire: production bindings preserve command identity and reply actor"
    <| fun _ ->
        withSystem <| fun system ->
            let command = command ()
            let original = wire "ConditionalCommand" [| boxed 7L; boxed command; boxed system.DeadLetters |]
            Expect.isFalse (original :? ISerializable) "transient wrappers do not enter the fail-fast journal serializer"
            let serializer = boundSerializer system original
            Expect.equal serializer.Identifier 1714 "the wrapper has its own serializer ID"
            Expect.equal (system.Serialization.FindSerializerFor(command).Identifier) 1713 "the existing command keeps its journal serializer"
            let manifest = serializer.Manifest original
            Expect.equal manifest "fcqrs:conditional-command:1" "the protocol has a fixed versioned manifest"
            let decoded = serializer.FromBinary(serializer.ToBinary original, manifest)
            Expect.equal (field<int64> "ExpectedVersion" decoded) 7L "the expected version survives serialization"
            Expect.equal (field<Command<string>> "Command" decoded) command "the nested command, ID, CID and metadata are unchanged"
            let reply = field<IActorRef> "ReplyTo" decoded
            Expect.equal reply system.DeadLetters "the reply actor path resolves in the receiving system"

let private roundTripConflict =
    testCase "conditional wire: conflicts preserve the rejected request identity"
    <| fun _ ->
        withSystem <| fun system ->
            let command = command ()
            let original =
                wire "ConditionalCommandConflict"
                    [| boxed 7L; boxed 9L; boxed command.Id; boxed command.CorrelationId; boxed "user/with spaces" |]
            Expect.isFalse (original :? ISerializable) "conflicts also avoid the journal serializer"
            let serializer = boundSerializer system original
            Expect.equal serializer.Identifier 1714 "conflicts use the dedicated protocol serializer"
            let manifest = serializer.Manifest original
            Expect.equal manifest "fcqrs:conditional-conflict:1" "conflicts have their own fixed manifest"
            let decoded = serializer.FromBinary(serializer.ToBinary original, manifest)
            Expect.equal (field<int64> "ExpectedVersion" decoded) 7L "the requested version survives"
            Expect.equal (field<int64> "ActualVersion" decoded) 9L "the observed version survives"
            Expect.equal (field<MessageId> "CommandId" decoded) command.Id "a conflict belongs to one command"
            Expect.equal (field<CID> "CorrelationId" decoded) command.CorrelationId "the correlation ID survives"
            Expect.equal (field<string> "AggregateId" decoded) "user/with spaces" "the target ID survives"

let private malformedMessages =
    testCase "conditional wire: malformed outer messages fail without entering the journal decoder"
    <| fun _ ->
        withSystem <| fun system ->
            let command = command ()
            let original = wire "ConditionalCommand" [| boxed 7L; boxed command; boxed system.DeadLetters |]
            let serializer = boundSerializer system original
            let manifest = serializer.Manifest original
            let bytes = serializer.ToBinary original
            let invalidLength = Array.copy bytes
            // expected-version (8 bytes), nested serializer ID (4), manifest length (4).
            Array.Copy(BitConverter.GetBytes(Int32.MaxValue), 0, invalidLength, 12, 4)
            let negativeVersion = Array.copy bytes
            Array.Copy(BitConverter.GetBytes(-1L), 0, negativeVersion, 0, 8)
            for description, payload, wireManifest in
                [ "unknown protocol version", bytes, "fcqrs:conditional-command:2"
                  "truncated header", bytes.[0..5], manifest
                  "impossible field length", invalidLength, manifest
                  "negative expected version", negativeVersion, manifest
                  "trailing bytes", Array.append bytes [| 0uy |], manifest ] do
                Expect.throws (fun () -> serializer.FromBinary(payload, wireManifest) |> ignore) description
            let invalidNested = wire "ConditionalCommand" [| boxed 7L; boxed "not a command"; boxed system.DeadLetters |]
            Expect.throwsT<InvalidDataException>
                (fun () -> serializer.ToBinary invalidNested |> ignore)
                "arbitrary objects cannot be wrapped as conditional commands"

let private legacyNodeRejects =
    testCase "conditional wire: a node without the serializer rejects the transport message"
    <| fun _ ->
        withSystem <| fun system ->
            let command = command ()
            let original = wire "ConditionalCommand" [| boxed 7L; boxed command; boxed system.DeadLetters |]
            let serializer = boundSerializer system original
            let bytes = serializer.ToBinary original
            let manifest = serializer.Manifest original
            // This system has the existing FCQRS journal serializer, but no conditional binding.
            let legacyConfig = ConfigurationFactory.ParseString("""
                akka.loglevel = OFF
                akka.stdout-loglevel = OFF
                akka.actor.serializers.stj = "FCQRS.ActorSerialization+STJSerializer, FCQRS"
                akka.actor.serialization-bindings {
                    "FCQRS.Common+ISerializable, FCQRS" = stj
                }
                """)
            use legacy = ActorSystem.Create("ConditionalLegacy" + Guid.NewGuid().ToString("N"), legacyConfig)
            try
                let serialization = (legacy :?> ExtendedActorSystem).Serialization
                Expect.equal (serialization.FindSerializerFor(command).Identifier) 1713 "the legacy journal serializer is installed"
                Expect.throws
                    (fun () -> serialization.Deserialize(bytes, 1714, manifest) |> ignore)
                    "an unrecognized serializer ID rejects the guarded message instead of unwrapping or fail-fast decoding it"
                Expect.isFalse legacy.WhenTerminated.IsCompleted "rejecting an unknown protocol does not terminate the actor system"
            finally legacy.Terminate().WaitAsync(TimeSpan.FromSeconds 10.0).GetAwaiter().GetResult() |> ignore

type RemoteConditionalReplyCapture(eventReply: TaskCompletionSource<obj * IActorRef>, conflictReply: TaskCompletionSource<obj * IActorRef>) =
    inherit UntypedActor()
    override this.OnReceive(message: obj) =
        if message.GetType() = wireType "ConditionalCommandConflict" then
            conflictReply.TrySetResult((message, this.Sender)) |> ignore
        else
            eventReply.TrySetResult((message, this.Sender)) |> ignore

type RemoteConditionalReceiver() =
    inherit UntypedActor()
    override this.OnReceive(message: obj) =
        let request = field<Command<string>> "Command" message
        let version = field<int64> "ExpectedVersion" message
        let replyTo = field<IActorRef> "ReplyTo" message
        // Exercise the ordinary event serializer and the conflict serializer over
        // the same transported ReplyTo, preserving the publishing actor as sender.
        let reply: Event<string> =
            { EventDetails = request.CommandDetails
              CreationDate = request.CreationDate
              Id = request.Id
              Sender = Some(Fcqrs.aggregateId "remote-entity")
              CorrelationId = request.CorrelationId
              Version = ValueLens.TryCreate(version + 1L) |> Result.value
              Metadata = request.Metadata }
        replyTo.Tell(reply, this.Self)
        replyTo.Tell(
            wire "ConditionalCommandConflict"
                [| boxed version; boxed (version + 1L); boxed request.Id; boxed request.CorrelationId; boxed "remote-entity" |],
            this.Self)

let private remoteRoundTrip =
    testCase "conditional wire: remote reply paths preserve event and conflict identities and senders"
    <| fun _ ->
        let config =
            ConfigurationFactory.ParseString("""
                akka.actor.provider = remote
                akka.remote.dot-netty.tcp {
                    hostname = "127.0.0.1"
                    public-hostname = "127.0.0.1"
                    port = 0
                }
                """).WithFallback(productionConfig ())
        use caller = ActorSystem.Create("ConditionalCaller" + Guid.NewGuid().ToString("N"), config)
        use receiver = ActorSystem.Create("ConditionalReceiver" + Guid.NewGuid().ToString("N"), config)
        try
            let eventReply = TaskCompletionSource<obj * IActorRef>(TaskCreationOptions.RunContinuationsAsynchronously)
            let conflictReply = TaskCompletionSource<obj * IActorRef>(TaskCreationOptions.RunContinuationsAsynchronously)
            let replyTo =
                caller.ActorOf(Props.Create(typeof<RemoteConditionalReplyCapture>, [| boxed eventReply; boxed conflictReply |]), "reply")
            let target = receiver.ActorOf(Props.Create(typeof<RemoteConditionalReceiver>), "remote-entity")
            let targetPath = target.Path.ToStringWithAddress((receiver :?> ExtendedActorSystem).Provider.DefaultAddress)
            let request = command ()
            let wrapper = wire "ConditionalCommand" [| boxed 3L; boxed request; boxed replyTo |]
            caller.ActorSelection(targetPath).Tell(wrapper, replyTo)
            let event, eventSender = eventReply.Task.WaitAsync(TimeSpan.FromSeconds 15.0).GetAwaiter().GetResult()
            let conflict, conflictSender = conflictReply.Task.WaitAsync(TimeSpan.FromSeconds 15.0).GetAwaiter().GetResult()
            let event = event :?> Event<string>
            Expect.equal event.Id request.Id "the remote event retains the original command ID"
            Expect.equal event.CorrelationId request.CorrelationId "the remote event retains the correlation ID"
            Expect.equal event.EventDetails request.CommandDetails "the nested command payload reaches the other system"
            Expect.equal (field<MessageId> "CommandId" conflict) request.Id "the remote conflict retains the command ID"
            Expect.equal (field<CID> "CorrelationId" conflict) request.CorrelationId "the remote conflict retains the correlation ID"
            Expect.equal (field<int64> "ActualVersion" conflict) 4L "the remote conflict retains the actual version"
            Expect.equal (eventSender.Path.ToString()) targetPath "remote success replies preserve the publishing actor"
            Expect.equal (conflictSender.Path.ToString()) targetPath "remote conflicts preserve the rejecting actor"
        finally
            caller.Terminate().WaitAsync(TimeSpan.FromSeconds 15.0).GetAwaiter().GetResult() |> ignore
            receiver.Terminate().WaitAsync(TimeSpan.FromSeconds 15.0).GetAwaiter().GetResult() |> ignore

let tests =
    testList "conditional serialization"
        [ roundTripCommand; roundTripConflict; malformedMessages; legacyNodeRejects; remoteRoundTrip ]
