module UnionCommandTests

open System
open System.IO
open System.Threading
open Expecto
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging.Abstractions
open FCQRS.Common
open FCQRS.FSharp
open FCQRS.Model.Data

type Tally = { Amount: int }
type Reset = { Reason: string }

// The shape the C# compiler gives `union CounterCommand(Tally, Reset)`: a struct marked with
// UnionAttribute, one constructor per case, and the active case in Value.
[<System.Runtime.CompilerServices.Union; Struct>]
type CounterCommand =
    val Value: obj
    new(tally: Tally) = { Value = box tally }
    new(reset: Reset) = { Value = box reset }

type CounterEvent = Tallied of int

// A command record whose field is called Value. It is not a union.
type Amount = { Value: decimal }
type Raised = { By: int }

[<System.Runtime.CompilerServices.Union; Struct>]
type LevelEvent =
    val Value: obj
    new(raised: Raised) = { Value = box raised }

type Watching = { Level: int }
type Settled = { Final: int }

[<System.Runtime.CompilerServices.Union; Struct>]
type WatchState =
    val Value: obj
    new(watching: Watching) = { Value = box watching }
    new(settled: Settled) = { Value = box settled }

// A generic case: its full name embeds the assembly version of its type argument.
type Boxed<'T> = { Item: 'T }

type Count = { Total: int }

[<System.Runtime.CompilerServices.Union; Struct>]
type BoxEvent =
    val Value: obj
    new(boxed: Boxed<int>) = { Value = box boxed }
    new(count: Count) = { Value = box count }

type BoxCommand =
    | Store of int
    | Total

type private CapturingLogger(category: string, sink: Collections.Concurrent.ConcurrentQueue<string>) =
    interface Microsoft.Extensions.Logging.ILogger with
        member _.BeginScope<'TState when 'TState: not null>(_state: 'TState) : IDisposable | null = null
        member _.IsEnabled(_level) = true
        member _.Log<'TState>(_level, _eventId, state: 'TState, error: exn | null, formatter: Func<'TState, exn | null, string>) =
            sink.Enqueue(category + " | " + formatter.Invoke(state, error))

type private CapturingLoggerFactory(sink: Collections.Concurrent.ConcurrentQueue<string>) =
    interface Microsoft.Extensions.Logging.ILoggerFactory with
        member _.CreateLogger(category: string) = CapturingLogger(category, sink) :> Microsoft.Extensions.Logging.ILogger
        member _.AddProvider(_provider) = ()
        member _.Dispose() = ()

let private caseSentAsItsOwnType =
    testCase "commands: a C# union case sent as its own type reaches the aggregate as the union"
    <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), $"fcqrs_union_commands_{Guid.NewGuid():N}.db")
        let api =
            Fcqrs.actor (ConfigurationBuilder().Build()) NullLoggerFactory.Instance
                (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={db};")) "UnionCommands"
        try
            let counters =
                Fcqrs.aggregate api
                    { Name = "UnionCounter"
                      Initial = 0
                      Decide =
                        fun (command: Command<CounterCommand>) _ ->
                            match command.CommandDetails.Value with
                            | :? Tally as tally -> PersistEvent(Tallied tally.Amount)
                            | _ -> IgnoreEvent
                      Fold = fun (event: Event<CounterEvent>) state -> let (Tallied n) = event.EventDetails in state + n
                      Snapshots = NoSnapshots
                      Passivation = PassivationPolicy.Default }
            Fcqrs.wireSagaStarters api []

            // Listen where the aggregate publishes events for this correlation ID.
            let cid = Fcqrs.newCid ()
            let published = Collections.Concurrent.ConcurrentQueue<obj>()
            use subscribed = new ManualResetEventSlim(false)
            let probe =
                Akkling.Spawn.spawn api.System "union-topic-probe" (Akkling.Props.props (fun (mailbox: Akkling.Actors.Actor<obj>) ->
                    let rec receive () =
                        Akkling.ComputationExpressions.actor {
                            let! message = mailbox.Receive()
                            match message with
                            | :? Akka.Cluster.Tools.PublishSubscribe.SubscribeAck -> subscribed.Set()
                            | :? Akkling.Actors.LifecycleEvent -> ()
                            | other -> published.Enqueue other
                            return! receive ()
                        }
                    receive ()))
                |> Akkling.ActorRefs.untyped
            let topic = "counter~" + (cid |> ValueLens.Value |> ValueLens.Value)
            Akka.Cluster.Tools.PublishSubscribe.DistributedPubSub.Get(api.System).Mediator.Tell(
                Akka.Cluster.Tools.PublishSubscribe.Subscribe(topic, probe), probe)
            Expect.isTrue (subscribed.Wait(TimeSpan.FromSeconds 5.0)) "the probe listens on the correlation topic"

            // What a saga's object-typed command helper sends for a bare case: Command<Tally>.
            let command: Command<Tally> =
                { CommandDetails = { Amount = 5 }
                  CreationDate = DateTime.UtcNow
                  Id = Guid.CreateVersion7().ToString() |> ValueLens.CreateAsResult |> Result.value
                  Sender = None
                  CorrelationId = cid
                  Metadata = Map.empty }
            (counters.Factory "counter").Tell(command, Akka.Actor.ActorRefs.NoSender)

            let deadline = DateTime.UtcNow.AddSeconds 10.0
            let tallied () =
                published |> Seq.exists (function
                    | :? Event<CounterEvent> as event -> event.EventDetails = Tallied 5
                    | _ -> false)
            while not (tallied ()) && DateTime.UtcNow < deadline do
                Thread.Sleep 50
            Expect.isTrue (tallied ()) "the aggregate handled the case as its command union and stored Tallied 5"
        finally
            api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore
            for path in [ db; db + "-wal"; db + "-shm" ] do
                if File.Exists path then File.Delete path

let private namedByActiveCase =
    testCase "telemetry: a C# union is named and rendered by its active case"
    <| fun _ ->
        let spans = Collections.Concurrent.ConcurrentQueue<string>()
        use listener = new Diagnostics.ActivityListener()
        listener.ShouldListenTo <- fun source -> Array.contains source.Name Telemetry.AllActivitySources
        listener.Sample <-
            Diagnostics.SampleActivity<Diagnostics.ActivityContext>(fun _ ->
                Diagnostics.ActivitySamplingResult.AllDataAndRecorded)
        listener.ActivityStopped <- fun activity -> spans.Enqueue activity.OperationName
        Diagnostics.ActivitySource.AddActivityListener listener

        let logs = Collections.Concurrent.ConcurrentQueue<string>()
        let db = Path.Combine(Path.GetTempPath(), $"fcqrs_union_names_{Guid.NewGuid():N}.db")
        let api =
            Fcqrs.actor (ConfigurationBuilder().Build()) (new CapturingLoggerFactory(logs))
                (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={db};")) "UnionNames"
        try
            let levels =
                Fcqrs.aggregate api
                    { Name = "UnionLevel"
                      Initial = 0
                      Decide = fun (command: Command<Amount>) _ ->
                          PersistEvent(LevelEvent { By = int command.CommandDetails.Value })
                      Fold = fun (event: Event<LevelEvent>) state ->
                          match event.EventDetails.Value with
                          | :? Raised as raised -> state + raised.By
                          | _ -> state
                      Snapshots = NoSnapshots
                      Passivation = PassivationPolicy.Default }
            let settled = Threading.Tasks.TaskCompletionSource<int>(Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
            let watch =
                Fcqrs.saga api
                    { Name = "UnionWatch"
                      InitialData = ()
                      Originator = levels.Factory
                      HandleEvent = fun message saga ->
                          match message, saga.State with
                          | (:? Event<LevelEvent> as event), None ->
                              match event.EventDetails.Value with
                              | :? Raised as raised -> StateChangedEvent(WatchState { Level = raised.By })
                              | _ -> UnhandledEvent
                          | _ -> UnhandledEvent
                      ApplySideEffects = fun saga _ ->
                          match saga.State.Value with
                          | :? Watching as watching -> NextState(WatchState { Final = watching.Level }), []
                          | :? Settled as final ->
                              settled.TrySetResult final.Final |> ignore
                              StopSaga, []
                          | _ -> StopSaga, []
                      StartOn = fun (_: Event<LevelEvent>) -> true
                      Snapshots = NoSnapshots }
            Fcqrs.wireSagaStarters api [ watch ]

            levels.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "level") { Value = 3m } (fun _ -> true)
            |> Async.RunSynchronously
            |> ignore
            Expect.isTrue (settled.Task.Wait(TimeSpan.FromSeconds 15.0)) "the saga moved through both union states"
            let deadline = DateTime.UtcNow.AddSeconds 10.0
            while not (Seq.contains "Saga:Settled" spans) && DateTime.UtcNow < deadline do
                Thread.Sleep 50

            // Span names carry the active case, never the union's own type or a Value field's type.
            for name in [ "Command:Amount"; "Event:Raised"; "Saga:Watching"; "Saga:Settled" ] do
                Expect.contains spans name $"a span is named {name}"
            for name in [ "Command:Decimal"; "Event:LevelEvent"; "Saga:WatchState" ] do
                Expect.isFalse (Seq.contains name spans) $"no span is named {name}"

            // The flow log names the states and renders the case payloads.
            let logged (text: string) = logs |> Seq.exists (fun line -> line.Contains text)
            Expect.isTrue (logged "changed state to Watching") "the flow log names the first state's case"
            Expect.isTrue (logged "changed state to Settled") "the flow log names the second state's case"
            Expect.isTrue (logged "yielded PersistEvent ({ By = 3 })") "the aggregate's decision shows the event case"
            Expect.isTrue (logged "StateChangedEvent (UserDefined ({ Level = 3 }))") "the saga's decision shows the state case"
        finally
            api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore
            for path in [ db; db + "-wal"; db + "-shm" ] do
                if File.Exists path then File.Delete path

let private genericCaseWithoutVersions =
    testCase "journal: a generic C# union case is stored without assembly versions and recovers"
    <| fun _ ->
        let db = Path.Combine(Path.GetTempPath(), $"fcqrs_union_generic_{Guid.NewGuid():N}.db")
        let start () =
            let api =
                Fcqrs.actor (ConfigurationBuilder().Build()) NullLoggerFactory.Instance
                    (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={db};")) "UnionGenericCase"
            let boxes =
                Fcqrs.aggregate api
                    { Name = "UnionBoxes"
                      Initial = 0
                      Decide =
                        fun (command: Command<BoxCommand>) state ->
                            match command.CommandDetails with
                            | Store n -> PersistEvent(BoxEvent { Item = n })
                            | Total -> DeferEvent(BoxEvent { Total = state })
                      Fold =
                        fun (event: Event<BoxEvent>) state ->
                            match event.EventDetails.Value with
                            | :? Boxed<int> as boxed -> state + boxed.Item
                            | _ -> state
                      Snapshots = NoSnapshots
                      Passivation = PassivationPolicy.Default }
            Fcqrs.wireSagaStarters api []
            api, boxes
        let send (boxes: AggregateHandle<BoxCommand, BoxEvent>) command =
            let reply = Async.RunSynchronously(boxes.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId "box") command (fun _ -> true), 20000)
            match reply.EventDetails.Value with
            | :? Boxed<int> as boxed -> boxed.Item
            | :? Count as count -> count.Total
            | other -> failwithf "unexpected reply %A" other

        let first, boxes = start ()
        try
            Expect.equal (send boxes (Store 5)) 5 "the aggregate stored the generic case"
        finally
            first.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

        use connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};")
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <- "SELECT CAST(message AS TEXT) FROM journal WHERE persistence_id LIKE 'UnionBoxes/%'"
        let stored = command.ExecuteScalar() |> string
        connection.Close()
        Expect.stringContains stored "System.Private.CoreLib]]" "the stored case name names its type argument's assembly"
        Expect.isFalse (stored.Contains "Version=") "the stored case name has no assembly version"

        let second, boxes = start ()
        try
            Expect.equal (send boxes Total) 5 "recovery read the stored case"
        finally
            second.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
            for path in [ db; db + "-wal"; db + "-shm" ] do
                if File.Exists path then File.Delete path

let tests = testList "union commands" [ caseSentAsItsOwnType; namedByActiveCase; genericCaseWithoutVersions ]
