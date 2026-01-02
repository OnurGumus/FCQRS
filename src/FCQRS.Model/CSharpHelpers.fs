/// C# interoperability helpers for FCQRS.Model
/// Provides simpler APIs for consuming FCQRS types from C#
module FCQRS.Model.CSharp

open System
open System.Runtime.CompilerServices
open FCQRS.Model.Data

/// C#-friendly factory methods for FSharpResult
type Results =
    /// Create a successful result
    static member Ok<'T, 'E>(value: 'T) : Result<'T, 'E> =
        Ok value

    /// Create an error result
    static member Error<'T, 'E>(error: 'E) : Result<'T, 'E> =
        Error error

/// C#-friendly string type creation with simple error handling
type StringTypes =
    /// Try to create a ShortString using out parameter pattern
    static member TryCreateShortString(s: string, [<System.Runtime.InteropServices.Out>] result: byref<ShortString>) : bool =
        match ValueLens.TryCreate<ShortString, _, _> s with
        | Ok v -> result <- v; true
        | Error _ -> false

    /// Try to create a LongString using out parameter pattern
    static member TryCreateLongString(s: string, [<System.Runtime.InteropServices.Out>] result: byref<LongString>) : bool =
        match ValueLens.TryCreate<LongString, _, _> s with
        | Ok v -> result <- v; true
        | Error _ -> false

/// Helper methods for creating FCQRS.Model types from C#
type Helpers =
    /// Create a ShortString from a string (throws on failure)
    static member CreateShortString(s: string) : ShortString =
        match ValueLens.TryCreate<ShortString, _, _> s with
        | Ok v -> v
        | Error e -> failwithf "Failed to create ShortString: %A" e

    /// Try to create a ShortString (returns Result instead of throwing)
    static member TryCreateShortString(s: string) : Result<ShortString, ModelError list> =
        ValueLens.TryCreate<ShortString, _, _> s

    /// Create a LongString from a string (throws on failure)
    static member CreateLongString(s: string) : LongString =
        match ValueLens.TryCreate<LongString, _, _> s with
        | Ok v -> v
        | Error e -> failwithf "Failed to create LongString: %A" e

    /// Try to create a LongString (returns Result instead of throwing)
    static member TryCreateLongString(s: string) : Result<LongString, ModelError list> =
        ValueLens.TryCreate<LongString, _, _> s
