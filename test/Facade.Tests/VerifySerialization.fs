module VerifySerialization

// Every actor system in these tests serializes each message it sends, as if the message crossed to
// another node. A message another node could not read then fails the suite here, before a cluster
// ever runs it.

open System
open System.Collections.Concurrent
open System.Collections.Generic
open Akka.Actor
open Akka.Serialization
open Expecto
open Microsoft.Extensions.Configuration

/// Printed for each failing message type, so a child process can report failures to its parent.
[<Literal>]
let FailurePrefix = "serialization-check failed: "

/// Replaces the `System.Object` binding, which decides how a message without its own serializer
/// crosses nodes. A message from FCQRS, Akkling or the tests must survive that JSON serializer. Akka's
/// own internal messages, such as Akka.Persistence.Sql's, never leave their node and would fail the
/// check, so they pass through unchanged. A failure is recorded and the original message delivered,
/// so the test that sent it still runs and the last test reports every failing type at once.
type Serializer(system: ExtendedActorSystem) =
    inherit Akka.Serialization.Serializer(system)

    static let parked = ConcurrentDictionary<Guid, obj>()
    static let failures = ConcurrentDictionary<string, string>()
    static let mutable checkedCount = 0L
    let json = NewtonSoftJsonSerializer(system)

    let internalToAkka (message: obj) =
        let assembly = message.GetType().Assembly.GetName().Name |> Unchecked.nonNull
        assembly = "Akka" || assembly.StartsWith "Akka."

    let park (message: obj) =
        let id = Guid.NewGuid()
        parked[id] <- message
        Array.append [| 0uy |] (id.ToByteArray())

    /// Message types that did not survive the round trip, with the reason.
    static member Failures = failures :> IReadOnlyDictionary<string, string>

    /// How many messages went through the round trip.
    static member Checked = Threading.Interlocked.Read &checkedCount

    override _.Identifier = 1_900_001
    override _.IncludeManifest = true

    override _.ToBinary(message: obj) =
        if internalToAkka message then
            park message
        else
            Threading.Interlocked.Increment &checkedCount |> ignore
            let bytes, failure =
                try
                    let bytes = json.ToBinary message
                    match json.FromBinary(bytes, message.GetType()) with
                    | null -> bytes, Some "it came back as null"
                    | copy when copy.GetType() <> message.GetType() -> bytes, Some $"it came back as {copy.GetType()}"
                    | _ -> bytes, None
                with error ->
                    [||], Some(error.Message.Split('\n')[0])
            match failure with
            | Some reason ->
                if failures.TryAdd(string (message.GetType()), reason) then
                    Console.WriteLine $"{FailurePrefix}{message.GetType()}: {reason}"
                park message
            | None -> Array.append [| 1uy |] bytes

    override _.FromBinary(bytes: byte[], messageType: Type) =
        if bytes[0] = 0uy then
            match parked.TryRemove(Guid(ReadOnlySpan(bytes, 1, 16))) with
            | true, message -> message
            | _ -> invalidOp "A parked message was read twice."
        else
            json.FromBinary(bytes[1..], messageType)

/// The settings that turn the check on.
let settings =
    [ KeyValuePair<string, string | null>("config:akka:actor:serialize-messages", "on")
      KeyValuePair<string, string | null>("config:akka:actor:serializers:verify", "VerifySerialization+Serializer, Facade.Tests")
      KeyValuePair<string, string | null>("config:akka:actor:serialization-bindings:System.Object", "verify") ]

/// A configuration builder with the check on. Add a test's own settings after it.
let configuration () : IConfigurationBuilder =
    ConfigurationBuilder().AddInMemoryCollection settings

/// Runs last: every message the earlier tests sent would have reached another node.
let tests =
    testCase "serialization: every message the tests sent survives the trip to another node"
    <| fun _ ->
        Expect.isGreaterThan Serializer.Checked 0L "the tests sent messages through the check"
        let failed = [ for KeyValue(messageType, reason) in Serializer.Failures -> $"{messageType}: {reason}" ]
        Expect.isEmpty failed "no message type failed the round trip"
