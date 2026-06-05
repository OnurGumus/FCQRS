/// End-to-end smoke test for the FCQRS.FSharp facade against a real (SQLite-backed)
/// actor system. Exercises: Fcqrs.actor / connect / newCid / aggregateId,
/// Fcqrs.aggregate + the typed handle .Send, Fcqrs.saga + toOriginator +
/// wireSagaStarters, and Fcqrs.projection + the ISubscribe stream.
///
/// The saga assertion uses a timeout, so a facade that registers a saga which
/// never fires (e.g. a miswired originator-event type) fails cleanly instead of
/// hanging.
module FacadeTests

open System
open System.IO
open Expecto
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging.Abstractions
open FCQRS.Common
open FCQRS.Model.Data
open FCQRS.FSharp

/// A trivial counter aggregate.
module Counter =
    type Command =
        | Increment of int
        | Reset

    type Event =
        | Incremented of int
        | WasReset

    type State = { Total: int }
    let initial = { Total = 0 }

    let decide (cmd: Command<Command>) _state =
        match cmd.CommandDetails with
        | Increment n -> Incremented n |> PersistEvent
        | Reset -> WasReset |> PersistEvent

    let fold (e: Event<Event>) state =
        match e.EventDetails with
        | Incremented n -> { state with Total = state.Total + n }
        | WasReset -> { Total = 0 }

/// A saga that resets the counter whenever it sees a "big" (>= 100) increment —
/// proving the saga reacts to an originator event and drives a command back into
/// the originator via toOriginator.
module AutoReset =
    type State =
        | Resetting
        | Finished

    let private handleEvent (evt: obj) (sagaState: SagaState<_, _>) =
        match evt, sagaState.State with
        | :? (Event<Counter.Event>) as e, None ->
            match e.EventDetails with
            | Counter.Incremented n when n >= 100 -> Resetting |> StateChangedEvent
            | _ -> UnhandledEvent
        | :? (Event<Counter.Event>) as e, Some Resetting ->
            match e.EventDetails with
            | Counter.WasReset -> Finished |> StateChangedEvent
            | _ -> UnhandledEvent
        | _ -> UnhandledEvent

    let private applySideEffects counterFactory (sagaState: SagaState<_, _>) _recovering =
        match sagaState.State with
        | Resetting -> Stay, [ toOriginator counterFactory (Counter.Reset :> obj) ]
        | Finished -> StopSaga, []

    let private startsOn (e: Event<Counter.Event>) =
        match e.EventDetails with
        | Counter.Incremented n -> n >= 100
        | _ -> false

    let definition counterFactory =
        { Name = "AutoReset"
          InitialData = ()
          Originator = counterFactory
          HandleEvent = handleEvent
          ApplySideEffects = applySideEffects counterFactory
          StartOn = startsOn }

let private projection (_offset: int64) (ev: obj) : IMessageWithCID list =
    match ev with
    | :? (Event<Counter.Event>) as e -> [ e :> IMessageWithCID ]
    | _ -> []

let private boot () =
    let db = Path.Combine(Path.GetTempPath(), sprintf "fcqrs_facade_%s.db" (Guid.NewGuid().ToString("N")))
    let api =
        Fcqrs.actor (ConfigurationBuilder().Build()) NullLoggerFactory.Instance
            (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite (sprintf "Data Source=%s;" db))) "FacadeSmoke"
    let counter =
        Fcqrs.aggregate api { Name = "Counter"; Initial = Counter.initial; Decide = Counter.decide; Fold = Counter.fold }
    let saga = Fcqrs.saga api (AutoReset.definition counter.Factory)
    Fcqrs.wireSagaStarters api [ saga ]
    let subs = Fcqrs.projection api { LastOffset = 0; Handle = projection }
    counter, subs

let private isWasReset (m: IMessageWithCID) =
    match m with
    | :? (Event<Counter.Event>) as e ->
        match e.EventDetails with
        | Counter.WasReset -> true
        | _ -> false
    | _ -> false

let tests =
    testCase "facade: aggregate handle round-trips a command, and a saga drives a follow-up into the originator"
    <| fun _ ->
        let counter, subs = boot ()

        // 1. the typed handle round-trips command -> event
        let ev =
            counter.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "c1") (Counter.Increment 5)
                (function Counter.Incremented _ -> true | _ -> false)
            |> Async.RunSynchronously
        Expect.equal ev.EventDetails (Counter.Incremented 5) "Send returns the persisted event"

        // 2. a big increment must trigger the saga -> Reset -> WasReset (seen via the projection)
        use sawReset = subs.Subscribe(isWasReset, 1)
        counter.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "big") (Counter.Increment 100)
            (function Counter.Incremented _ -> true | _ -> false)
        |> Async.RunSynchronously
        |> ignore
        Expect.isTrue (sawReset.Task.Wait(TimeSpan.FromSeconds 20.0)) "the saga reset the counter (WasReset observed through the projection)"

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv tests
