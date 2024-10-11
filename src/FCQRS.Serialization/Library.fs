module FCQRS.Serialization


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
        
let encodeToBytes obj = JsonSerializer.SerializeToUtf8Bytes(obj, jsonOptions)

let decodeFromBytes<'T> (json: byte array) =
            JsonSerializer.Deserialize<'T>(json, jsonOptions)