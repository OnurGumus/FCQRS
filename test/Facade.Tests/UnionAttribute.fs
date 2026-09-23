namespace System.Runtime.CompilerServices

open System

/// Stands in for the .NET 11 attribute that marks a C# 15 union. FCQRS finds unions by this
/// attribute's full name, so a union-shaped F# type exercises the same code as a C# union.
[<AttributeUsage(AttributeTargets.Class ||| AttributeTargets.Struct, Inherited = false)>]
type UnionAttribute() =
    inherit Attribute()
