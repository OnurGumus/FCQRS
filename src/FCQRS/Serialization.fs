module FCQRS.ActorSerialization

open Akkling
open Akka.Actor
open Akka.Serialization
open System
open System.Text.Json
open FCQRS.Serialization

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
            Environment.FailFast("Process terminated due to serialization error", ex)
            failwith "unreachable" // FailFast never returns; satisfies the compiler

    override _.Manifest(o: obj) : string = o.GetType().AssemblyQualifiedName |> Unchecked.nonNull

    override _.FromBinary(bytes: byte[], manifest: string) : obj =
        try
            let typ = Type.GetType manifest
            if isNull typ then
                failwithf "Failed to resolve type from manifest: %s" manifest
            JsonSerializer.Deserialize(bytes, Unchecked.nonNull typ, Serialization.jsonOptions) |> Unchecked.nonNull
        with ex ->
            let preview =
                if bytes.Length <= 200 then System.Text.Encoding.UTF8.GetString(bytes)
                else System.Text.Encoding.UTF8.GetString(bytes, 0, 200) + "..."
            let msg =  sprintf "Deserialization error for manifest '%s': %s\nPayload preview: %s" manifest ex.Message preview
            system.Log.Log(Akka.Event.LogLevel.ErrorLevel, ex, msg)
            eprintfn "%s" msg
            Environment.FailFast("Process terminated due to deserialization error", ex)
            failwith "unreachable" // FailFast never returns; satisfies the compiler
