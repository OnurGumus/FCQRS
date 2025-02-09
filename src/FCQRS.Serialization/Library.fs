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
        //.WithUnionFieldNamesFromTypes()
        .WithUnionUnwrapSingleCaseUnions(false)
        .ToJsonSerializerOptions()

let encode obj = JsonSerializer.Serialize(obj, jsonOptions)



let decode<'T> (json: JsonElement) =
            match JsonSerializer.Deserialize<'T>(json, jsonOptions) with
            | null -> invalidOp "null values not supported on deserialization"
            | value ->  value
        
            
let encodeToBytes obj = JsonSerializer.SerializeToUtf8Bytes(obj, jsonOptions)

let decodeFromBytes<'T> (json: byte array) =
    match JsonSerializer.Deserialize<'T>(json, jsonOptions) with
    | null -> invalidOp "null values not supported on deserialization"
    | value ->  value