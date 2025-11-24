module  rec FCQRS.Actor

open Akka.Streams
open Akka.Cluster
open Akka.Cluster.Tools.PublishSubscribe
open Akkling
open Microsoft.Extensions.Configuration
open Hocon.Extensions.Configuration
open DynamicConfig
open System.Dynamic
open Akkling.Persistence
open Akka
open Common
open AkklingHelpers
open System
open Microsoft.Extensions.Logging
open AkkaTimeProvider
open FCQRS.Model.Data
open Akkling.Cluster.Sharding
open System.Diagnostics

// ActivitySource for distributed tracing
let private activitySource = new ActivitySource("FCQRS")


[<AutoOpen>]
module internal Internal =

    type State<'InnerState> =
        { Version: Version
          State: 'InnerState }
        interface ISerializable

    type BodyInput<'TEvent when 'TEvent : not null> =
        { 
        Message: obj
        State: obj
        PublishEvent: Event<'TEvent> -> unit
        SendToSagaStarter: Event<'TEvent> -> obj
        Mediator: IActorRef<Publish>
        Log: ILogger }
    let runActor<'TEvent , 'TState when 'TEvent : not struct and 'TEvent : not null>
        (snapshotVersionCount: int64)
        (logger: ILogger)
        (mailbox: Eventsourced<obj>)
        mediator
        (set: State<'TState> -> _)
        (state: State<'TState>)
        (applyNewState: Event<'TEvent> -> 'TState -> 'TState)
        (body: BodyInput<'TEvent> -> _)
        : Effect<obj> =

        let mediatorS = retype mediator

        let publishEvent event =
            SagaStarter.Internal.publishEvent
                logger
                mailbox
                mediator
                event
                (event.CorrelationId |> ValueLens.Value |> ValueLens.Value)

        actor {
            let! msg = mailbox.Receive()

            match msg with
            | PersistentLifecycleEvent _
            | :? Persistence.SaveSnapshotSuccess
            | LifecycleEvent _ -> return! state |> set

            | SnapshotOffer(snapState: obj) -> return! snapState |> unbox<_> |> set
            | :? Command<ContinueOrAbort<'TEvent>> as (cmd) ->
                let (ContinueOrAbort(e: Event<'TEvent>)) = cmd.CommandDetails
                let currentVersion = state.Version |> ValueLens.Value
                let eventVersion = e.Version |> ValueLens.Value

                if currentVersion = eventVersion then
                    publishEvent e
                    return! state |> set
                else
                    let abortedEvent =
                        { EventDetails = AbortedEvent
                          CreationDate = mailbox.System.Scheduler.Now.UtcDateTime
                          Id = Guid.CreateVersion7().ToString() |> ValueLens.CreateAsResult |> Result.value
                          Sender = mailbox.Self.Path.Name |> ValueLens.CreateAsResult |> Result.value |> Some
                          CorrelationId = e.CorrelationId
                          Version = state.Version
                          Metadata = e.Metadata }
                    SagaStarter.Internal.publishEvent
                        logger
                        mailbox
                        mediator
                        abortedEvent
                        (e.CorrelationId |> ValueLens.Value |> ValueLens.Value)

                    return! state |> set

            // actor level events will come here
            | Deferred mailbox (:? Common.Event<'TEvent> as event) ->
                let state = applyNewState event (state.State)
                publishEvent event

                let newState =
                    {   Version = event.Version
                        State = state }

                return! newState |> set

            | Persisted mailbox (:? Common.Event<'TEvent> as event) ->
                let versionN = event.Version |> ValueLens.Value
                let eventCid = event.CorrelationId |> ValueLens.Value |> ValueLens.Value
                let eventType =
                    match box event.EventDetails with
                    | null -> "null"
                    | details -> sprintf "%A" details
                let actorName = mailbox.Self.Path.Name

                // Try to parse CID as W3C traceparent to recover trace context
                let activity =
                    let mutable parentContext = Unchecked.defaultof<ActivityContext>
                    if ActivityContext.TryParse(eventCid, null, &parentContext) then
                        activitySource.StartActivity($"Event:{eventType}", ActivityKind.Internal, parentContext)
                    else
                        // CID is not a traceparent - no tracing for this event
                        null
                match activity with
                | null -> ()
                | act ->
                    act.SetTag("cid", eventCid) |> ignore
                    act.SetTag("actor", actorName) |> ignore
                    act.SetTag("event.type", eventType) |> ignore
                    act.SetTag("version", versionN) |> ignore

                let innerState = applyNewState event state.State
                publishEvent event

                match activity with
                | null -> ()
                | act -> act.Dispose()

                let newState =
                    {   Version = event.Version
                        State = innerState }

                let state = newState

                if versionN > 0L && versionN % snapshotVersionCount = 0L then
                    return! state |> set <@> SaveSnapshot state
                else
                    return! state |> set

            | Recovering mailbox (:? Common.Event<'TEvent> as event) ->
                let state = applyNewState event state.State

                let newState =
                    {   Version = event.Version
                        State = state }

                return! newState |> set
            | _ ->
                let starter = SagaStarter.Internal.toSendMessage mediatorS mailbox.Self

                let bodyInput =
                    {   Message = msg
                        State = state
                        PublishEvent = publishEvent
                        SendToSagaStarter = starter
                        Mediator = mediator
                        Log = logger }

                return! body bodyInput
        }

    let rec handleEffect effect state  (mailbox: Eventsourced<obj>) toEvent nextVersion bodyInput set = actor {
         match effect with
            | PersistEvent event ->
                let nextVersion: Version =
                    (state.Version |> ValueLens.Value) + 1L |> ValueLens.TryCreate |> Result.value
                return! event |> toEvent nextVersion |> bodyInput.SendToSagaStarter |> Persist
                
            | DeferEvent event ->
                return! seq { event |> toEvent state.Version |> bodyInput.SendToSagaStarter } |> Defer
            | PublishEvent event ->
                event |> bodyInput.PublishEvent |> ignore
                return set state
            | IgnoreEvent -> return set state
            | StateChangedEvent _
            | UnhandledEvent -> return Unhandled
            | Stash effect ->
                mailbox.Stash()
                return! handleEffect effect state mailbox toEvent nextVersion bodyInput set
            | Unstash effect ->
                mailbox.Unstash()
                return! handleEffect effect state mailbox toEvent nextVersion bodyInput set
            | UnstashAll effect ->
                mailbox.UnstashAll()
                return! handleEffect effect state mailbox toEvent nextVersion bodyInput set
        }

    let actorProp
        (config: IConfiguration)
        (loggerFactory: ILoggerFactory)
        handleCommand
        apply
        (initialState: 'State)
        (name: string)
        toEvent
        (mediator: IActorRef<Publish>)
        (mailbox: Eventsourced<obj>)
        =
        let logger = loggerFactory.CreateLogger name

        let snapshotVersionCount =
            let s: string | null = config["config:akka:persistence:snapshot-version-count"]

            match s |> System.Int32.TryParse with
            | true, v -> v
            | _ -> 30

        let rec set (state: State<'State>) =
            let body (bodyInput: BodyInput<'Event>) =
                let msg = bodyInput.Message

                actor {
                    match msg, state with
                    | :? Persistence.RecoveryCompleted, _ -> return! state |> set
                    | :? (Common.Command<'Command>) as cmd, _ ->
                        let cmdCid = cmd.CorrelationId |> ValueLens.Value |> ValueLens.Value
                        let cmdType =
                            match box cmd.CommandDetails with
                            | null -> "null"
                            | details -> sprintf "%A" details
                        let actorName = mailbox.Self.Path.Name

                        // Try to parse CID as W3C traceparent to recover trace context
                        let activity =
                            let mutable parentContext = Unchecked.defaultof<ActivityContext>
                            if ActivityContext.TryParse(cmdCid, null, &parentContext) then
                                activitySource.StartActivity($"Command:{cmdType}", ActivityKind.Internal, parentContext)
                            else
                                // CID is not a traceparent - no tracing for this command
                                null

                        match activity with
                        | null -> ()
                        | act ->
                            act.SetTag("cid", cmdCid) |> ignore
                            act.SetTag("actor", actorName) |> ignore
                            act.SetTag("command.type", cmdType) |> ignore
                            act.SetTag("version", state.Version |> ValueLens.Value) |> ignore

                        // Keep original CID for pub/sub routing - trace hierarchy is via TraceId, not CID propagation
                        let toEvent =
                            toEvent
                                cmd.Id
                                cmd.CorrelationId
                                (mailbox.Self.Path.Name |> ValueLens.CreateAsResult |> Result.value |> Some)
                                cmd.Metadata
                        let effect = handleCommand cmd state.State

                        match activity with
                        | null -> ()
                        | act ->
                            act.SetTag("effect", effect.GetType().Name) |> ignore
                            act.Dispose()

                        return! handleEffect effect state mailbox toEvent state.Version bodyInput set
                    | _ ->
                        bodyInput.Log.LogWarning("Unhandled message: {msg}", msg)
                        return Unhandled
                }

            runActor snapshotVersionCount logger mailbox mediator set state (apply: Event<_> -> 'State -> 'State) body

        let initialState =
            { 
                Version = 0L |> ValueLens.TryCreate |> Result.value
                State = initialState }

        set initialState





    let createCommandSubscription (actorApi: IActor) factory (cid: CID) (id: AggregateId) command filter (metadata: Map<string, string> option) =
        let actor = factory (id |> ValueLens.Value |> ValueLens.Value)

        let commonCommand: Command<_> =
            {
                CommandDetails = command
                Id = Guid.CreateVersion7().ToString() |> ValueLens.CreateAsResult |> Result.value
                CreationDate = actorApi.System.Scheduler.Now.UtcDateTime
                CorrelationId = cid
                Sender = None
                Metadata = metadata |> Option.defaultValue Map.empty }

        let e =
            { 
                Cmd = commonCommand
                EntityRef = actor
                Filter = filter }

        let ex = Execute e
        ex |> actorApi.SubscribeForCommand

    let init config loggerFactory initialState name toEvent (actorApi: IActor) handleCommand apply =
        AkklingHelpers.Internal.entityFactoryFor actorApi.System shardResolver name
        <| propsPersist (actorProp config loggerFactory handleCommand apply initialState name toEvent (typed actorApi.Mediator))
        <| false

/// Custom configuration provider for in-memory HOCON strings
type HoconStringConfigurationProvider(hoconString: string) =
    inherit ConfigurationProvider()

    override this.Load() =
        use memoryStream = new IO.MemoryStream(Text.Encoding.UTF8.GetBytes hoconString)
        let hoconSource = new HoconConfigurationSource()
        let hoconProvider = new HoconConfigurationProvider(hoconSource)
        hoconProvider.Load memoryStream

        // Use reflection to get the Data property from the provider
        let providerType = hoconProvider.GetType()
        let dataProperty =
            match providerType.GetProperty("Data", Reflection.BindingFlags.Instance ||| Reflection.BindingFlags.NonPublic ||| Reflection.BindingFlags.Public) with
            | null -> failwith "Could not find Data property on HoconConfigurationProvider"
            | prop -> prop
        let providerData =
            match dataProperty.GetValue(hoconProvider) with
            | null -> failwith "HoconConfigurationProvider Data is null"
            | data -> data :?> System.Collections.Generic.IDictionary<string, string>

        // Copy data from hoconProvider to this provider
        for kvp in providerData do
            this.Data.[kvp.Key] <- kvp.Value

/// Custom configuration source for in-memory HOCON strings
type HoconStringConfigurationSource(hoconString: string) =
    interface IConfigurationSource with
        member _.Build(builder: IConfigurationBuilder) =
            upcast new HoconStringConfigurationProvider(hoconString)

/// Represents the type of database connection
type DBType =
    /// SQLite using Microsoft.Data.Sqlite provider
    | Sqlite
    /// Microsoft SQL Server 2012
    | SqlServer2012
    /// Microsoft SQL Server 2014
    | SqlServer2014
    /// Microsoft SQL Server 2016
    | SqlServer2016
    /// Microsoft SQL Server 2017
    | SqlServer2017
    /// Microsoft SQL Server 2019
    | SqlServer2019
    /// Microsoft SQL Server 2022
    | SqlServer2022
    /// PostgreSQL 9.3+
    | PostgreSQL
    /// PostgreSQL 15+
    | PostgreSQL15
    /// MySQL using MySqlConnector
    | MySql
    /// Oracle Database
    | Oracle
    /// Firebird
    | Firebird
    /// IBM DB2
    | DB2

/// Represents a database connection configuration
type Connection =
    { ConnectionString: Model.Data.ShortString
      DBType: DBType }

let api (config: IConfiguration) (loggerFactory: ILoggerFactory) (connection: Connection option) (clusterName: Model.Data.ShortString) =
    let mergedConfig =
        match connection with
        | Some conn ->
            // Read embedded hocon resource
            let assembly = Reflection.Assembly.GetExecutingAssembly()
            let resourceName = "FCQRS.default.hocon"
            use stream =
                match assembly.GetManifestResourceStream resourceName with
                | null -> failwith "Could not find embedded resource: FCQRS.default.hocon"
                | s -> s
            use reader = new IO.StreamReader(stream)
            let hoconTemplate = reader.ReadToEnd()

            // Replace placeholders with Linq2Db provider names
            let dbTypeString =
                match conn.DBType with
                | Sqlite -> "SQLite.MS"
                | SqlServer2012 -> "SqlServer.2012"
                | SqlServer2014 -> "SqlServer.2014"
                | SqlServer2016 -> "SqlServer.2016"
                | SqlServer2017 -> "SqlServer.2017"
                | SqlServer2019 -> "SqlServer.2019"
                | SqlServer2022 -> "SqlServer.2022"
                | PostgreSQL -> "PostgreSQL.9.3"
                | PostgreSQL15 -> "PostgreSQL.15"
                | MySql -> "MySqlConnector"
                | Oracle -> "Oracle.Managed"
                | Firebird -> "Firebird"
                | DB2 -> "DB2"

            let connectionStringValue = conn.ConnectionString |> ValueLens.Value

            let hoconString =
                hoconTemplate
                    .Replace("${connection-string}", connectionStringValue)
                    .Replace("${db-type}", dbTypeString)

            // Create new configuration builder with hocon string merged with existing config
            // Add embedded HOCON first, then user config to allow overrides
            let configBuilder = ConfigurationBuilder()
            configBuilder.Add(HoconStringConfigurationSource(hoconString)) |> ignore
            configBuilder.AddConfiguration config |> ignore
            configBuilder.Build() :> IConfiguration
        | None ->
            config

    let akkaConfig: ExpandoObject =
        unbox<_> (mergedConfig.GetSectionAsDynamic("config:akka"))

    let akkaConfiguration = Configuration.ConfigurationFactory.FromObject akkaConfig

    let clusterNameValue = clusterName |> ValueLens.Value
    let system = System.create clusterNameValue akkaConfiguration

    Cluster.Get(system).SelfAddress |> Cluster.Get(system).Join

    let mediator = DistributedPubSub.Get(system).Mediator

    let mat = ActorMaterializer.Create system

    let subscribeForCommand command =
        subscribeForCommand system (typed mediator) command

    { new IActor with
        /// <summary>
        /// Gets the mediator actor reference which serves as the central hub for publish/subscribe messaging.
        /// This mediator is used to broadcast and route messages across the cluster, enabling distributed coordination.
        /// </summary>
        member _.Mediator = mediator
        
        /// <summary>
        /// Gets the actor materializer instance used to run Akka Streams.
        /// This materializer is essential for processing streaming data and handling asynchronous event flows within actors.
        /// </summary>
        member _.Materializer = mat
        
        /// <summary>
        /// Gets the underlying actor system that manages actor lifecycles, message dispatch, and cluster membership.
        /// </summary>
        member _.System = system
        
        /// <summary>
        /// Provides a time provider based on the system's scheduler, useful for timestamping messages and scheduling tasks.
        /// </summary>
        member _.TimeProvider = new AkkaTimeProvider(system)
        
        /// <summary>
        /// Gets the logger factory used to create loggers for detailed diagnostics and operational logging.
        /// This factory aids in capturing context-rich log entries across the actor system.
        /// </summary>
        member _.LoggerFactory = loggerFactory

        /// <summary>
        /// Gets the configuration used by the actor system.
        /// This configuration provides access to application settings and Akka configuration.
        /// </summary>
        member _.Configuration = config

        /// <summary>
        /// Subscribes for a command by instantiating a temporary actor that listens for a specific command.
        /// This dynamic subscription mechanism allows decoupled command handling by setting up an independent listener.
        /// </summary>
        /// <remarks>
        /// Under the hood, this method creates an actor that awaits a subscription acknowledgment before forwarding the command.
        /// Such a pattern is common in systems that separate command dispatch from command processing.
        /// </remarks>
        member _.SubscribeForCommand command = subscribeForCommand command
        
        /// <summary>
        /// Stops the actor system gracefully by terminating all actors and releasing associated resources.
        /// This method should be invoked during system shutdown to ensure a clean termination.
        /// </summary>
        member _.Stop() = system.Terminate()
        
        /// <summary>
        /// Creates a command subscription by using a provided factory to generate an entity reference,
        /// while applying a filter predicate to ensure only relevant commands are processed.
        /// </summary>
        /// <remarks>
        /// This approach encapsulates the wiring required to connect a command source with its handler,
        /// including setting up filters, delay mechanisms, and processing pipelines.
        /// </remarks>
        member this.CreateCommandSubscription factory cid id command filter metadata =
            createCommandSubscription this factory cid id command filter metadata
        
        /// <summary>
        /// Initializes a persistent actor with the defined configuration, initial state, unique name, command handler, and event applier.
        /// The actor leverages event sourcing to persist its state, enabling state recovery after failures or restarts.
        /// </summary>
        /// <remarks>
        /// This method orchestrates the conversion of incoming commands to events, then applies those events to update the state.
        /// Such a mechanism is key to implementing CQRS patterns, where events represent the source of truth for state changes.
        /// </remarks>
        /// <example>
        /// <code lang="fsharp">
        /// // Example: Initialize an actor that handles user management.
        /// // The command handler maps commands (e.g. Login, Register) to domain events,
        /// // while the event applier incorporates these events into the current state.
        /// let userActor = 
        ///     actorApi.InitializeActor(
        ///         config, 
        ///         userInitialState, 
        ///         "UserActor", 
        ///         userCommandHandler, 
        ///         userEventApplier)
        /// </code>
        /// </example>
        member this.InitializeActor initialState name handleCommand apply =
            let toEvent mid ci sender metadata version event = toEvent system.Scheduler (Some mid) ci sender version metadata event
            init config loggerFactory initialState name toEvent this handleCommand apply
        
        /// <summary>
        /// Initializes a saga to manage a long-running business process across multiple actors.
        /// The saga coordinates state transitions, side effects, and inter-actor communications.
        /// </summary>
        /// <remarks>
        /// Sagas are used to implement complex workflows that require orchestrating multiple steps,
        /// and this method bootstraps such a saga using event sourcing principles.
        /// </remarks>
        /// <example>
        /// <code lang="fsharp">
        /// // Example: Initialize an order processing saga.
        /// let orderSaga = 
        ///     actorApi.InitializeSaga(
        ///         config, 
        ///         initialOrderSagaState, 
        ///         orderEventHandler, 
        ///         orderSideEffects, 
        ///         orderStateApplier, 
        ///         "OrderSaga")
        /// </code>
        /// </example>
        member this.InitializeSaga
            (initialState: SagaState<'SagaState, 'State>)
            handleEvent
            applySideEffects
            apply
            name : EntityFac<obj> =
            Saga.init this initialState handleEvent applySideEffects apply name
        
        /// <summary>
        /// Initializes the saga starter actor with specific rules for saga initiation.
        /// The saga starter listens for incoming messages and triggers saga workflows based on configured rules.
        /// </summary>
        /// <remarks>
        /// The rules determine which messages should launch a saga and how entities associated with the saga are created.
        /// This enables dynamic saga management in a distributed environment.
        /// </remarks>
        member _.InitializeSagaStarter (rules: (obj -> list<(string -> IEntityRef<obj>) * PrefixConversion * obj>)) : unit =
            SagaStarter.Internal.init system mediator rules
    }
