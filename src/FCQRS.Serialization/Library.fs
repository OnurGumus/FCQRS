namespace FCQRS.Serialization

/// <summary>
/// Provides JSON serialization and deserialization functionality for FCQRS.
/// </summary>
module public Serialization =

    open System
    open System.Collections.Generic
    open System.Reflection
    open System.Text.Json
    open System.Text.Json.Serialization

    // -------------------------------------------------------------------------
    // C# 15 discriminated union ("union") interop
    // -------------------------------------------------------------------------
    // The C# 15 `union` keyword compiles a declaration like
    //     public union Pet(Cat, Dog);
    // to a struct (or class) marked [System.Runtime.CompilerServices.Union]
    // that exposes one single-parameter constructor per case plus a public
    // `Value : obj` property holding the active case.
    //
    // System.Text.Json cannot round-trip that shape on its own: it emits no
    // case discriminator and has no way to pick a constructor on read (a
    // round-trip silently yields `default`). This converter teaches the FCQRS
    // serializer that shape, writing { "$case": <case type>, "$value": <payload> }.
    //
    // Union types are detected purely by reflection (attribute name), so this
    // assembly keeps targeting net9.0/net10.0 without referencing the net11
    // union BCL types.

    [<Literal>]
    let private UnionAttributeName = "System.Runtime.CompilerServices.UnionAttribute"

    let private typeKey (t: Type) =
        match t.FullName with
        | null -> t.Name
        | name -> name

    /// True when <paramref name="t"/> is a C# 15 discriminated union.
    let private isUnionType (t: Type) =
        t.GetCustomAttributes(false)
        |> Array.exists (fun a -> a.GetType().FullName = UnionAttributeName)

    let rec private inheritanceDepth (t: Type) =
        match t.BaseType with
        | null -> 0
        | baseType -> 1 + inheritanceDepth baseType

    /// Reflective description of a C# union: its `Value` accessor and the
    /// single-parameter constructor for each declared case.
    type private UnionShape =
        { ValueProperty: PropertyInfo
          /// (caseType, ctor) pairs, most-derived first — used on the write side
          /// to map a runtime case value back to its declared case type.
          CasesByDepth: (Type * ConstructorInfo) array
          /// case constructors keyed by case type key — used on the read side.
          CtorByCase: Dictionary<string, ConstructorInfo> }

    let private describeUnion (t: Type) : UnionShape =
        let valueProperty =
            match t.GetProperty("Value", BindingFlags.Public ||| BindingFlags.Instance) with
            | null -> failwithf "C# union type '%s' has no public 'Value' property" (typeKey t)
            | prop -> prop
        let cases =
            t.GetConstructors()
            |> Array.choose (fun ctor ->
                match ctor.GetParameters() with
                | [| p |] -> Some(p.ParameterType, ctor)
                | _ -> None)
        let ctorByCase = Dictionary<string, ConstructorInfo>()
        for caseType, ctor in cases do
            ctorByCase[typeKey caseType] <- ctor
        { ValueProperty = valueProperty
          CasesByDepth = cases |> Array.sortByDescending (fst >> inheritanceDepth)
          CtorByCase = ctorByCase }

    /// System.Text.Json converter for a single C# 15 union type.
    type UnionConverter<'T>() =
        inherit JsonConverter<'T>()
        let shape = describeUnion typeof<'T>

        // A union whose `Value` is null is serialized as JSON null, so the
        // converter must be handed null tokens on read.
        override _.HandleNull = true

        override _.Write(writer, value, options) =
            match shape.ValueProperty.GetValue(box value) with
            | null -> writer.WriteNullValue()
            | caseValue ->
                let caseType =
                    shape.CasesByDepth
                    |> Array.tryPick (fun (caseType, _) ->
                        if caseType.IsInstanceOfType caseValue then Some caseType else None)
                    |> Option.defaultValue (caseValue.GetType())
                writer.WriteStartObject()
                writer.WriteString("$case", typeKey caseType)
                writer.WritePropertyName("$value")
                JsonSerializer.Serialize(writer, caseValue, caseType, options)
                writer.WriteEndObject()

        override _.Read(reader, typeToConvert, options) =
            if reader.TokenType = JsonTokenType.Null then
                Unchecked.defaultof<'T>
            else
                use doc = JsonDocument.ParseValue(&reader)
                let root = doc.RootElement
                let caseName =
                    match root.TryGetProperty("$case") with
                    | true, prop -> prop.GetString()
                    | _ -> null
                match caseName with
                | null ->
                    failwithf "C# union JSON for '%s' is missing the '$case' discriminator" (typeKey typeToConvert)
                | name ->
                    match shape.CtorByCase.TryGetValue name with
                    | false, _ -> failwithf "C# union '%s' has no case '%s'" (typeKey typeToConvert) name
                    | true, ctor ->
                        let caseType = ctor.GetParameters().[0].ParameterType
                        let payload =
                            match root.TryGetProperty("$value") with
                            | true, prop -> JsonSerializer.Deserialize(prop.GetRawText(), caseType, options)
                            | _ -> null
                        Unchecked.unbox<'T> (ctor.Invoke [| payload |])

    /// Factory that supplies <see cref="T:FCQRS.Serialization.Serialization.UnionConverter`1"/>
    /// for any C# 15 discriminated union type.
    type UnionConverterFactory() =
        inherit JsonConverterFactory()
        override _.CanConvert(typeToConvert) = isUnionType typeToConvert
        override _.CreateConverter(typeToConvert, _options) =
            let converterType = typedefof<UnionConverter<_>>.MakeGenericType(typeToConvert)
            Activator.CreateInstance converterType |> Unchecked.nonNull :?> JsonConverter

    /// <summary>
    /// Default JSON serializer options configured for F# types, C# polymorphic
    /// types, and C# 15 discriminated unions.
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
        // Enable C# 15 `union` round-tripping. Inserted ahead of the F#
        // converter so unions win converter resolution unambiguously.
        opts.Converters.Insert(0, UnionConverterFactory())
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
