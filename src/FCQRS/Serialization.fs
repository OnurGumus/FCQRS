module FCQRS.ActorSerialization

open Akkling
open Akka.Actor
open Akka.Serialization
open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open FCQRS.Common
open FCQRS.Model.Data
open FCQRS.Serialization

/// Journal manifests.
///
/// Payload types registered with JournalTypes get structured, STABLE manifests:
///     fcqrs:ev(doc.event)                          Event<DocumentEvent>
///     fcqrs:agg-snap(doc.state)                    State<DocumentState>
///     fcqrs:saga-snap(quota.data,quota.state,doc.event)
/// The envelope tags are FCQRS-owned; the payload names are the user's contract.
/// Renaming or moving a CLR type then only means updating the JournalTypes
/// mapping — decade-old journal rows keep deserializing.
///
/// Anything else (unregistered payloads, pre-existing journals) uses the legacy
/// CLR type-name manifest, and the read side dispatches on the "fcqrs:" prefix —
/// so old journals never need migrating. New legacy manifests omit assembly
/// versions (see versionFree); rows written before that keep reading.
module internal Manifests =

    [<Literal>]
    let Prefix = "fcqrs:"

    /// The envelope generics FCQRS journals, each with a stable tag. Builder sagas wrap
    /// their state in SagaStateWrapper; "saga-wrap" gives their state rows and snapshots
    /// stable manifests too. FCQRS 6.7.0 reads it; earlier releases cannot.
    let private tagToDef, private defToTag =
        let tags =
            [ "ev", typedefof<Event<obj>>
              "cmd", typedefof<Command<obj>>
              "saga-ev", typedefof<SagaEvent<obj>>
              "saga-state", typedefof<SagaStateWithVersion<obj, obj>>
              "saga-snap", typedefof<FCQRS.Saga.SagaSnapshot<obj, obj, obj>>
              "saga-start", typedefof<FCQRS.Saga.SagaStartingEventWrapper<obj>>
              "agg-snap", typedefof<FCQRS.Actor.Internal.State<obj>>
              "cont", typedefof<ContinueOrAbort<obj>>
              "sse", typedefof<SagaStarter.SagaStartingEvent<obj>>
              "saga-wrap", typedefof<SagaBuilder.SagaStateWrapper<obj, obj>> ]

        dict tags, dict [ for tag, def in tags -> def, tag ]

    /// The CLR type name without assembly version, culture or public-key token.
    /// Each FCQRS release changes the FCQRS assembly version, and a node still running
    /// an older release cannot bind a newer one, so versioned names written by an
    /// upgraded node would crash older readers during a rolling deployment.
    /// Type.GetType resolves these names on every version, and still reads versioned
    /// names from earlier rows. An escaped comma inside a type name is not a separator.
    /// FCQRS.Serialization applies the same rule to union case keys (Library.fs, versionFree),
    /// and the two packages ship separately. Change both copies together.
    let versionFree (t: Type) =
        Regex.Replace(t.AssemblyQualifiedName |> Unchecked.nonNull, @"(?<!\\), (?:Version|Culture|PublicKeyToken)=[^,\]]*", "")

    /// Type -> "tag(arg,...)" / logical name; None if anything inside is unregistered.
    let rec tryEncode (t: Type) : string option =
        if t.IsGenericType then
            match defToTag.TryGetValue(t.GetGenericTypeDefinition()) with
            | true, tag ->
                let args = t.GetGenericArguments() |> Array.map tryEncode

                if args |> Array.forall Option.isSome then
                    Some(tag + "(" + String.Join(",", args |> Array.map Option.get) + ")")
                else
                    None
            | _ -> JournalTypes.TryGetName t
        else
            JournalTypes.TryGetName t

    /// Parse "tag(arg,...)" / logical name back to a Type. Throws on unknown
    /// names — an unresolvable journal manifest is a fatal configuration error.
    let resolve (manifest: string) : Type =
        let body = manifest.Substring(Prefix.Length)

        let rec parse (s: string) (pos: int) : Type * int =
            // read until a delimiter
            let mutable i = pos

            while i < s.Length && s[i] <> '(' && s[i] <> ')' && s[i] <> ',' do
                i <- i + 1

            let head = s.Substring(pos, i - pos)

            if i < s.Length && s[i] = '(' then
                match tagToDef.TryGetValue head with
                | false, _ -> failwithf "Unknown FCQRS manifest envelope tag '%s' in '%s'" head manifest
                | true, def ->
                    let mutable args = []
                    let mutable j = i + 1 // past '('

                    let mutable expectMore = s[j] <> ')'

                    while expectMore do
                        let argType, next = parse s j
                        args <- argType :: args
                        j <- next

                        if j < s.Length && s[j] = ',' then j <- j + 1
                        else expectMore <- false

                    if j >= s.Length || s[j] <> ')' then
                        failwithf "Malformed FCQRS manifest '%s'" manifest

                    def.MakeGenericType(args |> List.rev |> List.toArray), j + 1
            else
                match JournalTypes.TryGetType head with
                | Some t -> t, i
                | None ->
                    failwithf
                        "Journal manifest '%s' names '%s', which is not registered with JournalTypes — map it (or an alias) before recovery"
                        manifest
                        head

        let t, finalPos = parse body 0

        if finalPos <> body.Length then
            failwithf "Malformed FCQRS manifest '%s'" manifest

        t

type STJSerializer(system: ExtendedActorSystem) =
    inherit SerializerWithStringManifest(system)

    override __.Identifier = 1713

    // Aggregates and sagas are eternal; their journals must stay readable and
    // writable forever. A serialization failure in either direction would
    // otherwise surface as an Akka persistence failure that quietly STOPS the
    // entity while the process keeps reporting healthy — the exact opposite of
    // the fail-fast policy. Crash loudly instead; a deterministic crash loop on
    // a corrupt row is preferable to a silently dead aggregate.

    override __.ToBinary o =
        try
            JsonSerializer.SerializeToUtf8Bytes(o, o.GetType(), Serialization.jsonOptions)
        with ex ->
            let msg = sprintf "Serialization error for type '%s': %s" (o.GetType().FullName |> string) ex.Message
            system.Log.Log(Akka.Event.LogLevel.ErrorLevel, ex, msg)
            eprintfn "%s" msg
            fatalFailFast null "Process terminated due to serialization error" ex
            failwith "unreachable" // FailFast never returns; satisfies the compiler

    override _.Manifest(o: obj) : string =
        match o with
        | :? SagaStarter.Internal.Message ->
            // Readiness can reach a node still running the previous package.
            // Its existing Type.GetType reader accepts the CLR type and simple
            // assembly name, whereas a newer assembly version cannot bind to an
            // older loaded assembly. Keep this transient protocol version-free;
            // journal and public message manifests retain their existing rules.
            let typ = o.GetType()
            $"{typ.FullName}, {typ.Assembly.GetName().Name}"
        | _ ->
            match Manifests.tryEncode (o.GetType()) with
            | Some encoded -> Manifests.Prefix + encoded
            | None ->
                // Legacy manifest for unregistered types — readable forever via the
                // fallback below, like the versioned names in pre-existing journal rows.
                Manifests.versionFree (o.GetType())

    override _.FromBinary(bytes: byte[], manifest: string) : obj =
        try
            let typ =
                if manifest.StartsWith Manifests.Prefix then
                    Manifests.resolve manifest
                else
                    match Type.GetType manifest with
                    | null -> failwithf "Failed to resolve type from manifest: %s" manifest
                    | t -> t

            JsonSerializer.Deserialize(bytes, typ, Serialization.jsonOptions) |> Unchecked.nonNull
        with ex ->
            let preview =
                if bytes.Length <= 200 then System.Text.Encoding.UTF8.GetString(bytes)
                else System.Text.Encoding.UTF8.GetString(bytes, 0, 200) + "..."
            let msg =  sprintf "Deserialization error for manifest '%s': %s\nPayload preview: %s" manifest ex.Message preview
            system.Log.Log(Akka.Event.LogLevel.ErrorLevel, ex, msg)
            eprintfn "%s" msg
            fatalFailFast null "Process terminated due to deserialization error" ex
            failwith "unreachable" // FailFast never returns; satisfies the compiler

/// Serializes the transient expected-version protocol separately from persisted messages.
/// A node without this serializer rejects the transport message instead of passing an
/// unknown wrapper type to the journal serializer. Install readers on every node before
/// sending conditional commands during a rolling deployment.
type ConditionalCommandSerializer(system: ExtendedActorSystem) =
    inherit SerializerWithStringManifest(system)

    let commandManifest = "fcqrs:conditional-command:1"
    let conflictManifest = "fcqrs:conditional-conflict:1"
    let utf8 = UTF8Encoding(false, true)

    let invalidData message = raise (InvalidDataException message)

    let validateVersion (version: int64) =
        if version < 0L then invalidData "A conditional command version must be nonnegative."

    let validateCommand (command: obj) =
        if isNull (box command) then invalidData "A conditional command must contain a command envelope."
        let commandType = command.GetType()
        if not commandType.IsGenericType || commandType.GetGenericTypeDefinition() <> typedefof<Command<_>> then
            invalidData "A conditional command must contain a Command envelope."

    let writeBytes (writer: BinaryWriter) (bytes: byte array) =
        writer.Write(bytes.Length)
        writer.Write(bytes)

    let writeText (writer: BinaryWriter) (value: string) =
        if isNull (box value) then invalidData "A conditional command field cannot be null."
        writeBytes writer (utf8.GetBytes value)

    let readBytes (reader: BinaryReader) =
        let length = reader.ReadInt32()
        if length < 0 || int64 length > reader.BaseStream.Length - reader.BaseStream.Position then
            invalidData "A conditional command contains an invalid field length."
        reader.ReadBytes length

    let readText (reader: BinaryReader) = utf8.GetString(readBytes reader)

    let requireEnd (reader: BinaryReader) =
        if reader.BaseStream.Position <> reader.BaseStream.Length then
            invalidData "A conditional command contains trailing bytes."

    let messageId (value: string) : MessageId =
        match ValueLens.CreateAsResult value with
        | Ok value -> value
        | Error _ -> invalidData "A conditional conflict contains an invalid command ID."

    let correlationId (value: string) : CID =
        match ValueLens.CreateAsResult value with
        | Ok value -> value
        | Error _ -> invalidData "A conditional conflict contains an invalid correlation ID."

    override _.Identifier = 1714

    override _.Manifest(value: obj) =
        match value with
        | :? ConditionalCommand -> commandManifest
        | :? ConditionalCommandConflict -> conflictManifest
        | _ -> invalidArg (nameof value) "The conditional command serializer only accepts its protocol messages."

    override _.ToBinary(value: obj) =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream, utf8, true)
        match value with
        | :? ConditionalCommand as command ->
            validateVersion command.ExpectedVersion
            validateCommand command.Command
            if isNull (box command.ReplyTo) then invalidData "A conditional command requires a reply actor."
            let serializer = system.Serialization.FindSerializerFor command.Command
            writer.Write(command.ExpectedVersion)
            writer.Write(serializer.Identifier)
            writeText writer (Akka.Serialization.Serialization.ManifestFor(serializer, command.Command))
            writeBytes writer (serializer.ToBinary command.Command)
            writeText writer (Akka.Serialization.Serialization.SerializedActorPath command.ReplyTo)
        | :? ConditionalCommandConflict as conflict ->
            validateVersion conflict.ExpectedVersion
            validateVersion conflict.ActualVersion
            if isNull (box conflict.CommandId) || not conflict.CommandId.IsValid then
                invalidData "A conditional conflict requires a valid command ID."
            if isNull (box conflict.CorrelationId) || not conflict.CorrelationId.IsValid then
                invalidData "A conditional conflict requires a valid correlation ID."
            if String.IsNullOrWhiteSpace conflict.AggregateId then
                invalidData "A conditional conflict requires an aggregate ID."
            writer.Write(conflict.ExpectedVersion)
            writer.Write(conflict.ActualVersion)
            writeText writer (conflict.CommandId.ToString())
            writeText writer (conflict.CorrelationId.ToString())
            writeText writer conflict.AggregateId
        | _ -> invalidArg (nameof value) "The conditional command serializer only accepts its protocol messages."
        writer.Flush()
        stream.ToArray()

    override _.FromBinary(bytes: byte array, manifest: string) : obj =
        if isNull (box bytes) then nullArg (nameof bytes)
        use stream = new MemoryStream(bytes, false)
        use reader = new BinaryReader(stream, utf8, true)
        match manifest with
        | value when value = commandManifest ->
            let expectedVersion = reader.ReadInt64()
            validateVersion expectedVersion
            let serializerId = reader.ReadInt32()
            if serializerId = 1714 then invalidData "Conditional command wrappers cannot be nested."
            let nestedManifest = readText reader
            let nestedBytes = readBytes reader
            let replyPath = readText reader
            if String.IsNullOrWhiteSpace replyPath then invalidData "A conditional command requires a reply actor path."
            requireEnd reader
            let command =
                match system.Serialization.Deserialize(nestedBytes, serializerId, nestedManifest) with
                | null -> invalidData "A conditional command must contain a command envelope."
                | command -> command
            validateCommand command
            box
                { ConditionalCommand.ExpectedVersion = expectedVersion
                  Command = command
                  ReplyTo = system.Provider.ResolveActorRef replyPath }
            |> Unchecked.nonNull
        | value when value = conflictManifest ->
            let expectedVersion = reader.ReadInt64()
            let actualVersion = reader.ReadInt64()
            validateVersion expectedVersion
            validateVersion actualVersion
            let commandId = readText reader |> messageId
            let cid = readText reader |> correlationId
            let aggregateId = readText reader
            if String.IsNullOrWhiteSpace aggregateId then invalidData "A conditional conflict requires an aggregate ID."
            requireEnd reader
            box
                { ConditionalCommandConflict.ExpectedVersion = expectedVersion
                  ActualVersion = actualVersion
                  CommandId = commandId
                  CorrelationId = cid
                  AggregateId = aggregateId }
            |> Unchecked.nonNull
        | _ -> invalidData "Unsupported conditional command protocol manifest."

/// Carries a command to the node that hosts its aggregate or saga. Akkling wraps every message sent
/// through an entity reference in a `ShardEnvelope`, which has no serializer of its own. The default
/// JSON serializer cannot rebuild FCQRS's validated values, so the receiving node dropped the command.
/// This serializer writes the envelope's IDs and serializes the message with the serializer bound to it.
type ShardEnvelopeSerializer(system: ExtendedActorSystem) =
    inherit SerializerWithStringManifest(system)

    let envelopeManifest = "fcqrs:shard-envelope:1"
    let utf8 = UTF8Encoding(false, true)

    let invalidData message = raise (InvalidDataException message)

    let writeBytes (writer: BinaryWriter) (bytes: byte array) =
        writer.Write(bytes.Length)
        writer.Write(bytes)

    let writeText (writer: BinaryWriter) (value: string) =
        if isNull (box value) then invalidData "A shard envelope field cannot be null."
        writeBytes writer (utf8.GetBytes value)

    let readBytes (reader: BinaryReader) =
        let length = reader.ReadInt32()
        if length < 0 || int64 length > reader.BaseStream.Length - reader.BaseStream.Position then
            invalidData "A shard envelope contains an invalid field length."
        reader.ReadBytes length

    let readText (reader: BinaryReader) = utf8.GetString(readBytes reader)

    override _.Identifier = 1715

    override _.Manifest(value: obj) =
        match value with
        | :? Akkling.Cluster.Sharding.ShardEnvelope -> envelopeManifest
        | _ -> invalidArg (nameof value) "The shard envelope serializer only accepts Akkling shard envelopes."

    override _.ToBinary(value: obj) =
        match value with
        | :? Akkling.Cluster.Sharding.ShardEnvelope as envelope ->
            if isNull envelope.Message then invalidData "A shard envelope must contain a message."
            use stream = new MemoryStream()
            use writer = new BinaryWriter(stream, utf8, true)
            let serializer = system.Serialization.FindSerializerFor envelope.Message
            writeText writer envelope.ShardId
            writeText writer envelope.EntityId
            writer.Write(serializer.Identifier)
            writeText writer (Akka.Serialization.Serialization.ManifestFor(serializer, envelope.Message))
            writeBytes writer (serializer.ToBinary envelope.Message)
            writer.Flush()
            stream.ToArray()
        | _ -> invalidArg (nameof value) "The shard envelope serializer only accepts Akkling shard envelopes."

    override _.FromBinary(bytes: byte array, manifest: string) : obj =
        if isNull (box bytes) then nullArg (nameof bytes)
        if manifest <> envelopeManifest then invalidData "Unsupported shard envelope manifest."
        use stream = new MemoryStream(bytes, false)
        use reader = new BinaryReader(stream, utf8, true)
        let shardId = readText reader
        let entityId = readText reader
        let serializerId = reader.ReadInt32()
        let messageManifest = readText reader
        let messageBytes = readBytes reader
        if stream.Position <> stream.Length then invalidData "A shard envelope contains trailing bytes."
        match system.Serialization.Deserialize(messageBytes, serializerId, messageManifest) with
        | null -> invalidData "A shard envelope must contain a message."
        | message ->
            box
                ({ ShardId = shardId
                   EntityId = entityId
                   Message = message }: Akkling.Cluster.Sharding.ShardEnvelope)
