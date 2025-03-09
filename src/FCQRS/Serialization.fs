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

    override __.ToBinary o =
        JsonSerializer.SerializeToUtf8Bytes(o, jsonOptions)

    override _.Manifest(o: obj) : string = o.GetType().AssemblyQualifiedName |> Unchecked.nonNull

    override _.FromBinary(bytes: byte[], manifest: string) : obj =
        JsonSerializer.Deserialize(bytes, Type.GetType manifest |> Unchecked.nonNull, jsonOptions) |> Unchecked.nonNull
