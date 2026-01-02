/// C# interoperability helpers for FCQRS
/// Provides simpler APIs for consuming FCQRS from C#
module FCQRS.CSharp

open System
open System.Threading.Tasks
open System.Runtime.CompilerServices
open FCQRS.Model.Data
open FCQRS.Common

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

/// C#-friendly record for defining saga starters
[<Struct>]
type SagaDefinition = {
    /// Factory function to create entity reference from entity ID
    Factory: Func<string, Akkling.Cluster.Sharding.IEntityRef<obj>>
    /// How to derive saga entity ID from source entity ID
    PrefixConversion: PrefixConversion
    /// The event to send to start the saga
    StartingEvent: obj
}

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
            |> Seq.map (fun def -> (def.Factory.Invoke, def.PrefixConversion, def.StartingEvent))
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
    static member SubscribeFor<'T>(
        subs: Query.ISubscribe<'T>,
        filter: Func<'T, bool>,
        take: int) : Query.IAwaitableDisposable =
        subs.Subscribe((fun e -> filter.Invoke(e)), take)
