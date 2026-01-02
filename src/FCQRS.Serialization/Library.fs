namespace FCQRS.Serialization

/// <summary>
/// Provides JSON serialization and deserialization functionality for FCQRS.
/// </summary>
module public Serialization =

    open System
    open System.Text.Json
    open System.Text.Json.Serialization

    /// <summary>
    /// Default JSON serializer options configured for F# types and C# polymorphic types.
    /// </summary>
    let public jsonOptions =
        let opts =
            JsonFSharpOptions
                .Default()
                .WithSkippableOptionFields()
                .WithUnionInternalTag()
                .WithUnionNamedFields()
                //.WithUnionFieldNamesFromTypes()
                .WithUnionUnwrapSingleCaseUnions(false)
                .ToJsonSerializerOptions()
        // Enable C# polymorphic serialization with [JsonDerivedType] attributes
        opts.RespectNullableAnnotations <- true
        opts

    /// <summary>
    /// Serializes an object to a JSON string.
    /// </summary>
    /// <param name="obj">The object to serialize.</param>
    /// <returns>A JSON string representation of the object.</returns>
    let public encode obj = JsonSerializer.Serialize(obj, jsonOptions)

    /// <summary>
    /// Deserializes a JSON element to a strongly-typed object.
    /// </summary>
    /// <param name="json">The JSON element to deserialize.</param>
    /// <typeparam name="'T">The type to deserialize to.</typeparam>
    /// <returns>The deserialized object.</returns>
    /// <exception cref="System.InvalidOperationException">Thrown when the deserialized value is null.</exception>
    let public decode<'T> (json: JsonElement) =
        match JsonSerializer.Deserialize<'T>(json, jsonOptions) with
        | null -> invalidOp "null values not supported on deserialization"
        | value -> value

    /// <summary>
    /// Serializes an object to a UTF-8 byte array.
    /// </summary>
    /// <param name="obj">The object to serialize.</param>
    /// <returns>A byte array containing the JSON representation of the object.</returns>
    let public encodeToBytes obj = JsonSerializer.SerializeToUtf8Bytes(obj, jsonOptions)

    /// <summary>
    /// Deserializes a byte array to a strongly-typed object.
    /// </summary>
    /// <param name="json">The byte array containing JSON data.</param>
    /// <typeparam name="'T">The type to deserialize to.</typeparam>
    /// <returns>The deserialized object.</returns>
    /// <exception cref="System.InvalidOperationException">Thrown when the deserialized value is null.</exception>
    let public decodeFromBytes<'T> (json: byte array) =
        match JsonSerializer.Deserialize<'T>(json, jsonOptions) with
        | null -> invalidOp "null values not supported on deserialization"
        | value -> value