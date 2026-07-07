module FCQRS.ActorSerialization

open Akkling
open Akka.Actor
open Akka.Serialization
open System
open System.Text.Json
open FCQRS.Common
open FCQRS.Serialization

/// Journal manifests.
///
/// Payload types registered with JournalTypes get structured, STABLE manifests:
///     fcqrs:ev(doc.event)                          Event<DocumentEvent>
///     fcqrs:agg-snap(doc.state)                    State<DocumentState>
///     fcqrs:saga-snap(quota.data,quota.state,doc.event)
/// The envelope tags are FCQRS-owned; the payload names are the user's contract.
/// Renaming or moving a CLR type then only means updating the JournalTypes
/// mapping — decade-old journal rows keep deserializing.
///
/// Anything else (unregistered payloads, pre-existing journals) uses the legacy
/// CLR AssemblyQualifiedName manifest, and the read side dispatches on the
/// "fcqrs:" prefix — so old journals never need migrating.
module internal Manifests =

    [<Literal>]
    let Prefix = "fcqrs:"

    /// The envelope generics FCQRS journals, each with a stable tag.
    let private tagToDef, private defToTag =
        let pairs =
            [ "ev", typedefof<Event<obj>>
              "cmd", typedefof<Command<obj>>
              "saga-ev", typedefof<SagaEvent<obj>>
              "saga-state", typedefof<SagaStateWithVersion<obj, obj>>
              "saga-snap", typedefof<FCQRS.Saga.SagaSnapshot<obj, obj, obj>>
              "saga-start", typedefof<FCQRS.Saga.SagaStartingEventWrapper<obj>>
              "agg-snap", typedefof<FCQRS.Actor.Internal.State<obj>>
              "cont", typedefof<ContinueOrAbort<obj>>
              "sse", typedefof<SagaStarter.SagaStartingEvent<obj>> ]

        dict pairs, dict [ for tag, def in pairs -> def, tag ]

    /// Type -> "tag(arg,...)" / logical name; None if anything inside is unregistered.
    let rec tryEncode (t: Type) : string option =
        if t.IsGenericType then
            match defToTag.TryGetValue(t.GetGenericTypeDefinition()) with
            | true, tag ->
                let args = t.GetGenericArguments() |> Array.map tryEncode

                if args |> Array.forall Option.isSome then
                    Some(tag + "(" + String.Join(",", args |> Array.map Option.get) + ")")
                else
                    None
            | _ -> JournalTypes.TryGetName t
        else
            JournalTypes.TryGetName t

    /// Parse "tag(arg,...)" / logical name back to a Type. Throws on unknown
    /// names — an unresolvable journal manifest is a fatal configuration error.
    let resolve (manifest: string) : Type =
        let body = manifest.Substring(Prefix.Length)

        let rec parse (s: string) (pos: int) : Type * int =
            // read until a delimiter
            let mutable i = pos

            while i < s.Length && s[i] <> '(' && s[i] <> ')' && s[i] <> ',' do
                i <- i + 1

            let head = s.Substring(pos, i - pos)

            if i < s.Length && s[i] = '(' then
                match tagToDef.TryGetValue head with
                | false, _ -> failwithf "Unknown FCQRS manifest envelope tag '%s' in '%s'" head manifest
                | true, def ->
                    let mutable args = []
                    let mutable j = i + 1 // past '('

                    let mutable expectMore = s[j] <> ')'

                    while expectMore do
                        let argType, next = parse s j
                        args <- argType :: args
                        j <- next

                        if j < s.Length && s[j] = ',' then j <- j + 1
                        else expectMore <- false

                    if j >= s.Length || s[j] <> ')' then
                        failwithf "Malformed FCQRS manifest '%s'" manifest

                    def.MakeGenericType(args |> List.rev |> List.toArray), j + 1
            else
                match JournalTypes.TryGetType head with
                | Some t -> t, i
                | None ->
                    failwithf
                        "Journal manifest '%s' names '%s', which is not registered with JournalTypes — map it (or an alias) before recovery"
                        manifest
                        head

        let t, finalPos = parse body 0

        if finalPos <> body.Length then
            failwithf "Malformed FCQRS manifest '%s'" manifest

        t

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
            fatalFailFast null "Process terminated due to serialization error" ex
            failwith "unreachable" // FailFast never returns; satisfies the compiler

    override _.Manifest(o: obj) : string =
        match Manifests.tryEncode (o.GetType()) with
        | Some encoded -> Manifests.Prefix + encoded
        | None ->
            // Legacy manifest for unregistered types (also what every pre-existing
            // journal row carries) — readable forever via the fallback below.
            o.GetType().AssemblyQualifiedName |> Unchecked.nonNull

    override _.FromBinary(bytes: byte[], manifest: string) : obj =
        try
            let typ =
                if manifest.StartsWith Manifests.Prefix then
                    Manifests.resolve manifest
                else
                    match Type.GetType manifest with
                    | null -> failwithf "Failed to resolve type from manifest: %s" manifest
                    | t -> t

            JsonSerializer.Deserialize(bytes, typ, Serialization.jsonOptions) |> Unchecked.nonNull
        with ex ->
            let preview =
                if bytes.Length <= 200 then System.Text.Encoding.UTF8.GetString(bytes)
                else System.Text.Encoding.UTF8.GetString(bytes, 0, 200) + "..."
            let msg =  sprintf "Deserialization error for manifest '%s': %s\nPayload preview: %s" manifest ex.Message preview
            system.Log.Log(Akka.Event.LogLevel.ErrorLevel, ex, msg)
            eprintfn "%s" msg
            fatalFailFast null "Process terminated due to deserialization error" ex
            failwith "unreachable" // FailFast never returns; satisfies the compiler
