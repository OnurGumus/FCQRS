/// C# interoperability helpers for FCQRS
/// Provides simpler APIs for consuming FCQRS from C#
module FCQRS.CSharp

open System
open System.Threading.Tasks
open System.Runtime.CompilerServices
open FCQRS.Model.Data
open FCQRS.Common

/// C# delegate for command handlers - returns Task<Event<TEvent>>
type Handler<'TCmd, 'TEvent when 'TEvent: not null> = delegate of filter: Func<'TEvent, bool> * cid: CID * aggregateId: AggregateId * command: 'TCmd -> Task<Event<'TEvent>>

/// Extension methods for Async to make it easier to use from C#
[<Extension>]
type AsyncExtensions =
    /// Convert Async to Task
    [<Extension>]
    static member AsTask(computation: Async<'T>) : Task<'T> =
        Async.StartAsTask computation

    /// Static helper to convert Async to Task (for C# where extension may not resolve)
    static member ToTask(computation: Async<'T>) : Task<'T> =
        Async.StartAsTask computation

/// Helper methods for creating FCQRS types from C#
type Helpers =
    /// Create a ShortString from a string (throws on failure)
    static member CreateShortString(s: string) : ShortString =
        match ValueLens.TryCreate<ShortString, _, _> s with
        | Ok v -> v
        | Error e -> failwithf "Failed to create ShortString: %A" e

    /// Create a CID from a string (throws on failure)
    static member CreateCID(s: string) : CID =
        let shortString = Helpers.CreateShortString s
        ValueLens.Create shortString

    /// Create a new CID from a GUID v7
    static member NewCID() : CID =
        Guid.CreateVersion7().ToString() |> Helpers.CreateCID

    /// Create an AggregateId from a string (throws on failure)
    static member CreateAggregateId(s: string) : AggregateId =
        let shortString = Helpers.CreateShortString s
        ValueLens.Create shortString

    /// Try to create a ShortString (returns Result instead of throwing)
    static member TryCreateShortString(s: string) : Result<ShortString, ModelError list> =
        ValueLens.TryCreate<ShortString, _, _> s

    /// Try to create a LongString (returns Result instead of throwing)
    static member TryCreateLongString(s: string) : Result<LongString, ModelError list> =
        ValueLens.TryCreate<LongString, _, _> s

    /// Create a LongString from a string (throws on failure)
    static member CreateLongString(s: string) : LongString =
        match ValueLens.TryCreate<LongString, _, _> s with
        | Ok v -> v
        | Error e -> failwithf "Failed to create LongString: %A" e

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
    /// Try to create a ShortString, returns (success, value, errorMessage)
    static member TryCreateShortString(s: string, [<System.Runtime.InteropServices.Out>] result: byref<ShortString>) : bool =
        match ValueLens.TryCreate<ShortString, _, _> s with
        | Ok v -> result <- v; true
        | Error _ -> false

    /// Try to create a LongString, returns (success, value, errorMessage)
    static member TryCreateLongString(s: string, [<System.Runtime.InteropServices.Out>] result: byref<LongString>) : bool =
        match ValueLens.TryCreate<LongString, _, _> s with
        | Ok v -> result <- v; true
        | Error _ -> false

/// C#-friendly factory methods for EventAction
type EventActions =
    /// Create a PersistEvent action (event will be persisted and published)
    static member Persist<'TEvent when 'TEvent : not struct and 'TEvent: not null>(event: 'TEvent) : EventAction<'TEvent> =
        EventAction.PersistEvent event

    /// Create a DeferEvent action (event is returned but not persisted - for errors/rejections)
    static member Defer<'TEvent when 'TEvent : not struct and 'TEvent: not null>(event: 'TEvent) : EventAction<'TEvent> =
        EventAction.DeferEvent event

    /// Create an IgnoreEvent action (command is ignored, no event produced)
    static member Ignore<'TEvent when 'TEvent : not struct and 'TEvent: not null>() : EventAction<'TEvent> =
        EventAction.IgnoreEvent

/// C#-friendly class for defining saga starters (uses class for C# object initializer syntax)
[<AllowNullLiteral>]
type SagaDefinition() =
    /// Factory function to create entity reference from entity ID
    member val Factory: Func<string, Akkling.Cluster.Sharding.IEntityRef<obj>> | null = null with get, set
    /// How to derive saga entity ID from source entity ID
    member val PrefixConversion: PrefixConversion = PrefixConversion None with get, set
    /// The event to send to start the saga
    member val StartingEvent: obj | null = null with get, set

/// C#-friendly factory for PrefixConversion
type PrefixConversions =
    /// Identity conversion - uses originator prefix with saga suffix
    /// This creates saga IDs like: originatorId~Saga~correlationId
    static member Identity = PrefixConversion (Some id)
    /// Custom conversion function
    static member Custom(f: Func<string, string>) = PrefixConversion (Some f.Invoke)

/// C#-friendly Actor API
type ActorApi =
    /// Create the actor system with SQLite connection
    static member Create(
        configuration: Microsoft.Extensions.Configuration.IConfiguration,
        loggerFactory: Microsoft.Extensions.Logging.ILoggerFactory,
        sqliteConnectionString: string,
        clusterName: string) : IActor =
        let connString = Helpers.CreateShortString sqliteConnectionString
        let connection : Actor.Connection = { ConnectionString = connString; DBType = Actor.DBType.Sqlite }
        let name = Helpers.CreateShortString clusterName
        Actor.api configuration loggerFactory (Some connection) name

/// C#-friendly Query API
type QueryApi =
    /// Initialize the query subscription (F# list version)
    static member Init(
        actorApi: IActor,
        lastOffset: int,
        eventHandler: Func<int64, obj, IMessageWithCID list>) : FCQRS.Query.ISubscribe<IMessageWithCID> =
        let handler offset evt = eventHandler.Invoke(offset, evt)
        Query.init actorApi lastOffset handler

    /// Initialize the query subscription (C# IList version - converts to F# list internally)
    static member InitWithList(
        actorApi: IActor,
        lastOffset: int,
        eventHandler: Func<int64, obj, System.Collections.Generic.IList<IMessageWithCID>>) : FCQRS.Query.ISubscribe<IMessageWithCID> =
        let handler offset evt = eventHandler.Invoke(offset, evt) |> List.ofSeq
        Query.init actorApi lastOffset handler

/// Extension methods for IActor
[<Extension>]
type IActorExtensions =
    /// Initialize saga starter with no sagas (for simple scenarios)
    [<Extension>]
    static member InitializeSagaStarterEmpty(actor: IActor) : unit =
        actor.InitializeSagaStarter(fun _ -> [])

    /// Static helper for C# where extension may not resolve
    static member InitSagaStarterEmpty(actor: IActor) : unit =
        actor.InitializeSagaStarter(fun _ -> [])

    /// C#-friendly InitializeSagaStarter that accepts Func returning IList of SagaDefinition
    static member InitSagaStarter(
        actor: IActor,
        eventHandler: Func<obj, System.Collections.Generic.IList<SagaDefinition>>) : unit =
        let handler evt =
            eventHandler.Invoke(evt)
            |> Seq.map (fun def -> ((nonNull def.Factory).Invoke, def.PrefixConversion, nonNull def.StartingEvent))
            |> List.ofSeq
        actor.InitializeSagaStarter(handler)

    /// C#-friendly InitializeActor that accepts Func delegates instead of F# functions
    static member InitActor<'TState, 'TCommand, 'TEvent when 'TEvent : not struct and 'TEvent: not null>(
        actor: IActor,
        initialState: 'TState,
        entityName: string,
        handleCommand: Func<Command<'TCommand>, 'TState, EventAction<'TEvent>>,
        applyEvent: Func<Event<'TEvent>, 'TState, 'TState>) : Akkling.Cluster.Sharding.EntityFac<obj> =
        let cmdHandler cmd state = handleCommand.Invoke(cmd, state)
        let evtApplier evt state = applyEvent.Invoke(evt, state)
        actor.InitializeActor initialState entityName cmdHandler evtApplier

    /// Send a command and wait for the event (C# friendly)
    [<Extension>]
    static member SendCommandAsync<'TEvent, 'TCommand when 'TEvent: not null>(
        actor: IActor,
        entityFactory: Func<string, Akkling.Cluster.Sharding.IEntityRef<obj>>,
        cid: CID,
        aggregateId: AggregateId,
        command: 'TCommand,
        filter: Func<'TEvent, bool>) : Task<Event<'TEvent>> =
        let factory = fun s -> entityFactory.Invoke(s)
        let filterF = fun e -> filter.Invoke(e)
        actor.CreateCommandSubscription factory cid aggregateId command filterF None
        |> Async.StartAsTask

    /// C#-friendly CreateCommandSubscription that returns FSharpAsync (for use with Handler delegate)
    static member CreateCommand<'TEvent, 'TCommand when 'TEvent: not null>(
        actor: IActor,
        entityFactory: Func<string, Akkling.Cluster.Sharding.IEntityRef<obj>>,
        cid: CID,
        aggregateId: AggregateId,
        command: 'TCommand,
        filter: Func<'TEvent, bool>) : Async<Event<'TEvent>> =
        let factory = fun s -> entityFactory.Invoke(s)
        let filterF = fun e -> filter.Invoke(e)
        actor.CreateCommandSubscription factory cid aggregateId command filterF None

/// Extension methods for ISubscribe
[<Extension>]
type ISubscribeExtensions =
    /// C#-friendly Subscribe that accepts Func instead of FSharpFunc
    [<Extension>]
    static member SubscribeFor<'T when 'T :> FCQRS.Model.Data.IMessageWithCID>(
        subs: Query.ISubscribe<'T>,
        filter: Func<'T, bool>,
        take: int) : Query.IAwaitableDisposable =
        subs.Subscribe((fun e -> filter.Invoke(e)), take)

    /// C#-friendly Subscribe that matches by CID only
    [<Extension>]
    static member SubscribeFor<'T when 'T :> FCQRS.Model.Data.IMessageWithCID>(
        subs: Query.ISubscribe<'T>,
        cid: FCQRS.Model.Data.CID,
        take: int) : Query.IAwaitableDisposable =
        subs.Subscribe(cid, take)

    /// C#-friendly Subscribe that matches by CID and additional filter
    [<Extension>]
    static member SubscribeFor<'T when 'T :> FCQRS.Model.Data.IMessageWithCID>(
        subs: Query.ISubscribe<'T>,
        cid: FCQRS.Model.Data.CID,
        filter: Func<'T, bool>,
        take: int) : Query.IAwaitableDisposable =
        subs.Subscribe(cid, (fun e -> filter.Invoke(e)), take)

// =============================================================================
// SAGA C# INTEROP
// =============================================================================

open Akkling.Cluster.Sharding
open Common.SagaBuilder

/// C#-friendly result type for saga side effects (uses class for C# object initializer syntax)
[<AllowNullLiteral>]
type SagaSideEffectResult<'TState>() =
    member val Transition: SagaTransition<'TState> = SagaTransition.Stay with get, set
    member val Commands: System.Collections.Generic.IList<ExecuteCommand> = System.Collections.Generic.List<ExecuteCommand>() with get, set

    /// Convert commands to F# list for internal use
    member this.CommandsList = this.Commands |> List.ofSeq

/// C#-friendly helpers for SagaState
type SagaStates =
    /// Create a new SagaState with updated Data
    static member WithData<'TSagaData, 'TState>(sagaState: SagaState<'TSagaData, 'TState>, newData: 'TSagaData) : SagaState<'TSagaData, 'TState> =
        { sagaState with Data = newData }

    /// Create a new SagaState with updated State
    static member WithState<'TSagaData, 'TState>(sagaState: SagaState<'TSagaData, 'TState>, newState: 'TState) : SagaState<'TSagaData, 'TState> =
        { sagaState with State = newState }

/// C#-friendly factory methods for SagaTransition
type SagaTransitions =
    /// Stay in the current state (no transition)
    static member Stay<'TState>() : SagaTransition<'TState> =
        SagaTransition.Stay

    /// Transition to a new state
    static member NextState<'TState>(newState: 'TState) : SagaTransition<'TState> =
        SagaTransition.NextState newState

    /// Stop the saga (completes the saga lifecycle)
    static member StopSaga<'TState>() : SagaTransition<'TState> =
        SagaTransition.StopSaga

/// C#-friendly factory methods for saga EventAction
type SagaEventActions =
    /// Create a state change event
    static member StateChanged<'TState when 'TState : not null>(newState: 'TState) : EventAction<'TState> =
        EventAction.StateChangedEvent newState

    /// Event was not handled
    static member Unhandled<'TState when 'TState : not null>() : EventAction<'TState> =
        EventAction.UnhandledEvent

    /// Ignore the event
    static member Ignore<'TState when 'TState : not null>() : EventAction<'TState> =
        EventAction.IgnoreEvent

/// C#-friendly saga command targeting
type SagaCommands =
    /// Create a command to send to the originator aggregate
    static member ToOriginator(
        factory: Func<string, IEntityRef<obj>>,
        command: obj) : ExecuteCommand =
        { TargetActor = FactoryAndName { Factory = factory.Invoke; Name = Originator }
          Command = command
          DelayInMs = None }

    /// Create a command to send to a named aggregate
    static member ToAggregate(
        factory: Func<string, IEntityRef<obj>>,
        aggregateId: string,
        command: obj) : ExecuteCommand =
        { TargetActor = FactoryAndName { Factory = factory.Invoke; Name = Name aggregateId }
          Command = command
          DelayInMs = None }

    /// Create a command to send to an actor ref
    static member ToActor(
        actorRef: Akkling.ActorRefs.IActorRef<obj>,
        command: obj) : ExecuteCommand =
        { TargetActor = ActorRef actorRef
          Command = command
          DelayInMs = None }

    /// Create a delayed command
    static member ToOriginatorDelayed(
        factory: Func<string, IEntityRef<obj>>,
        command: obj,
        delayMs: int64,
        taskName: string) : ExecuteCommand =
        { TargetActor = FactoryAndName { Factory = factory.Invoke; Name = Originator }
          Command = command
          DelayInMs = Some (delayMs, taskName) }

/// C#-friendly saga builder
type SagaBuilderCSharp =
    /// Initialize a saga with C# delegates
    /// 'TEvent - The triggering event type (from the originator aggregate)
    /// 'TSagaData - Cross-cutting saga data
    /// 'TSagaState - User-defined saga states
    static member Init<'TEvent, 'TSagaData, 'TSagaState when 'TSagaState : not null and 'TEvent : not null>(
        actorApi: IActor,
        sagaData: 'TSagaData,
        handleEvent: Func<obj, SagaState<'TSagaData, 'TSagaState option>, EventAction<'TSagaState>>,
        applySideEffects: Func<SagaState<'TSagaData, 'TSagaState>, bool, SagaSideEffectResult<'TSagaState>>,
        apply: Func<SagaState<'TSagaData, SagaStateWrapper<'TSagaState, 'TEvent>>, SagaState<'TSagaData, SagaStateWrapper<'TSagaState, 'TEvent>>>,
        originatorFactory: Func<string, IEntityRef<obj>>,
        sagaName: string) : EntityFac<obj> =

        // Convert C# handleEvent to F#
        let fsharpHandleEvent (evt: obj) (state: SagaState<'TSagaData, 'TSagaState option>) : EventAction<'TSagaState> =
            printfn ">>> F# fsharpHandleEvent: evt=%s state.State=%A" (evt.GetType().FullName) state.State
            let result = handleEvent.Invoke(evt, state)
            printfn ">>> F# fsharpHandleEvent result: %A" result
            result

        // Convert C# applySideEffects to F#
        let fsharpApplySideEffects (state: SagaState<'TSagaData, 'TSagaState>) (recovering: bool) : SagaTransition<'TSagaState> * ExecuteCommand list =
            let result = applySideEffects.Invoke(state, recovering)
            (result.Transition, result.CommandsList)

        // Convert C# apply to F#
        let fsharpApply (state: SagaState<'TSagaData, SagaStateWrapper<'TSagaState, 'TEvent>>) =
            apply.Invoke(state)

        // Convert C# factory to F#
        let fsharpFactory = fun (s: string) -> originatorFactory.Invoke(s)

        SagaBuilder.init
            actorApi
            sagaData
            fsharpHandleEvent
            fsharpApplySideEffects
            fsharpApply
            fsharpFactory
            sagaName

    /// Initialize a saga with simplified apply - just takes data and unwrapped state, returns new data
    static member Init<'TEvent, 'TSagaData, 'TSagaState when 'TSagaState : not null and 'TEvent : not null>(
        actorApi: IActor,
        sagaData: 'TSagaData,
        handleEvent: Func<obj, SagaState<'TSagaData, 'TSagaState option>, EventAction<'TSagaState>>,
        applySideEffects: Func<SagaState<'TSagaData, 'TSagaState>, bool, SagaSideEffectResult<'TSagaState>>,
        apply: Func<'TSagaData, 'TSagaState, 'TSagaData>,
        originatorFactory: Func<string, IEntityRef<obj>>,
        sagaName: string) : EntityFac<obj> =

        // Wrap the simplified apply into the full signature
        let fullApply (sagaState: SagaState<'TSagaData, SagaStateWrapper<'TSagaState, 'TEvent>>) =
            match sagaState.State with
            | SagaStateWrapper.UserDefined userState ->
                let newData = apply.Invoke(sagaState.Data, userState)
                { sagaState with Data = newData }
            | _ -> sagaState

        SagaBuilderCSharp.Init<'TEvent, 'TSagaData, 'TSagaState>(
            actorApi, sagaData, handleEvent, applySideEffects,
            Func<_, _>(fullApply), originatorFactory, sagaName)

    /// Initialize a saga without apply (no data updates based on state)
    static member Init<'TEvent, 'TSagaData, 'TSagaState when 'TSagaState : not null and 'TEvent : not null>(
        actorApi: IActor,
        sagaData: 'TSagaData,
        handleEvent: Func<obj, SagaState<'TSagaData, 'TSagaState option>, EventAction<'TSagaState>>,
        applySideEffects: Func<SagaState<'TSagaData, 'TSagaState>, bool, SagaSideEffectResult<'TSagaState>>,
        originatorFactory: Func<string, IEntityRef<obj>>,
        sagaName: string) : EntityFac<obj> =

        SagaBuilderCSharp.Init<'TEvent, 'TSagaData, 'TSagaState>(
            actorApi, sagaData, handleEvent, applySideEffects,
            Func<_, _, _>(fun data _ -> data), originatorFactory, sagaName)

    /// Initialize a saga with simplified signatures - no FSharpOption, typed events
    /// handleEvent: (event, data, currentState) -> EventAction
    /// applySideEffects: (data, state, recovering) -> SagaSideEffectResult
    static member InitSimple<'TEvent, 'TSagaData, 'TSagaState when 'TSagaState : not null and 'TEvent : not null>(
        actorApi: IActor,
        sagaData: 'TSagaData,
        handleEvent: Func<Event<'TEvent>, 'TSagaData, 'TSagaState, EventAction<'TSagaState>>,
        applySideEffects: Func<'TSagaData, 'TSagaState, bool, SagaSideEffectResult<'TSagaState>>,
        apply: Func<'TSagaData, 'TSagaState, 'TSagaData>,
        originatorFactory: Func<string, IEntityRef<obj>>,
        sagaName: string) : EntityFac<obj> =

        // Wrap simplified handleEvent - unwrap FSharpOption and cast event
        let wrappedHandleEvent (evt: obj) (sagaState: SagaState<'TSagaData, 'TSagaState option>) =
            match evt with
            | :? Event<'TEvent> as typedEvent ->
                let currentState = sagaState.State |> Option.defaultValue Unchecked.defaultof<'TSagaState>
                handleEvent.Invoke(typedEvent, sagaState.Data, currentState)
            | _ -> EventAction.UnhandledEvent

        // Wrap simplified applySideEffects
        let wrappedApplySideEffects (sagaState: SagaState<'TSagaData, 'TSagaState>) (recovering: bool) =
            applySideEffects.Invoke(sagaState.Data, sagaState.State, recovering)

        SagaBuilderCSharp.Init<'TEvent, 'TSagaData, 'TSagaState>(
            actorApi, sagaData,
            Func<_, _, _>(wrappedHandleEvent),
            Func<_, _, _>(wrappedApplySideEffects),
            apply, originatorFactory, sagaName)

    /// Initialize a saga with simplified signatures and no apply
    static member InitSimple<'TEvent, 'TSagaData, 'TSagaState when 'TSagaState : not null and 'TEvent : not null>(
        actorApi: IActor,
        sagaData: 'TSagaData,
        handleEvent: Func<Event<'TEvent>, 'TSagaData, 'TSagaState, EventAction<'TSagaState>>,
        applySideEffects: Func<'TSagaData, 'TSagaState, bool, SagaSideEffectResult<'TSagaState>>,
        originatorFactory: Func<string, IEntityRef<obj>>,
        sagaName: string) : EntityFac<obj> =

        SagaBuilderCSharp.InitSimple<'TEvent, 'TSagaData, 'TSagaState>(
            actorApi, sagaData, handleEvent, applySideEffects,
            Func<_, _, _>(fun data _ -> data), originatorFactory, sagaName)

    /// Get the factory for a saga
    static member Factory(
        actorApi: IActor,
        sagaEntityFac: EntityFac<obj>,
        entityId: string) : IEntityRef<obj> =
        sagaEntityFac.RefFor DEFAULT_SHARD entityId
