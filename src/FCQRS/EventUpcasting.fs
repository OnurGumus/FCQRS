/// Conversions of historical event payloads at FCQRS journal-read boundaries.
module FCQRS.EventUpcasting

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Threading
open Akka.Actor
open FCQRS.Common

module internal Internal =
    type private Step =
        { Target: Type
          Convert: obj -> obj }

    type private Pipeline =
        { Target: Type
          Convert: obj -> obj }

    /// Startup-only registrations, scoped to one actor system. After freezing,
    /// the dictionaries and compiled pipelines are immutable and safe to share.
    type Registry() =
        let gate = obj ()
        let steps = Dictionary<Type, Step>()
        let pipelines = Dictionary<Type, Pipeline>()
        let mutable frozen = false

        member private _.Add(source: Type, step: Step) =
            lock gate (fun () ->
                if frozen then
                    invalidOp "Register all event upcasters before initializing any aggregate, saga, or projection."
                if source = step.Target then
                    invalidArg "convert" "An event upcaster requires distinct source and target payload types."
                if steps.ContainsKey source then
                    invalidOp $"An event upcaster is already registered for '{source.FullName}'. Only one conversion per source type is allowed."

                let mutable current = step.Target
                let mutable atEnd = false
                while not atEnd do
                    if current = source then
                        invalidOp $"The event upcaster from '{source.FullName}' to '{step.Target.FullName}' would create a cycle."
                    match steps.TryGetValue current with
                    | true, next -> current <- next.Target
                    | false, _ -> atEnd <- true
                steps.Add(source, step))

        member this.Register<'Old, 'New when 'Old: not null and 'New: not null>(convert: Func<'Old, 'New>) =
            if isNull (box convert) then nullArg (nameof convert)
            let convertEnvelope (value: obj) =
                let original = unbox<Event<'Old>> value
                let payload =
                    try
                        if isNull (box original.EventDetails) then
                            invalidOp "The historical event payload is null."
                        let converted = convert.Invoke original.EventDetails
                        if isNull (box converted) then
                            invalidOp "An event upcaster returned null."
                        converted
                    with ex ->
                        raise (InvalidOperationException(
                            $"Event upcaster from '{typeof<'Old>.FullName}' to '{typeof<'New>.FullName}' failed.", ex))
                box
                    { EventDetails = payload
                      CreationDate = original.CreationDate
                      Id = original.Id
                      Sender = original.Sender
                      CorrelationId = original.CorrelationId
                      Version = original.Version
                      Metadata = original.Metadata }
                |> Unchecked.nonNull
            this.Add(typeof<'Old>, { Target = typeof<'New>; Convert = convertEnvelope })

        member _.Freeze() =
            lock gate (fun () ->
                if not frozen then
                    for source in steps.Keys do
                        let path = ResizeArray<Step>()
                        let mutable current = source
                        let mutable atEnd = false
                        while not atEnd do
                            match steps.TryGetValue current with
                            | true, step ->
                                path.Add step
                                current <- step.Target
                            | false, _ -> atEnd <- true
                        let path = path.ToArray()
                        pipelines.Add(source,
                            { Target = current
                              Convert = fun value -> Array.fold (fun value (step: Step) -> step.Convert value) value path })
                    Volatile.Write(&frozen, true))

        member this.CopyTo(target: Registry) =
            // A builder can create more than one service provider, but each
            // runtime gets its own immutable registry and registration lifetime.
            this.Freeze()
            for pair in steps do
                target.Add(pair.Key, pair.Value)

        member this.TargetType(source: Type) =
            if not (Volatile.Read(&frozen)) then this.Freeze()
            match pipelines.TryGetValue source with
            | true, pipeline -> pipeline.Target
            | false, _ -> source

        member this.Upcast(value: obj) =
            if not (Volatile.Read(&frozen)) then this.Freeze()
            let envelopeType = value.GetType()
            if envelopeType.IsGenericType && envelopeType.GetGenericTypeDefinition() = typedefof<Event<obj>> then
                // Match the envelope's declared payload type. F# union cases and
                // C# derived payloads may have a different runtime GetType().
                match pipelines.TryGetValue(envelopeType.GetGenericArguments().[0]) with
                | true, pipeline -> pipeline.Convert value
                | false, _ -> value
            else
                value

    let private registries = ConditionalWeakTable<ActorSystem, Registry>()

    let private registry (system: ActorSystem) =
        registries.GetValue(system, fun _ -> Registry())

    let register<'Old, 'New when 'Old: not null and 'New: not null>
        (system: ActorSystem) (convert: Func<'Old, 'New>) =
        (registry system).Register convert

    let install (system: ActorSystem) (configuration: Registry) =
        configuration.CopyTo(registry system)

    let freeze (system: ActorSystem) = (registry system).Freeze()

    let targetType (system: ActorSystem) (payloadType: Type) =
        (registry system).TargetType payloadType

    let upcastEvent (system: ActorSystem) (value: obj) =
        (registry system).Upcast value
