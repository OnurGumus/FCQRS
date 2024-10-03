module CQRS.Serialization

open CQRS
open Akkling
open Akka.Actor
open Akka.Serialization
open System
open System.Text.Json
open System.Text.Json.Serialization

let jsonOptions =
    JsonFSharpOptions
        .Default()
        .WithSkippableOptionFields()
        .WithUnionInternalTag()
        .WithUnionNamedFields()
        .WithUnionFieldNamesFromTypes()
        .WithUnionUnwrapSingleCaseUnions(false)
        .ToJsonSerializerOptions()

let encode obj = JsonSerializer.Serialize(obj, jsonOptions)

let decode<'T> (json: string) =
            JsonSerializer.Deserialize<'T>(json, jsonOptions)
        
open System.IO

type STJSerializer(system: ExtendedActorSystem) =
    inherit SerializerWithStringManifest(system)
    do ()


    override __.Identifier = 1713

    override __.ToBinary(o) =

        let memoryStream = new MemoryStream()
        JsonSerializer.Serialize(memoryStream, o, jsonOptions)
        memoryStream.ToArray()

    override _.Manifest(o: obj) : string = o.GetType().AssemblyQualifiedName

    override _.FromBinary(bytes: byte[], manifest: string) : obj =
        JsonSerializer.Deserialize(new MemoryStream(bytes), Type.GetType(manifest), jsonOptions)
