/// <summary>
///  Contains common types like Events and Commands
/// </summary>
///
/// <namespacedoc>
///   <summary>Functionality for Write Side.</summary>
/// </namespacedoc>
module rec FCQRS.Common

open System
open Akkling
open Akkling.Persistence
open Akka.Cluster.Tools.PublishSubscribe
open Akka
open Akkling.Cluster.Sharding
open Akka.Actor
open Akka.Event
open Microsoft.Extensions.Logging
open FCQRS.Model.Data
open Akka.Streams
open SagaStarter // Contains internal types, careful exposure
open Microsoft.Extensions.Configuration
open System.Diagnostics

/// Marker interface for types that can be serialized by Akka.NET.
type ISerializable = interface end

/// Decision returned by a filtered projection handler: whether the event it just
/// handled should be published to subscribers as-is. `Publish` notifies (so a
/// read-your-writes awaiter wakes on it); `Suppress` updates the read model
/// silently — e.g. an intermediate event that a later one supersedes. The middle
/// ground between an always-publish handler and one returning an arbitrary
/// notification list.
type Notify =
    | Publish
    | Suppress

/// Helper to extract TraceId from a CID (W3C traceparent format: 00-{traceId}-{spanId}-{flags})
/// Returns the full CID string if not in traceparent format
let extractTraceId (cid: CID) =
    let cidStr = cid |> ValueLens.Value |> ValueLens.Value
    let parts = cidStr.Split('-')
    // Traceparent format: "00-{traceId}-{spanId}-{flags}" has 4 parts
    if parts.Length = 4 && parts.[0] = "00" then
        parts.[1] // Return just the traceId
    else
        cidStr // Return full string for non-traceparent CIDs

/// Compare two CIDs by their TraceId (for correlation in distributed tracing scenarios)
let sameTrace (cid1: CID) (cid2: CID) =
    extractTraceId cid1 = extractTraceId cid2

type ILoggerFactoryWrapper =
    abstract LoggerFactory: ILoggerFactory

type IConfigurationWrapper =
    abstract Configuration: IConfiguration

// Helper to create a traceparent CID from current activity context for distributed tracing
// Format: "00-{traceId}-{spanId}-{flags}" (W3C traceparent)
let traceparentCid (): CID =
    match Activity.Current with
    | null ->
        Guid.NewGuid().ToString()
    | act ->
        let traceparent = $"00-{act.TraceId.ToHexString()}-{act.SpanId.ToHexString()}-01"
        traceparent 
    |> ValueLens.CreateAsResult |> Result.value


/// Non-generic access to a Command&lt;_&gt;/Event&lt;_&gt; payload, so a boxed message
/// can be unwrapped to its details without knowing the concrete payload type.
type IEnvelope =
    /// The command/event details, boxed.
    abstract member Payload: obj

/// The aggregate rejected a conditional command before running its handler because
/// its persisted version differed from the caller's expected version.
type AggregateVersionConflictException(aggregateId: string, expectedVersion: int64, actualVersion: int64) =
    inherit InvalidOperationException(
        $"Aggregate '{aggregateId}' has persisted version {actualVersion}; expected {expectedVersion}.")
    /// The target aggregate's entity ID.
    member _.AggregateId = aggregateId
    /// The persisted version required by the caller.
    member _.ExpectedVersion = expectedVersion
    /// The persisted version observed when the aggregate checked the command.
    member _.ActualVersion = actualVersion

/// The aggregate stored nothing for a command: one of its events would start a saga that the
/// command's correlation ID already started for another event. A saga starts once per
/// correlation ID and aggregate, so its second start would be lost.
type SagaAlreadyStartedException(aggregateId: string, correlationId: string, saga: string) =
    inherit InvalidOperationException(
        $"Aggregate '{aggregateId}' stored nothing: correlation ID '{correlationId}' already started saga '{saga}' for another event. Send each command that starts a saga with a new correlation ID.")
    /// The target aggregate's entity ID.
    member _.AggregateId = aggregateId
    /// The correlation ID the command reused.
    member _.CorrelationId = correlationId
    /// The name of the saga that was already started.
    member _.Saga = saga

/// The aggregate's reply when it refuses a command's saga start. It crosses nodes when the
/// caller runs on another node than the aggregate.
type internal SagaStartRefused =
    { CorrelationId: CID
      AggregateId: string
      Saga: string }

    interface ISerializable

// Dedicated transient wire messages. Do not implement ISerializable: older
// receivers must reject their serializer instead of executing an unguarded command.
type internal ConditionalCommand =
    { ExpectedVersion: int64
      Command: obj
      ReplyTo: Akka.Actor.IActorRef }

type internal ConditionalCommandConflict =
    { ExpectedVersion: int64
      ActualVersion: int64
      CommandId: MessageId
      CorrelationId: CID
      AggregateId: string }

/// Represents a command to be processed by an aggregate actor.
/// <typeparam name="'CommandDetails">The specific type of the command payload.</typeparam>
type Command<'CommandDetails> =
    {
        /// The specific details or payload of the command.
        CommandDetails: 'CommandDetails
        /// The timestamp when the command was created.
        CreationDate: DateTime
        /// A unique identifier for the message.
        Id: MessageId
        /// An optional identifier for the actor that sent the command.
        Sender: AggregateId option
        /// The correlation ID used to track the command through the system.
        CorrelationId: CID
        /// Metadata associated with the command.
        Metadata: Map<string, string>
    }

    override this.ToString() = sprintf "%A" this

    interface ISerializable

    interface FCQRS.Model.Data.IMessage with
        member this.CID = this.CorrelationId
        member this.Id = this.Id
        member this.CreationDate = this.CreationDate
        member this.Sender = this.Sender
        member this.Metadata = this.Metadata

    interface IEnvelope with
        // box yields a nullable obj under F# nullness; the payload is never null
        // (a command always has details), so assert non-null at the boundary.
        member this.Payload = nonNull (box this.CommandDetails)

/// Represents an event generated by an aggregate actor as a result of processing a command.
/// <typeparam name="'EventDetails">The specific type of the event payload.</typeparam>
type Event<'EventDetails when 'EventDetails : not null> =
    {
        /// The specific details or payload of the event.
        EventDetails: 'EventDetails
        /// The timestamp when the event was created.
        CreationDate: DateTime
        /// A unique identifier for the message.
        Id: MessageId
        /// An optional identifier for the actor that generated the event.
        Sender: AggregateId option
        /// The correlation ID linking the event back to the originating command.
        CorrelationId: CID
        /// The version number of the aggregate after this event was applied.
        Version: Version
        /// Metadata associated with the event.
        Metadata: Map<string, string>
    }

    override this.ToString() = sprintf "%A" this

    interface ISerializable

    interface FCQRS.Model.Data.IMessage with
        member this.CID = this.CorrelationId
        member this.Id = this.Id
        member this.CreationDate = this.CreationDate
        member this.Sender = this.Sender
        member this.Metadata = this.Metadata

    interface IEnvelope with
        // box yields a nullable obj under F# nullness; EventDetails is `not null`,
        // so the boxed value is never null — assert it at the boundary.
        member this.Payload = nonNull (box this.EventDetails)

/// Metadata key stamped onto DELIVERED aggregate event envelopes: "true" when
/// the event was journaled (PersistEvent family), "false" when it was a
/// deferred or publish-only reply. Only the outbound copy is stamped — the
/// journal record stays clean — so read-your-writes callers can tell whether
/// a projection event will ever follow this ack.
[<Literal>]
let JournaledMetadataKey = "fcqrs:journaled"

type Event<'EventDetails when 'EventDetails : not null> with
    /// Whether this envelope's event was journaled, read from the delivery
    /// stamp: Some true (a projection event will follow), Some false (a
    /// deferred/publish-only reply — nothing to await), or None (an envelope
    /// that never passed through aggregate delivery, e.g. read back from the
    /// journal, or produced by a pre-stamp FCQRS).
    member this.Journaled: bool option =
        match this.Metadata.TryFind JournaledMetadataKey with
        | Some "true" -> Some true
        | Some "false" -> Some false
        | _ -> None

[<Literal>]
let DEFAULT_SHARD = "default-shard"
// Internal types related to saga flow control

type internal ContinueOrAbort<'EventDetails when 'EventDetails : not null> =
    | ContinueOrAbort of Event<'EventDetails>

    interface ISerializable

type internal AbortedEvent = AbortedEvent

/// The stop message cluster sharding sends an entity for passivation and shard hand-off.
/// Akka's default, PoisonPill, is handled ahead of the persistence stash, so it could stop
/// an entity with a save in flight: that save's reply and publication would be lost, and
/// commands waiting behind it dropped. A regular message is processed after the save.
type internal StopEntity = StopEntity

[<AutoOpen>]
module internal Internal =
    type SagaEvent<'TState> =
        // EnteredAt: scheduler-clock time the state was entered, stamped at
        // persist. It is the base for expectation deadlines: recovery re-derives
        // "how long has this state been waiting" from the journal instead of the
        // wall clock, so a crash loop cannot postpone an expectation forever.
        | StateChanged of state: 'TState * enteredAt: System.DateTime

        interface ISerializable


    type SagaStateWithVersion<'SagaData, 'State> =
        { SagaState: SagaState<'SagaData, 'State>
          Version: int64
          // Entry time of SagaState.State, mirrored from the last StateChanged
          // event so snapshot recovery keeps expectation deadlines anchored.
          StateEnteredAt: System.DateTime }

        interface ISerializable

    // Internal constants for saga naming conventions
    [<Literal>]
    let SAGA_Suffix = "~Saga~"

    [<Literal>]
    let CID_Separator = "~"

    /// FCQRS.Model.Data.ShortString's cap, mirrored so the saga naming code can
    /// explain a length failure in terms of the budget an aggregate id has left
    /// once "~Saga~" and a correlation id are appended to it.
    [<Literal>]
    let ShortStringMaxLength = 255

    // Internal default shard resolver
    let shardResolver = fun _ -> DEFAULT_SHARD

    let toEvent (sch: IScheduler) (id: MessageId option) ci sender version metadata event =
        let messageId =
            match id with
            | Some existingId -> existingId
            | None -> Guid.CreateVersion7().ToString() |> ValueLens.CreateAsResult |> Result.value

        { EventDetails = event
          Id = messageId
          CreationDate = sch.Now.UtcDateTime
          Sender = sender
          CorrelationId = ci
          Version = version
          Metadata = metadata }


/// Defines the possible actions an aggregate or saga actor can take after processing a command or event.
/// <typeparam name="'T">The type of the event payload associated with the action (e.g., for PersistEvent).</typeparam>
type EventAction<'T when 'T : not null> =
    /// Persist the event to the journal. The actor's state will be updated using the event handler *after* persistence succeeds.
    | PersistEvent of 'T
    /// Persist several events from one command as a single journal AtomicWrite:
    /// all land or none do (one DB transaction in the SQL plugins), and no state
    /// update, publish or saga wake-up happens until the whole batch is durable.
    /// Versions are allocated sequentially. Atomic for THIS aggregate instance
    /// only — atomicity across aggregates is saga territory. Note the read side
    /// still projects the batch event-by-event (durability is atomic,
    /// read-model visibility is not).
    | PersistAllEvents of 'T list
    /// Persist the event and, once it is durable and folded, immediately save a
    /// snapshot — a manual checkpoint, independent of the SnapshotPolicy cadence.
    | PersistAndSnapshot of 'T
    /// Publish and fold the event in the live actor without storing it or incrementing the persisted version.
    /// A deferred event does not start a saga; running sagas still receive it.
    | DeferEvent of 'T
    /// Publish the event immediately to the mediator without persisting it. The actor's state is not updated.
    | PublishEvent of Event<'T>
    /// Ignore the event or command completely. No persistence, publishing, or state update occurs.
    | IgnoreEvent
    /// Indicate that the command or event could not be handled in the current state.
    | UnhandledEvent
    /// Indicate that the state of a saga has changed (used internally by sagas for persistence).
    | StateChangedEvent of 'T
    | Stash of EventAction<'T>
    | Unstash of EventAction<'T>
    | UnstashAll of EventAction<'T>
    /// Dispatch an async side effect (e.g. an AI/oracle read) as a "mini saga"
    /// without the saga's persistence ceremony. The payload is an INSPECTABLE
    /// DATA description of the effect (boxed) — NOT a closure — so `decide`
    /// stays a pure `(command, state) -> effect` function you can unit-test by
    /// asserting `decide cmd state = RunAsync (box (ClusterThemes texts))`
    /// without any runtime. The oracle lives in the runner registered at
    /// `Fcqrs.aggregateWithEffects`, which maps the description to a command;
    /// that command is sent back to THIS aggregate (reusing the originating
    /// CID) and re-enters `decide`, re-validated against current state.
    ///
    /// EPHEMERAL: the in-flight work is process state, NOT journaled. A crash,
    /// restart, or shard rebalance while it runs loses it SILENTLY — nothing
    /// re-issues it. Use this only when that loss is tolerable; when the result
    /// MUST survive a crash, use a saga (which persists its intent).
    ///
    /// TOTAL: the registered runner must map every outcome (oracle error,
    /// timeout) to a command (e.g. `ClusteringFailed`), never let an exception
    /// escape — `Fcqrs.total` helps. An escaping exception fail-fasts the
    /// process, like a throwing fold.
    | RunAsync of obj

/// Akka's internal log verbosity. FCQRS ships with Akka logging OFF (it is
/// chatty); this enables it without hand-editing HOCON/config.
[<RequireQualifiedAccess>]
type AkkaLogLevel =
    | Off
    | Error
    | Warning
    | Info
    | Debug

    member this.ToHocon() =
        match this with
        | Off -> "OFF"
        | Error -> "ERROR"
        | Warning -> "WARNING"
        | Info -> "INFO"
        | Debug -> "DEBUG"

/// FCQRS's ActivitySource names — register them with your tracing pipeline
/// (e.g. OpenTelemetry: tracing.AddSource(Telemetry.AllActivitySources)).
type Telemetry =
    /// Commands and events flowing through aggregates.
    static member ActivitySourceName: string = "FCQRS"
    /// Saga state transitions.
    static member SagaActivitySourceName: string = "FCQRS.Saga"
    /// Read-side projection of journal events.
    static member QueryActivitySourceName: string = "FCQRS.Query"
    /// Every FCQRS source, for one-call registration.
    static member AllActivitySources: string[] =
        [| Telemetry.ActivitySourceName
           Telemetry.SagaActivitySourceName
           Telemetry.QueryActivitySourceName |]
    /// Metadata key carrying the W3C traceparent. Stamped automatically on
    /// commands created while an Activity is current; flows command -> events ->
    /// saga -> the saga's commands through the existing Metadata plumbing, so
    /// the CID stays a plain correlation id and tracing rides beside it.
    static member TraceparentMetadataKey: string = "traceparent"

    /// Logger category of the out-of-box message-flow narrative: which command
    /// reached which aggregate and what it yielded, saga state transitions, the
    /// commands sagas issue, and the events they pick up. Written at
    /// Information level — these lines describe the application's messages,
    /// not FCQRS internals, so they are ON by default. Silence them with
    /// Telemetry.MessageFlowLogging <- false (or
    /// FcqrsBuilder.WithMessageFlowLogging(false)), or filter this category in
    /// your logging configuration.
    static member MessageFlowCategory: string = "FCQRS.MessageFlow"

    /// Process-wide switch for the message-flow narrative logs. Default: on.
    static member val MessageFlowLogging: bool = true with get, set

    /// Process-wide switch for including rendered message *payloads* in
    /// diagnostics. Default: on.
    ///
    /// Span *names* are always low-cardinality case names (e.g. "Command:Register")
    /// regardless of this switch — payload values never appear there, so
    /// per-operation tracing rules and trace-viewer grouping always work, and
    /// nothing sensitive leaks into the indexed span name.
    ///
    /// This switch governs the *detail*: the payload rendered into span tags
    /// (command.type / event.type) and into the message-flow log lines. Turn it
    /// off (Telemetry.IncludePayloads &lt;- false, or
    /// FcqrsBuilder.WithPayloadDiagnostics(false)) for sensitive domains — tags
    /// and log lines then carry the case name only, matching the span name.
    static member val IncludePayloads: bool = true with get, set

    /// Optional hook invoked right before FCQRS kills the process on a fatal
    /// error. FailFast skips finalizers and ProcessExit handlers, so without
    /// this everything still sitting in a batch exporter or buffered log sink
    /// is silently dropped — including the span and log entry of the fatal
    /// flow itself (every fatal site logs before invoking this hook). Flush
    /// your whole pipeline here, e.g.
    ///   Telemetry.FatalFlush <- Action(fun () ->
    ///       tracerProvider.ForceFlush(3000) |> ignore
    ///       loggerProvider.ForceFlush(3000) |> ignore) // or Serilog's Log.CloseAndFlush()
    /// It runs on a background thread with a 5-second cap so a hung exporter
    /// cannot block the kill.
    static member val FatalFlush: (Action | null) = null with get, set

/// (Internal) Resolve a span's parent: the metadata traceparent first, then the
/// CID itself for backward compat with traceparent-format CIDs.
let internal tryTraceContext (metadata: Map<string, string>) (cidStr: string) : ActivityContext option =
    let tryParse (s: string) =
        let mutable ctx = Unchecked.defaultof<ActivityContext>
        if ActivityContext.TryParse(s, null, &ctx) then Some ctx else None

    match metadata.TryFind Telemetry.TraceparentMetadataKey with
    | Some tp -> tryParse tp
    | None -> tryParse cidStr

/// (Internal) The current Activity as a traceparent string, if any.
let internal currentTraceparent () : string option =
    match Activity.Current with
    | null -> None
    | act -> Some $"00-{act.TraceId.ToHexString()}-{act.SpanId.ToHexString()}-01"

/// (Internal) Gate for the message-flow narrative lines: the process-wide
/// switch first, then the category's own level so filtered sinks pay nothing.
let internal messageFlowEnabled (logger: ILogger) : bool =
    Telemetry.MessageFlowLogging && logger.IsEnabled LogLevel.Information

/// The Value property of each C# 15 union type, or null for any other type.
let private unionValueProperties =
    Collections.Concurrent.ConcurrentDictionary<Type, Reflection.PropertyInfo | null>()

/// (Internal) The active case of a C# 15 union, None for any other value. The C# compiler
/// marks a union with System.Runtime.CompilerServices.UnionAttribute and keeps the case in
/// a generated Value property; a type is matched by the attribute, not by having a Value.
let internal unionCaseOf (value: obj) : obj option =
    let property =
        unionValueProperties.GetOrAdd(
            value.GetType(),
            fun t ->
                if t.GetCustomAttributes(false)
                   |> Array.exists (fun a -> a.GetType().FullName = "System.Runtime.CompilerServices.UnionAttribute") then
                    t.GetProperty "Value"
                else
                    null)
    match property with
    | null -> None
    | property ->
        match property.GetValue value with
        | null -> None
        | case -> Some case

/// True when a C# union sits in the value or, through F# union cases, inside it.
let rec private holdsUnionCase (value: obj) =
    (unionCaseOf value).IsSome
    || (let t = value.GetType()
        Microsoft.FSharp.Reflection.FSharpType.IsUnion t
        && Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(value, t)
           |> snd
           |> Array.exists (function
               | null -> false
               | field -> holdsUnionCase field))

/// %A, except that a C# union shows its active case. %A prints only a union's type name,
/// so an F# union holding one, such as StateChangedEvent (UserDefined state), is rendered
/// case by case.
let rec private render (value: obj) : string =
    match unionCaseOf value with
    | Some case -> render case
    | None ->
        let t = value.GetType()
        if Microsoft.FSharp.Reflection.FSharpType.IsUnion t && holdsUnionCase value then
            let case, fields = Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(value, t)
            let rendered =
                fields
                |> Array.map (function
                    | null -> "null"
                    | field -> render field)
            match rendered with
            | [||] -> case.Name
            | _ -> $"""{case.Name} ({String.concat ", " rendered})"""
        else
            sprintf "%A" value

/// (Internal) %A rendering collapsed to a single line, so every flow-narrative
/// entry stays one greppable log line even for multi-field records.
let internal renderValue (value: obj | null) : string =
    let s =
        match value with
        | null -> "null"
        | v -> render v

    if s.Contains '\n' then
        s.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map _.Trim()
        |> String.concat " "
    else
        s

/// (Internal) Unwrap a Command/Event envelope to its domain payload; a no-op
/// (returns the value itself) for anything that is not an envelope.
let internal detailsOf (msg: obj | null) : obj | null =
    match msg with
    | null -> null
    | msg ->
        let t = msg.GetType()

        let detailsProp =
            match t.GetProperty "EventDetails" with
            | null -> t.GetProperty "CommandDetails"
            | p -> p

        match detailsProp with
        | null -> msg
        | p -> p.GetValue msg

/// (Internal) Render a message for the flow narrative: unwrap the Command/Event
/// envelope to its domain payload (the envelope's CID/version are logged as
/// separate properties), fall back to %A on the whole message.
let internal renderPayload (msg: obj | null) : string = renderValue (detailsOf msg)

/// (Internal) Low-cardinality case name of a domain payload: the F# DU case
/// name, the active case of a C# 15 union (the runtime type of its generated
/// .Value), or the plain type name. Never includes field values — this is what
/// goes into span names, so per-operation tracing rules and trace grouping work
/// and no payload/secret is written to an indexed span name.
let internal caseNameOf (value: obj | null) : string =
    match value with
    | null -> "null"
    | v ->
        let t = v.GetType()
        if Microsoft.FSharp.Reflection.FSharpType.IsUnion t then
            let case, _ = Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(v, t)
            case.Name
        else
            // A C# 15 union is named by its active case's type.
            match unionCaseOf v with
            | Some case -> case.GetType().Name
            | None -> t.Name

/// (Internal) A raw value rendered for a span *tag* or flow-log line, honoring
/// the IncludePayloads switch: full rendering when on, the bare case name when
/// off. Span *names* never use this — they are always caseNameOf.
let internal payloadTag (value: obj | null) : string =
    if Telemetry.IncludePayloads then renderValue value else caseNameOf value

/// (Internal) A Command/Event message rendered for a flow-log line, honoring the
/// IncludePayloads switch: full payload when on, the domain payload's case name
/// when off (the envelope is unwrapped either way).
let internal logPayload (msg: obj | null) : string = payloadTag (detailsOf msg)

/// (Internal) Fatal-path exit. Marks the in-flight span(s) failed and disposed
/// (queueing them for export), gives the host's Telemetry.FatalFlush a bounded
/// chance to drain its exporters, then FailFasts. FailFast is deliberate — see
/// call sites — but it skips all normal shutdown, so this is the one point
/// where spans from the fatal flow can still get out.
let internal fatalFailFast (heldActivity: Activity | null) (message: string) (ex: exn) : unit =
    let markFailed (act: Activity | null) =
        match act with
        | null -> ()
        | act ->
            try
                // OTel semantic-convention exception event, portable across exporters.
                let tags = ActivityTagsCollection()
                tags.Add("exception.type", ex.GetType().FullName)
                tags.Add("exception.message", ex.Message)
                tags.Add("exception.stacktrace", string ex)
                act.AddEvent(ActivityEvent("exception", tags = tags)) |> ignore
                act.SetStatus(ActivityStatusCode.Error, message) |> ignore
                act.Dispose()
            with _ -> ()

    // Capture before disposal: Dispose resets Activity.Current to the parent.
    let current = Activity.Current
    markFailed current

    if not (obj.ReferenceEquals(heldActivity, current)) then
        markFailed heldActivity

    match Telemetry.FatalFlush with
    | null -> ()
    | flush ->
        // The process is already broken: run the flush on a background thread
        // with a hard cap so a hung exporter cannot block the kill.
        try
            let t = Threading.Thread(Threading.ThreadStart(fun () ->
                try flush.Invoke() with _ -> ()))
            t.IsBackground <- true
            t.Start()
            t.Join(TimeSpan.FromSeconds 5.0) |> ignore
        with _ -> ()

    Environment.FailFast(message, ex)

/// Stable logical names for journal payload types. Register every command/event/
/// state type that touches the journal; the serializer then writes manifests like
/// "fcqrs:ev(doc.event)" instead of CLR type names — so types can be
/// renamed or moved freely (update the mapping, old journal rows keep reading).
/// Unregistered types are written under their assembly-qualified CLR name without
/// the assembly version, culture and public-key token; versioned names in older rows
/// still read.
type JournalTypes private () =
    static let gate = obj ()
    static let byName = Collections.Generic.Dictionary<string, Type>()
    static let byType = Collections.Generic.Dictionary<Type, string>()

    static let validateName (name: string) =
        if String.IsNullOrWhiteSpace name || name.IndexOfAny [| '('; ')'; ','; ':' |] >= 0 then
            invalidArg "name" $"Journal type name '%s{name}' must be non-empty and must not contain '(', ')', ',' or ':'"

    static member private MapCore(payloadType: Type, name: string, aliases: string[], allowReplace: bool) =
        let aliases = Array.copy aliases
        validateName name
        aliases |> Array.iter validateName

        // Both indexes form one registry: concurrent registrations must not pass
        // the same conflict check, and readers must not observe a partial write.
        lock gate (fun () ->
            // Validate every conflict before changing either index, so a rejected
            // registration leaves all primary names and aliases untouched.
            let checkName (n: string) =
                match byName.TryGetValue n with
                | true, existing when existing <> payloadType && not allowReplace ->
                    invalidOp $"Journal name '%s{n}' is already mapped to %s{existing.FullName}; use Remap to replace it deliberately"
                | _ -> ()

            checkName name
            aliases |> Array.iter checkName

            match byType.TryGetValue payloadType with
            | true, existing when existing <> name && not allowReplace ->
                invalidOp $"%s{payloadType.FullName} is already mapped to '%s{existing}'; use Remap to replace it deliberately"
            | _ -> ()

            byName[name] <- payloadType
            aliases |> Array.iter (fun n -> byName[n] <- payloadType)
            byType[payloadType] <- name)

    /// Map a payload type to its stable journal name (plus optional read-side aliases).
    /// Concurrent registrations are serialized; a rejected mapping changes no names.
    static member Map(payloadType: Type, name: string, [<ParamArray>] aliases: string[]) =
        JournalTypes.MapCore(payloadType, name, aliases, false)

    /// Map a payload type to its stable journal name (plus optional read-side aliases).
    static member Map<'T>(name: string, [<ParamArray>] aliases: string[]) =
        JournalTypes.MapCore(typeof<'T>, name, aliases, false)

    /// Replace an existing mapping deliberately (e.g. pointing a logical name at a
    /// renamed CLR type).
    static member Remap(payloadType: Type, name: string, [<ParamArray>] aliases: string[]) =
        JournalTypes.MapCore(payloadType, name, aliases, true)

    static member internal TryGetName(t: Type) : string option =
        lock gate (fun () ->
            match byType.TryGetValue t with
            | true, n -> Some n
            | _ -> None)

    static member internal TryGetType(name: string) : Type option =
        lock gate (fun () ->
            match byName.TryGetValue name with
            | true, t -> Some t
            | _ -> None)

/// Snapshot cadence for an aggregate or saga, set per entity at registration.
type SnapshotPolicy =
    /// Use the global config (config:akka:persistence:snapshot-version-count), or 30.
    | Default
    /// Never snapshot: recovery always replays the full journal. Previously
    /// saved snapshots are still honored on recovery.
    | NoSnapshots
    /// Snapshot every N versions (N > 0; invalid values fall back to Default).
    | Every of int

/// Idle passivation for an aggregate type, set per entity at registration.
/// Passivation stops an idle actor and releases its in-memory state; the next
/// command recovers it from the journal. Only messages routed through cluster
/// sharding count as activity.
///
/// Sagas ignore this: their shard regions remember entities, which disables
/// idle passivation in Akka.NET. A saga stops at StopSaga or abort instead.
[<RequireQualifiedAccess>]
type PassivationPolicy =
    /// Use configuration: `akka.cluster.sharding.<EntityName>.passivate-idle-entity-after`,
    /// then `akka.cluster.sharding.passivate-idle-entity-after`, then Akka.NET's 120s.
    | Default
    /// Passivate after this idle period, overriding configuration. A non-positive
    /// value means Never.
    | After of TimeSpan
    /// Never passivate on idle. The entity stays resident until the node stops or
    /// the shard moves, so recovery cost is traded for memory held indefinitely.
    | Never

/// Represents the name identifying a target actor for a command, typically used within sagas.
type TargetName =
    /// Identify the target by its string name (entity ID).
    | Name of string
    /// Identify the target as the originator actor of the current saga process.
    | Originator

/// Represents the information needed to locate or create a target actor, typically used within sagas.
type FactoryAndName =
    {
        /// The factory function (or entity ref creator) used to potentially create the actor.
        Factory: obj // Typically (string -> IEntityRef<obj>)
        /// The name identifier for the target actor.
        Name: TargetName
    }

/// Represents the target of a command execution triggered by a saga.
type TargetActor =
    /// Specifies the target using a factory function and name.
    | FactoryAndName of FactoryAndName
    /// Specifies the target using its direct IActorRef (usually boxed as obj).
    | ActorRef of obj // Typically IActorRef
    /// Specifies the target as the original sender of the message that triggered the current saga step.
    /// NOTE: side effects run inside persist re-injections, where the ambient sender is the
    /// journal actor, or in the subscription-ack re-drive, where it is the pub-sub mediator —
    /// never the original trigger. Commands to Sender therefore dead-letter; the saga logs a
    /// warning at resolution. Use FactoryAndName with Originator to reach the originator instead.
    | Sender
    /// Specifies the target as the current saga actor itself.
    | Self

/// Represents a command to be executed, often scheduled or triggered by a saga.
type ExecuteCommand =
    {
        /// The target actor for the command.
        TargetActor: TargetActor
        /// The command message to send (boxed).
        Command: obj
        /// An optional delay in milliseconds before sending the command.
        DelayInMs: (int64 * string) option
    }

/// Retry cadence for a saga expectation (see <see cref="T:FCQRS.Common.Expectation"/>).
type RetrySchedule =
    /// Re-send at a fixed interval.
    | FixedInterval of TimeSpan
    /// Re-send with exponential backoff: first after `Initial`, each following
    /// interval multiplied by `Factor`, capped at `Max`. A stretch-only jitter of
    /// up to +25% is applied to every wake so many sagas released by one
    /// infrastructure blip do not retry in lockstep.
    | Backoff of Initial: TimeSpan * Factor: float * Max: TimeSpan

/// A declared expectation attached to a saga's waiting state via
/// <c>StayExpecting</c>: the framework sends <c>Resend</c> on state entry,
/// re-sends exactly those commands on the schedule while no state transition is
/// persisted, and past <c>Deadline</c> (measured from the persisted state-entry
/// time, so crash loops cannot postpone it) delivers an
/// <see cref="T:FCQRS.Common.ExpectationExhausted"/> message to the saga's
/// event handler. Commands in <c>Resend</c> must be retry-safe (the same
/// contract recovery re-drives already require) and must not carry their own
/// <c>DelayInMs</c>.
type Expectation =
    {
        /// The commands sent on state entry and re-sent on every retry tick.
        Resend: ExecuteCommand list
        /// Absolute time budget measured from the persisted state-entry time.
        Deadline: TimeSpan
        /// Cadence of re-sends inside the deadline window.
        RetryEvery: RetrySchedule
    }

/// Delivered to the saga's event handler when an expectation's deadline has
/// passed without a state transition. The handler must match this type and
/// answer with a state change (typically to a domain failure or compensation
/// state). An unhandled exhaustion is logged as an error and re-delivered one
/// deadline period later; the framework never invents a terminal state. The
/// original reply may still arrive after exhaustion — the escalated state's
/// handler should decide what a late success means.
type ExpectationExhausted =
    {
        /// Union-case name of the state whose expectation ran out.
        StateName: string
        /// The persisted entry time of that state. The initial state of a saga registered with
        /// `InitializeSaga` is never persisted; for it, this is the creation time of the saga's
        /// starting event.
        EnteredAt: DateTime
        /// Number of retry ticks that elapsed before exhaustion.
        Attempts: int
    }

/// Represents the next state transition for a saga after processing an event or timeout.
type SagaTransition<'State> =
    /// The saga should stop and terminate
    | StopSaga
    /// The saga should stay in current state without changes
    | Stay
    /// The saga should transition to a new state
    | NextState of 'State
    /// Stay in the current state, but declare that the expectation's commands
    /// anticipate a state transition: the framework re-sends them on the
    /// schedule and delivers ExpectationExhausted past the deadline.
    | StayExpecting of Expectation

/// Represents the state of a saga instance.
/// <typeparam name="'SagaData">The type of the custom data held by the saga.</typeparam>
/// <typeparam name="'State">The type representing the saga's current state machine state (e.g., an enum or DU).</typeparam>
type SagaState<'SagaData, 'State> =
    {
        /// The custom data associated with this saga instance.
        Data: 'SagaData
        /// The current state machine state of the saga.
        State: 'State
    }

    interface ISerializable

/// Defines the core functionalities and context provided by the FCQRS environment to actors.
/// This interface provides access to essential Akka.NET services and FCQRS initialization methods.
[<Interface>]
type IActor =
    /// Gets the reference to the distributed pub/sub mediator actor.
    abstract Mediator: Akka.Actor.IActorRef
    /// Gets the Akka Streams materializer.
    abstract Materializer: ActorMaterializer
    /// Gets the hosting ActorSystem.
    abstract System: ActorSystem
    /// Subscribes to the result of a command sent to another actor.
    abstract SubscribeForCommand: CommandHandler.Command<'a, 'b> -> Async<Common.Event<'b>>
    /// Stops the actor system gracefully.
    abstract Stop: unit -> System.Threading.Tasks.Task
    /// Gets the logger factory.
    abstract LoggerFactory: ILoggerFactory
    /// Gets the configuration.
    abstract Configuration: IConfiguration
    /// Gets the time provider.
    abstract TimeProvider: TimeProvider

    /// Creates a command subscription to wait for a specific event from a target actor.
    /// Sends the command and asynchronously returns the first matching event received.
    /// If no matching event arrives within `akka.fcqrs.command-timeout` (default
    /// 30s) — e.g. the aggregate decided UnhandledEvent/IgnoreEvent or the filter
    /// never matches — the returned Async raises TimeoutException instead of
    /// hanging forever. If the actor system stops first, it raises
    /// OperationCanceledException; the command may or may not have been applied.
    /// <param name="factory">Entity factory function for the target actor type.</param>
    /// <param name="cid">Correlation ID for tracking.</param>
    /// <param name="id">Entity ID of the target actor.</param>
    /// <param name="command">The command payload to send.</param>
    /// <param name="filter">A predicate function to select the desired event.</param>
    /// <param name="metadata">Optional metadata to include with the command.</param>
    /// <returns>An async computation yielding the target event.</returns>
    abstract CreateCommandSubscription:
        (string -> IEntityRef<obj>) ->
        CID ->
        AggregateId ->
        'b ->
        ('c -> bool) ->
        Map<string, string> option ->
            Async<Event<'c>>

    /// Initializes a sharded, persistent aggregate actor.
    /// <param name="cfg">Environment configuration (IConfiguration & ILoggerFactory).</param>
    /// <param name="initialState">The initial state for new aggregate instances.</param>
    /// <param name="name">The shard type name for this aggregate.</param>
    /// <param name="handleCommand">The command handler function: `Command -> State -> EventAction`.</param>
    /// <param name="apply">The event handler function: `Event -> State -> State`.</param>
    /// <param name="snapshotPolicy">Snapshot cadence for this aggregate.</param>
    /// <param name="passivationPolicy">Idle passivation for this aggregate; `Default` defers to configuration.</param>
    /// <returns>An entity factory (`EntityFac<obj>`) for creating instances of this actor.</returns>
    abstract InitializeActor:
        'a ->
        string ->
        (Command<'c> -> 'a -> EventAction<'b>) ->
        (Event<'b> -> 'a -> 'a) ->
        SnapshotPolicy ->
        PassivationPolicy ->
            EntityFac<obj> when 'b: not null

    /// Like InitializeActor, plus a runner for the `RunAsync` effect: it maps a
    /// boxed effect description to a boxed command (self-dispatched, re-entering
    /// decide). None means the aggregate does not use RunAsync (and using it
    /// fail-fasts). The runner must be total (map failure to a command).
    abstract InitializeActorWithRunner:
        'a ->
        string ->
        (Command<'c> -> 'a -> EventAction<'b>) ->
        (Event<'b> -> 'a -> 'a) ->
        SnapshotPolicy ->
        PassivationPolicy ->
        (obj -> Async<obj>) option ->
            EntityFac<obj> when 'b: not null

    /// Initializes a sharded, persistent saga actor.
    /// <param name="initialState">The initial state (`SagaState`) for new saga instances.</param>
    /// <param name="handleEvent">The event handler function: `Event -> SagaState -> EventAction`.</param>
    /// <param name="applySideEffects">Function determining side effects based on state transitions: `SagaState -> Option<StartingEvent> -> bool -> SagaTransition<NewState> * ExecuteCommand list`. CONTRACT: it must be idempotent for the same state — the framework invokes it on every state entry, on recovery re-drives, and (for this raw API) again when the start handshake acknowledges, so issued commands must be retry-safe. The SagaBuilder facade absorbs the extra invocations by returning Stay; raw sagas must do it themselves.</param>
    /// <param name="applyStateChange">Function to apply internal state changes: `SagaState -> SagaState`.</param>
    /// <param name="name">The shard type name for this saga.</param>
    /// <returns>An entity factory (`EntityFac<obj>`) for creating instances of this saga.</returns>
    abstract InitializeSaga:
        SagaState<'SagaState, 'State> ->
        (obj -> SagaState<'SagaState, 'State> -> EventAction<'State>) ->
        (SagaState<'SagaState, 'State>
            -> option<SagaStartingEvent<Event<'c>>>
            -> bool
            -> SagaTransition<'State> * ExecuteCommand list) ->
        (SagaState<'SagaState, 'State> -> SagaState<'SagaState, 'State>) ->
        string ->
        SnapshotPolicy ->
            EntityFac<obj>

    /// Registers the start rules: which sagas each stored event starts. Call it once, after
    /// registering the aggregates and sagas, with an empty rule when there are no sagas.
    /// <param name="eventHandler">A function mapping a received event object to a list of saga definitions to start: `obj -> list<(Factory * Prefix * StartingEvent)>`.</param>
    abstract InitializeSagaStarter: (obj -> list<(string -> IEntityRef<obj>) * PrefixConversion * obj>) -> unit

    /// Simplified saga starter where you just return factories.
    /// Uses default PrefixConversion (identity) and passes through the original event.
    abstract InitializeSagaStarter: (obj -> list<(string -> IEntityRef<obj>)>) -> unit

// Internal helper to create Event records

/// How FCQRS names a saga it starts for an originator event.
/// `PrefixConversion (Some f)` names the saga `originatorId~Saga~f(correlationId)`, and `Some id` gives
/// the standard name `originatorId~Saga~correlationId`. A saga reads its originator and correlation id
/// from its own name to receive the originator's events and to correlate the commands it sends, so `f`
/// must return the correlation id, optionally after a prefix that ends with `~` (for example
/// `"audit~" + cid`). The starter logs an error and does not start a saga when `f` changes or drops the
/// correlation id, or throws.
/// `PrefixConversion None` uses `originatorId~correlationId` as the saga name. That name has no `~Saga~`
/// marker, so the saga does not receive its originator's events.
type PrefixConversion = PrefixConversion of ((string -> string) option)

/// Contains types and functions for building and initializing sagas
module SagaBuilder =
    /// Standard recovery logic for Started state that all sagas should use
    /// Handles the version checking handshake with the originator aggregate
    let internal handleStartedState
        recovering
        (startingEvent: option<SagaStartingEvent<_>>)
        (originatorFactory: string -> IEntityRef<obj>)
        =
        match recovering with
        | true ->
            match startingEvent with
            | Some se ->
                let originator =
                    FactoryAndName
                        { Factory = originatorFactory
                          Name = Originator }

                Stay,
                [ { TargetActor = originator
                    Command = ContinueOrAbort se.Event
                    DelayInMs = None } ]
            | None ->
                // Recovered through a pre-SagaSnapshot snapshot taken in Started:
                // no starting event survives to version-check against. Stay put
                // rather than NRE into a deterministic crash loop on recovery.
                Stay, []
        | false -> Stay, []

    /// Standard wrapper for saga states that includes NotStarted/Started
    type SagaStateWrapper<'UserState, 'TEvent when 'TEvent : not null> =
        | NotStarted
        | Started of SagaStartingEvent<Event<'TEvent>>
        | UserDefined of 'UserState

    /// Creates initial saga state with NotStarted
    let internal createInitialState<'SagaData, 'UserState, 'TEvent when 'TEvent : not null>
        (data: 'SagaData)
        : SagaState<'SagaData, SagaStateWrapper<'UserState, 'TEvent>> =
        { State = NotStarted; Data = data }

    /// Wraps user's applySideEffects to handle NotStarted/Started automatically
    let internal wrapApplySideEffects<'SagaData, 'UserState, 'TEvent when 'TEvent : not null>
        (userApplySideEffects:
            SagaState<'SagaData, 'UserState> -> bool -> SagaTransition<'UserState> * ExecuteCommand list)
        (originatorFactory: string -> IEntityRef<obj>)
        (sagaState: SagaState<'SagaData, SagaStateWrapper<'UserState, 'TEvent>>)
        (startingEvent: option<SagaStartingEvent<Event<'TEvent>>>)
        (recovering: bool)
        : SagaTransition<SagaStateWrapper<'UserState, 'TEvent>> * ExecuteCommand list =
        match sagaState.State with
        | NotStarted ->
            match startingEvent with
            | Some startingEvent ->
                let commands =
                    if recovering then
                        let originator =
                            FactoryAndName
                                { Factory = originatorFactory
                                  Name = Originator }

                        [ { TargetActor = originator
                            Command = ContinueOrAbort startingEvent.Event
                            DelayInMs = None } ]
                    else
                        []

                NextState(Started startingEvent), commands
            | None -> Stay, []
        | Started _ ->
            let transition, commands =
                handleStartedState recovering startingEvent originatorFactory

            transition, commands
        | UserDefined userState ->
            let userSagaState =
                { Data = sagaState.Data
                  State = userState }

            let transition, commands = userApplySideEffects userSagaState recovering

            match transition with
            | StopSaga -> StopSaga, commands
            | Stay -> Stay, commands
            | StayExpecting exp -> StayExpecting exp, commands
            | NextState newState -> NextState(UserDefined newState), commands

    /// Wraps user's handleEvent to skip NotStarted but allow Started states
    let internal wrapHandleEvent<'SagaData, 'UserState, 'TEvent  when 'UserState : not null and 'TEvent : not null>
        (userHandleEvent: obj -> SagaState<'SagaData, 'UserState option> -> EventAction<'UserState>)
        (event: obj)
        (sagaState: SagaState<'SagaData, SagaStateWrapper<'UserState, 'TEvent>>)
        : EventAction<SagaStateWrapper<'UserState, 'TEvent>> =
        match sagaState.State with
        | NotStarted -> UnhandledEvent
        | Started _ ->
            // Allow user code to handle events in Started state to transition to user-defined states
            // For Started->UserDefined transitions, pass None since no user state exists yet
            let userSagaState = { Data = sagaState.Data; State = None }

            match userHandleEvent event userSagaState with
            | StateChangedEvent newState -> StateChangedEvent(UserDefined newState)
            | _ -> UnhandledEvent
        | UserDefined userState ->
            let userSagaState =
                { Data = sagaState.Data
                  State = Some userState }

            match userHandleEvent event userSagaState with
            | StateChangedEvent newState -> StateChangedEvent(UserDefined newState)
            | _ -> UnhandledEvent

    /// Wraps user's apply function to handle NotStarted/Started automatically
    let internal wrapApply<'SagaData, 'UserState, 'TEvent when 'TEvent : not null>
        (userApply: SagaState<'SagaData, 'UserState> -> SagaState<'SagaData, 'UserState>)
        (sagaState: SagaState<'SagaData, SagaStateWrapper<'UserState, 'TEvent>>)
        : SagaState<'SagaData, SagaStateWrapper<'UserState, 'TEvent>> =
        match sagaState.State with
        | NotStarted
        | Started _ -> sagaState
        | UserDefined userState ->
            let userSagaState = { Data = sagaState.Data; State = userState }
            let result = userApply userSagaState
            { Data = result.Data; State = UserDefined result.State }

    /// High-level saga initialization that handles all wrapping automatically
    let init<'SagaData, 'UserState, 'TEvent when 'UserState : not null and 'TEvent : not null>
        (actorApi: IActor)
        (sagaData: 'SagaData)
        (userHandleEvent: obj -> SagaState<'SagaData, 'UserState option> -> EventAction<'UserState>)
        (userApplySideEffects:
            SagaState<'SagaData, 'UserState> -> bool -> SagaTransition<'UserState> * ExecuteCommand list)
        (userApply:
            SagaState<'SagaData, SagaStateWrapper<'UserState, 'TEvent>>
                -> SagaState<'SagaData, SagaStateWrapper<'UserState, 'TEvent>>)
        (originatorFactory: string -> IEntityRef<obj>)
        (sagaName: string)
        (snapshotPolicy: SnapshotPolicy)
        =
        let initialState = createInitialState<'SagaData, 'UserState, 'TEvent> sagaData
        let handleEvent = wrapHandleEvent userHandleEvent
        let applySideEffects:
            SagaState<'SagaData, SagaStateWrapper<'UserState, 'TEvent>>
                -> option<SagaStartingEvent<Event<'TEvent>>>
                -> bool
                -> SagaTransition<SagaStateWrapper<'UserState, 'TEvent>> * ExecuteCommand list
            =
            wrapApplySideEffects userApplySideEffects originatorFactory

        actorApi.InitializeSaga initialState handleEvent applySideEffects userApply sagaName snapshotPolicy

    /// Simplified saga initialization with unwrapped apply function
    let initSimple<'SagaData, 'UserState, 'TEvent when 'UserState : not null and 'TEvent : not null>
        (actorApi: IActor)
        (sagaData: 'SagaData)
        (userHandleEvent: obj -> SagaState<'SagaData, 'UserState option> -> EventAction<'UserState>)
        (userApplySideEffects:
            SagaState<'SagaData, 'UserState> -> bool -> SagaTransition<'UserState> * ExecuteCommand list)
        (userApply: SagaState<'SagaData, 'UserState> -> SagaState<'SagaData, 'UserState>)
        (originatorFactory: string -> IEntityRef<obj>)
        (sagaName: string)
        (snapshotPolicy: SnapshotPolicy)
        =
        let wrappedApply = wrapApply userApply
        init<'SagaData, 'UserState, 'TEvent>
            actorApi
            sagaData
            userHandleEvent
            userApplySideEffects
            wrappedApply
            originatorFactory
            sagaName
            snapshotPolicy

/// The saga-start handshake: start rules, saga names, and readiness (internal implementation detail).
module SagaStarter =
    open Microsoft.FSharp.Reflection

    [<AutoOpen>]
    module Internal =
        /// The entity id behind an actor's Path.Name. The shard names entity actors
        /// Uri.EscapeDataString(entityId), so a path name is the ESCAPED id and does
        /// NOT round-trip through the name helpers below: they all parse ENTITY IDS.
        /// Feeding a path name to them straight built saga names the shard then
        /// escaped a second time ("counter 1" -> actor "counter%201" -> saga id
        /// "counter%201~Saga~cid" -> saga actor "counter%25201~Saga~cid"), so the
        /// starter's batch, the saga's topic and its Originator target all disagreed
        /// and the start handshake fail-fasted the process. Identity for ids that
        /// need no escaping, so existing names and journals are unaffected.
        let internal entityIdOf (pathName: string) = Uri.UnescapeDataString pathName

        // Internal helpers for manipulating saga/originator names and CIDs.
        // All of these take ENTITY IDS (see entityIdOf), never actor path names.

        /// LAST occurrence, not the first: the framework's own suffix is the last
        /// one in a saga name, so an entity id that itself contains "~Saga~" still
        /// resolves to the right originator. A CID cannot contribute one (CIDs
        /// reject "~"). With IndexOf, id "x~Saga~y" resolved to "x" and the saga
        /// silently never received an event.
        let internal toOriginatorName (name: string) =
            let index = name.LastIndexOf(SAGA_Suffix)
            if index > 0 then name.Substring(0, index) else name

        let internal toRawGuid (name: string) =
            let index = name.LastIndexOf(CID_Separator)
            name.Substring(index + 1).Replace(SAGA_Suffix, "")

        let internal toCidWithExisting (name: string) (existing: string) =
            let originator = name
            let guid = existing |> toRawGuid
            originator + CID_Separator + guid

        let internal cidToSagaName (name: string) = name + SAGA_Suffix
        let internal isSaga (name: string) = name.Contains(SAGA_Suffix)

        /// The pub-sub topic correlated events flow over: the originator's ESCAPED
        /// entity id (i.e. its actor path name) plus the raw CID. Publisher and both
        /// subscribers (the saga and the command awaiter) must build it identically —
        /// this is the one place that shape is defined.
        let internal correlationTopic (originatorEntityId: string) (cid: string) =
            Uri.EscapeDataString originatorEntityId + CID_Separator + cid

        /// The topic a saga listens on, derived from its own entity id.
        let internal sagaTopic (sagaEntityId: string) =
            correlationTopic (toOriginatorName sagaEntityId) (toRawGuid sagaEntityId)

        /// The readiness message a saga sends each aggregate that started it: its starting
        /// event is stored and it listens for the events that follow.
        type internal Command = | Continue

        type internal Message =
            | Command of Command
            /// The saga already stored a different starting event, so it cannot start for
            /// this one. The aggregate stores nothing and refuses the command.
            | Refused

            // Continue crosses nodes when a saga runs on another node than the
            // aggregate that started it. The default Newtonsoft serializer cannot
            // construct these private F# union cases; use the existing F# serializer
            // and unchanged message shape instead.
            interface ISerializable

        /// Which sagas an event starts: each saga's shard factory, its name conversion,
        /// and the payload of its starting message.
        type internal StartRules = obj -> ((string -> IEntityRef<obj>) * PrefixConversion * obj) list

        // The start rules wired for each actor system. Aggregate and saga regions go live
        // before the application wires its rules, and remembered sagas can re-drive
        // commands in that window, so an aggregate can store an event first. It waits on
        // this task.
        let private startRules =
            System.Runtime.CompilerServices.ConditionalWeakTable<ActorSystem, Threading.Tasks.TaskCompletionSource<StartRules>>()

        let internal startRulesOf (system: ActorSystem) =
            startRules.GetValue(
                system,
                fun _ ->
                    Threading.Tasks.TaskCompletionSource<StartRules>(
                        Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously))

        /// The entity id of the saga an originator starts for a correlation id, or None when
        /// the saga must not start. A saga reads its originator, its event topic, and the CID
        /// of every command it sends from its own entity id, and the originator publishes the
        /// starting event under the original CID. A name conversion that drops that CID would
        /// leave a builder saga in Started forever, so such a saga is refused.
        let internal sagaIdFor (log: ILogger) (originatorId: string) (cid: string) prefix =
            match prefix with
            | PrefixConversion None -> Some cid
            | PrefixConversion(Some f) ->
                let rawCid = cid |> toRawGuid

                let converted =
                    try
                        Some(f rawCid)
                    with error ->
                        log.LogError(
                            error,
                            "Saga not started for originator {Originator} [cid: {CID}]: the prefix conversion threw.",
                            originatorId,
                            rawCid)

                        None

                match converted with
                | None -> None
                | Some converted ->
                    let sagaId = originatorId + SAGA_Suffix + converted

                    if toOriginatorName sagaId = originatorId && toRawGuid sagaId = rawCid then
                        Some sagaId
                    else
                        log.LogError(
                            "Saga not started for originator {Originator} [cid: {CID}]: the prefix conversion returned '{Converted}'. A converted saga name must end with the correlation id, optionally after a prefix that ends with '~'.",
                            originatorId,
                            rawCid,
                            converted)

                        None

        let internal publishEvent (logger: ILogger) (mailbox: Actor<_>) (mediator) event (cid) =
            let sender = mailbox.Sender()
            let self = mailbox.Self
            logger.LogDebug("sender: {sender}", sender.Path.ToString())
            logger.LogDebug("Publishing event {event} from {self}", event, self.Path.ToString())

            let senderEntityId = sender.Path.Name |> entityIdOf

            if senderEntityId |> isSaga then
                let originatorName = senderEntityId |> toOriginatorName

                if originatorName <> (self.Path.Name |> entityIdOf) then
                    sender <! event

            mediator <! Akka.Cluster.Tools.PublishSubscribe.Publish(self.Path.Name, event)
            mediator <! Akka.Cluster.Tools.PublishSubscribe.Publish(self.Path.Name + CID_Separator + cid, event)

        /// Tells each aggregate that sent this saga its starting message that the saga is
        /// ready. A recovered saga has lost those references; an aggregate still waiting
        /// sends the starting message again, and the saga answers that one.
        let internal cont (saga: Akka.Actor.IActorRef) (coordinators: Akka.Actor.IActorRef list) =
            for coordinator in coordinators do
                coordinator.Tell(Continue |> Command, saga)

        let internal acknowledgeReady saga coordinators startingEventPersisted subscriptionAcked =
            if startingEventPersisted && subscriptionAcked then
                cont saga coordinators

        let internal subscriber (mediator: IActorRef<_>) (mailbox: Eventsourced<_>) =
            let topic = mailbox.Self.Path.Name |> entityIdOf |> sagaTopic
            mediator <! box (Subscribe(topic, untyped mailbox.Self))

        let internal (|SubscriptionAcknowledged|_|) (context: Actor<obj>) (msg: obj) : obj option =
            let topic = context.Self.Path.Name |> entityIdOf |> sagaTopic

            match msg with
            | :? SubscribeAck as s when s.Subscribe.Topic = topic -> Some msg
            | _ -> None

        let internal unboxx (msg: obj) =
            let genericType =
                typedefof<SagaStartingEvent<_>>.MakeGenericType [| msg.GetType() |]

            FSharpValue.MakeRecord(genericType, [| msg |])

        /// Registers the start rules of an actor system. Aggregates waiting for them
        /// continue once they are set.
        let internal init (system: ActorSystem) (rules: StartRules) =
            if not ((startRulesOf system).TrySetResult rules) then
                invalidOp "The saga starters of this actor system are already wired. Wire them once, after registering the aggregates and sagas."

    /// Wraps an event that is intended to start a saga.
    /// This is typically the message sent to a saga actor upon its creation.
    /// <typeparam name="'T">The type of the starting event payload.</typeparam>
    type SagaStartingEvent<'T> =
        {
            /// The actual event payload that triggers the saga.
            Event: 'T
        }

        interface ISerializable

/// (Internal) Contains the implementation for command subscriptions.
[<AutoOpen>]
module CommandHandler =
    [<AutoOpen>]
    module Internal =
        // Internal active pattern for subscription acknowledgements
        let (|SubscriptionAcknowledged|_|) (msg: obj) =
            match msg with
            | :? SubscribeAck as s -> Some s
            | _ -> None

        type State<'Command, 'Event> =
            { CommandDetails: CommandDetails<'Command, 'Event>
              Sender: IActorRef } // The actor waiting for the response

        /// Internal marker replied to the asker when the awaited event never arrives.
        type internal CommandSubscriptionTimeout =
            { EntityId: string
              Cid: string }

        /// One scheduled deadline per request. Unlike ReceiveTimeout, unrelated
        /// or nonmatching events cannot postpone it.
        type internal CommandDeadlineElapsed = CommandDeadlineElapsed

        /// Internal marker replied to the asker when the event filter throws.
        /// The ask must fail loudly with the real exception: letting it escape
        /// the subscriber actor restarts the incarnation without its Execute
        /// message or deadline, hanging the caller forever.
        type internal CommandSubscriptionFilterError =
            { EntityId: string
              Cid: string
              Exception: exn }

        /// Internal marker replied to the asker when the subscriber stops before the
        /// request completes, for example during actor-system shutdown.
        type internal CommandSubscriptionStopped = CommandSubscriptionStopped of entityId: string * cid: string

        // Upper bound for one command subscription: the aggregate's reply event
        // (persist + publish) should arrive in milliseconds; if it never does
        // (decide returned UnhandledEvent/IgnoreEvent, or the filter never
        // matches) the caller must fail instead of hanging forever, and the
        // temporary subscriber actor must not leak. Override with HOCON key
        // `akka.fcqrs.command-timeout`: a bare number means SECONDS (same rule
        // as `akka.fcqrs.saga-start-timeout`); HOCON durations ("500ms", "2s",
        // "1m") are also accepted. NOTE: HOCON GetTimeSpan reads a bare number
        // as MILLISECONDS, so never parse this key with GetTimeSpan alone.
        let private defaultCommandTimeout = TimeSpan.FromSeconds 30.0
        [<Literal>]
        let private commandTimeoutKey = "akka.fcqrs.command-timeout"

        let internal resolveCommandTimeout (cfg: Akka.Configuration.Config) =
            try
                if cfg.HasPath commandTimeoutKey then
                    match cfg.GetString commandTimeoutKey |> Int32.TryParse with
                    | true, seconds when seconds > 0 -> TimeSpan.FromSeconds(float seconds)
                    | _ ->
                        let t = cfg.GetTimeSpan(commandTimeoutKey, Nullable(defaultCommandTimeout))
                        if t > TimeSpan.Zero then t else defaultCommandTimeout
                else
                    defaultCommandTimeout
            with _ -> defaultCommandTimeout

        let private subscribeForCommandCore<'Command, 'Event when 'Event : not null> expectedVersion system mediator (command: Command<'Command, 'Event>) =
            let actorProp mediator (mailbox: Actor<obj>) =
                let log = mailbox.UntypedContext.GetLogger()
                let commandTimeout = resolveCommandTimeout mailbox.System.Settings.Config
                let mutable deadline: ICancelable option = None
                let mutable commandSent = false
                let mutable replied = false

                let cancelDeadline () =
                    deadline |> Option.iter (fun timer -> timer.Cancel())
                    deadline <- None

                // Every accepted request gets exactly one reply, including when this actor stops first.
                let reply (s: State<'Command, 'Event>) (message: obj) =
                    replied <- true
                    s.Sender.Tell(message, untyped mailbox.Self)

                let matchesTarget (target: IEntityRef<obj>) =
                    // DistributedPubSub preserves the publishing actor as Sender.
                    // Topics are intentionally unchanged for existing sagas and
                    // rolling deployments; the publisher identifies the aggregate
                    // type as well as the entity, even when event types are shared.
                    let path = mailbox.Sender().Path
                    entityIdOf path.Name = target.EntityId
                    && entityIdOf path.Parent.Name = target.ShardId
                    && entityIdOf path.Parent.Parent.Name = target.TypeName

                let rec set (state: State<'Command, 'Event> option) =
                    actor {
                        let! msg = mailbox.Receive()

                        match box msg |> Unchecked.nonNull with
                        // On SubscribeAck, send the actual command to the target entity
                        | SubscriptionAcknowledged ack ->
                            match state with
                            | Some s when not commandSent ->
                                let cd = s.CommandDetails
                                let cid = cd.Cmd.CorrelationId |> ValueLens.Value |> ValueLens.Value
                                if ack.Subscribe.Topic = correlationTopic cd.EntityRef.EntityId cid then
                                    commandSent <- true
                                    match expectedVersion with
                                    | None -> cd.EntityRef <! box cd.Cmd
                                    | Some version ->
                                        cd.EntityRef <! box {
                                            ExpectedVersion = version
                                            Command = box cd.Cmd |> Unchecked.nonNull
                                            ReplyTo = untyped mailbox.Self }
                            | _ -> ()
                            return! set state
                        // When receiving the initial Execute command, store details and subscribe
                        | :? Command<'Command, 'Event> as s ->
                            let sender = mailbox.Sender()

                            let cd =
                                match s with
                                | Execute cd -> cd

                            let cid = cd.Cmd.CorrelationId |> ValueLens.Value |> ValueLens.Value
                            mediator
                            <! box (Subscribe(correlationTopic cd.EntityRef.EntityId cid, untyped mailbox.Self))

                            // Schedule once from request acceptance. ReceiveTimeout
                            // measures inactivity and is reset by every rejected
                            // event, so it cannot bound the lifetime of this request.
                            cancelDeadline ()
                            deadline <-
                                Some(mailbox.System.Scheduler.ScheduleTellOnceCancelable(
                                    commandTimeout,
                                    untyped mailbox.Self,
                                    CommandDeadlineElapsed,
                                    ActorRefs.NoSender))

                            return!
                                Some
                                    { CommandDetails = cd
                                      Sender = untyped sender }
                                |> set
                        // The awaited event never arrived: fail the asker and stop,
                        // so an UnhandledEvent/IgnoreEvent decision or a filter that
                        // never matches cannot hang the caller or leak this actor.
                        | :? CommandDeadlineElapsed ->
                            cancelDeadline ()
                            match state with
                            | Some s ->
                                let cid = s.CommandDetails.Cmd.CorrelationId |> ValueLens.Value |> ValueLens.Value

                                log.Warning(
                                    "Command subscription timed out after {0} waiting for a matching event from entity {1} [cid: {2}]. The aggregate may have returned UnhandledEvent/IgnoreEvent, or the filter never matched.",
                                    commandTimeout,
                                    s.CommandDetails.EntityRef.EntityId,
                                    cid)

                                reply s
                                    (({ EntityId = s.CommandDetails.EntityRef.EntityId
                                        Cid = cid }: CommandSubscriptionTimeout) :> obj)
                            | None -> ()

                            return! Stop
                        | :? SagaStartRefused as refused ->
                            match state with
                            | Some s when commandSent
                                          && refused.CorrelationId = s.CommandDetails.Cmd.CorrelationId
                                          && matchesTarget s.CommandDetails.EntityRef ->
                                cancelDeadline ()
                                reply s (refused :> obj)
                                return! Stop
                            | _ -> return! set state
                        | :? ConditionalCommandConflict as conflict ->
                            match state with
                            | Some s when commandSent
                                          && expectedVersion = Some conflict.ExpectedVersion
                                          && conflict.CommandId = s.CommandDetails.Cmd.Id
                                          && conflict.CorrelationId = s.CommandDetails.Cmd.CorrelationId
                                          && conflict.AggregateId = s.CommandDetails.EntityRef.EntityId
                                          && matchesTarget s.CommandDetails.EntityRef ->
                                cancelDeadline ()
                                reply s (conflict :> obj)
                                return! Stop
                            | _ -> return! set state
                        // A CID can cross aggregate types. Only accept an event
                        // published by the aggregate instance this request targets.
                        | :? (Event<'Event>) as e ->
                            match state with
                            | Some s when commandSent
                                          && sameTrace e.CorrelationId s.CommandDetails.Cmd.CorrelationId
                                          && (expectedVersion.IsNone || e.Id = s.CommandDetails.Cmd.Id)
                                          && matchesTarget s.CommandDetails.EntityRef ->
                                match (try Choice1Of2(s.CommandDetails.Filter e.EventDetails) with ex -> Choice2Of2 ex) with
                                | Choice1Of2 true ->
                                    cancelDeadline ()
                                    reply s (e :> obj) // Send event back to original asker
                                    return! Stop // Stop the temporary subscription actor
                                | Choice1Of2 false -> return! set state // Continue waiting
                                | Choice2Of2 ex ->
                                    cancelDeadline ()
                                    // A filter exception must not escape the actor: the
                                    // default supervisor would restart this incarnation,
                                    // dropping the consumed Execute and the deadline
                                    // armed with it — hanging the caller's ask forever.
                                    // Fail the asker loudly with the real exception instead.
                                    let cid =
                                        s.CommandDetails.Cmd.CorrelationId
                                        |> ValueLens.Value
                                        |> ValueLens.Value

                                    log.Error(
                                        ex,
                                        "Command subscription filter threw for entity {0} [cid: {1}]; failing the waiting caller.",
                                        s.CommandDetails.EntityRef.EntityId,
                                        cid)

                                    reply s
                                        ({ EntityId = s.CommandDetails.EntityRef.EntityId
                                           Cid = cid
                                           Exception = ex } :> obj)

                                    return! Stop
                            | _ ->
                                // Different trace, or a restarted incarnation that lost
                                // its state: keep waiting for the matching event.
                                return! set state
                        | LifecycleEvent PostStop ->
                            cancelDeadline ()
                            // Stopping without a reply, as during actor-system shutdown, must
                            // still release the caller.
                            match state with
                            | Some s when not replied ->
                                let cid = s.CommandDetails.Cmd.CorrelationId |> ValueLens.Value |> ValueLens.Value
                                reply s (CommandSubscriptionStopped(s.CommandDetails.EntityRef.EntityId, cid) :> obj)
                            | _ -> ()
                            return! Ignore
                        | LifecycleEvent _ -> return! Ignore // Ignore actor lifecycle events
                        | _ ->
                            log.Error("Unexpected message in subscriber: {msg}", msg)
                            return! Ignore // Ignore other unexpected messages
                    }

                set None // Initial state is None

            // Spawn the temporary actor and send it the initial Execute command
            async {
                // The subscriber replies to every request it accepts, even when it stops first.
                // This bound covers a subscriber that stops before accepting one, such as a
                // send racing actor-system shutdown.
                // Ask cancels through CancellationTokenSource.CancelAfter, which rejects a delay
                // beyond ~49.7 days, so a longer configured timeout is clamped here.
                let maxBound = TimeSpan.FromMilliseconds(float UInt32.MaxValue - 1.0)

                let bound =
                    match box system with
                    | :? ActorSystem as actorSystem -> Some actorSystem.Settings.Config
                    | :? IActorContext as context -> Some context.System.Settings.Config
                    | _ -> None
                    |> Option.map (fun config ->
                        let bound = resolveCommandTimeout config + TimeSpan.FromSeconds 5.0
                        if bound > maxBound then maxBound else bound)

                let subscriber = spawnAnonymous system (props (actorProp mediator))

                let! (res: obj) =
                    async {
                        try
                            return! subscriber.Ask(box command, bound)
                        with :? AskTimeoutException as timeout ->
                            // Nobody waits for this subscriber any more.
                            (untyped subscriber).Tell(PoisonPill.Instance)
                            return
                                raise (
                                    TimeoutException(
                                        "The command subscription did not answer within the command timeout (akka.fcqrs.command-timeout).",
                                        timeout
                                    )
                                )
                    }

                match box res with
                | :? CommandSubscriptionStopped as stopped ->
                    let (CommandSubscriptionStopped(entityId, cid)) = stopped
                    return
                        raise (
                            OperationCanceledException(
                                $"The command subscription for entity '{entityId}' [cid: {cid}] stopped before a reply arrived, because the actor system shut down or the subscription failed. The command may or may not have been applied."
                            )
                        )
                | :? SagaStartRefused as refused ->
                    return
                        raise (
                            SagaAlreadyStartedException(
                                refused.AggregateId,
                                refused.CorrelationId |> ValueLens.Value |> ValueLens.Value,
                                refused.Saga
                            )
                        )
                | :? ConditionalCommandConflict as conflict ->
                    return raise (AggregateVersionConflictException(
                        conflict.AggregateId, conflict.ExpectedVersion, conflict.ActualVersion))
                | :? CommandSubscriptionFilterError as f ->
                    return
                        raise (
                            InvalidOperationException(
                                $"The event filter for the command subscription on entity '{f.EntityId}' [cid: {f.Cid}] threw an exception. See the inner exception.",
                                f.Exception
                            )
                        )
                | :? CommandSubscriptionTimeout as t ->
                    return
                        raise (
                            TimeoutException(
                                $"No matching event from entity '{t.EntityId}' [cid: {t.Cid}] within the command timeout (akka.fcqrs.command-timeout). The aggregate may have returned UnhandledEvent/IgnoreEvent, or the filter never matched."
                            )
                        )
                | r -> return r |> nonNull :?> Event<'Event> // Return the awaited event
            }

        let subscribeForCommand system mediator command =
            subscribeForCommandCore None system mediator command

        let internal subscribeForConditionalCommand expectedVersion system mediator command =
            subscribeForCommandCore (Some expectedVersion) system mediator command

    // Internal types for command subscription actor state -> Made public for Command DU
    type CommandDetails<'Command, 'Event> =
        { EntityRef: IEntityRef<obj>
          Cmd: Command<'Command> // The original Command object
          Filter: ('Event -> bool) }

    /// Represents the message sent to the internal subscription mechanism.
    /// <typeparam name="'Command">The type of the command payload.</typeparam>
    /// <typeparam name="'Event">The type of the expected event payload.</typeparam>
    type Command<'Command, 'Event> =
        | Execute of CommandDetails<'Command, 'Event>
        // Sent only to a subscriber on the caller's node; its filter is a function.
        interface Akka.Actor.INoSerializationVerificationNeeded



type internal ShardFactoryWith<'T, 'TEvent, 'TCommand, 'TState
    when 'T: (static member ApplyEvent: Event<'TEvent> * 'TState -> 'TState)
    and 'T: (static member Init: IActor * string -> EntityFac<obj>)
    and 'TEvent: comparison
    and 'T: (static member Factory: IActor -> (string -> IEntityRef<obj>))
    and 'T: (static member HandleCommand: Command<'TCommand> * 'TState -> EventAction<'TEvent>)> = 'T


type internal ShardFactoryWithEnv<'T, 'TEnv, 'TEvent, 'TCommand, 'TState
    when 'T: (static member ApplyEvent: 'TEnv * Event<'TEvent> * 'TState -> 'TState)
    and 'T: (static member Init: 'TEnv * IActor * string -> EntityFac<obj>)
    and 'T: (static member Factory: 'TEnv * IActor -> (string -> IEntityRef<obj>))
    and 'T: (static member HandleCommand: 'TEnv * Command<'TCommand> * 'TState -> EventAction<'TEvent>)> = 'T

type internal Handler<'Cmd, 'Event> = ('Event -> bool) -> CID -> AggregateId -> 'Cmd -> Async<'Event>

module internal Curry =
    let curry f x y = f (x, y)

    let uncurry f (x, y) = f x y
    let curry3 f x y z = f (x, y, z)

    let uncurry3 f (x, y, z) = f x y z

    // Convert static member to curried function
    let ofStatic3 (staticMember: 'a * 'b * 'c -> 'd) : 'a -> 'b -> 'c -> 'd = fun x y z -> staticMember (x, y, z)


let inline internal commandHandler<'T, 'TEvent, 'TCommand, 'TState when ShardFactoryWith<'T, 'TEvent, 'TCommand, 'TState>>
    actorApi
    (eventFilter: 'TEvent -> bool)
    cid
    actorId
    (command: 'TCommand)

    =
    let shard = 'T.Factory actorApi

    async {
        let! res = actorApi.CreateCommandSubscription shard cid actorId command eventFilter None
        return res.EventDetails
    }


let inline internal commandHandlerWithEnv<'T, 'TEnv, 'TEvent, 'TCommand, 'TState
    when ShardFactoryWithEnv<'T, 'TEnv, 'TEvent, 'TCommand, 'TState>>
    env
    actorApi
    (eventFilter: 'TEvent -> bool)
    cid
    actorId
    (command: 'TCommand)

    =
    let shard = 'T.Factory(env, actorApi)

    async {
        let! res = actorApi.CreateCommandSubscription shard cid actorId command eventFilter None
        return res.EventDetails
    }
