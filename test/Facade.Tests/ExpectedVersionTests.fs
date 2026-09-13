module ExpectedVersionTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging.Abstractions
open FCQRS.Common
open FCQRS.FSharp
open FCQRS.Model.Data

type VersionCommand =
    | Add of int
    | Batch of int * int
    | Inspect
    | PublishInitial
    | DeferredAdd of int
    | Quiet
    | StartAsync of int
    | Hold
    | AddWhenReleased of int
    | Release

type VersionEvent =
    | Added of int
    | Observed of int
    | DeferredAdded of int
    | Held
    | Released

type VersionState = { Total: int; Holding: bool }

let private deadline = TimeSpan.FromSeconds 15.0
let private signal () = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
let private wait (work: Task) = work.WaitAsync(deadline).GetAwaiter().GetResult()
let private result (work: Task<'T>) = work.WaitAsync(deadline).GetAwaiter().GetResult()
let private canceled (work: Task<'T>) message =
    let mutable canceled = false
    try result work |> ignore with :? OperationCanceledException -> canceled <- true
    Expect.isTrue canceled message
    Expect.isTrue work.IsCanceled "the returned task exposes cancellation"
let private version (event: Event<_>) = event.Version |> ValueLens.Value
let private databasePath () = Path.Combine(Path.GetTempPath(), $"fcqrs-expected-version-{Guid.NewGuid():N}.db")
let private deleteDatabase database =
    for path in [ database; database + "-wal"; database + "-shm" ] do
        if File.Exists path then File.Delete path

type private Fixture(?database: string, ?snapshots: SnapshotPolicy, ?commandTimeout: string) =
    let owned = database.IsNone
    let database = defaultArg database (databasePath ())
    let commands = ConcurrentQueue<Command<VersionCommand>>()
    let folded = ConcurrentQueue<Event<VersionEvent>>()
    let runnerEntered = signal ()
    let releaseRunner = signal ()
    let stashed = signal ()
    let mutable runnerCalls = 0
    let configuration =
        ConfigurationBuilder()
            .AddInMemoryCollection(
                [ Collections.Generic.KeyValuePair<string, string | null>(
                      "config:akka:fcqrs:command-timeout", defaultArg commandTimeout "10s") ])
            .Build()
    let api =
        Fcqrs.actor configuration NullLoggerFactory.Instance
            (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={database};Pooling=False"))
            ("ExpectedVersion" + Guid.NewGuid().ToString("N"))
    let definition name =
        { Name = name
          Initial = { Total = 0; Holding = false }
          Decide = fun (command: Command<VersionCommand>) state ->
              commands.Enqueue command
              match command.CommandDetails with
              | Add amount -> PersistEvent(Added amount)
              | Batch(first, second) -> PersistAllEvents [ Added first; Added second ]
              | Inspect -> DeferEvent(Observed state.Total)
              | PublishInitial ->
                  PublishEvent
                      { EventDetails = Observed state.Total
                        CreationDate = command.CreationDate
                        Id = command.Id
                        Sender = None
                        CorrelationId = command.CorrelationId
                        Version = Version.Zero
                        Metadata = command.Metadata }
              | DeferredAdd amount -> DeferEvent(DeferredAdded amount)
              | Quiet -> IgnoreEvent
              | StartAsync amount -> dispatch amount
              | Hold -> PersistEvent Held
              | AddWhenReleased _ when state.Holding ->
                  stashed.TrySetResult() |> ignore
                  Stash IgnoreEvent
              | AddWhenReleased amount -> PersistEvent(Added amount)
              | Release -> UnstashAll(PersistEvent Released)
          Fold = fun (event: Event<VersionEvent>) state ->
              folded.Enqueue event
              match event.EventDetails with
              | Added amount | DeferredAdded amount -> { state with Total = state.Total + amount }
              | Observed _ -> state
              | Held -> { state with Holding = true }
              | Released -> { state with Holding = false }
          Snapshots = defaultArg snapshots NoSnapshots
          Passivation = PassivationPolicy.Never }
    let runner amount = async {
        Interlocked.Increment(&runnerCalls) |> ignore
        runnerEntered.TrySetResult() |> ignore
        do! releaseRunner.Task |> Async.AwaitTask
        return Add amount
    }
    let handle = Fcqrs.aggregateWithEffects api (definition "VersionedCounter") runner
    let other = Fcqrs.aggregateWithEffects api (definition "OtherVersionedCounter") runner
    do Fcqrs.wireSagaStarters api []
    member _.Api = api
    member _.Handle = handle
    member _.Other = other
    member _.Commands = commands.ToArray()
    member _.Folded = folded.ToArray()
    member _.RunnerCalls = Volatile.Read(&runnerCalls)
    member _.RunnerEntered = runnerEntered.Task
    member _.Stashed = stashed.Task
    member _.ReleaseRunner() = releaseRunner.TrySetResult() |> ignore
    member _.Guard(expected, id, command, ?cid, ?filter: (VersionEvent -> bool), ?cancellationToken) =
        Fcqrs.sendIfVersion api handle expected (defaultArg cid (Fcqrs.newCid ())) (Fcqrs.aggregateId id)
            command (defaultArg filter (fun _ -> true))
        |> fun work -> Async.StartAsTask(work, cancellationToken = defaultArg cancellationToken CancellationToken.None)
    member _.Send(id, command, ?cid, ?filter: (VersionEvent -> bool)) =
        handle.Send (defaultArg cid (Fcqrs.newCid ())) (Fcqrs.aggregateId id) command (defaultArg filter (fun _ -> true))
        |> Async.RunSynchronously
    member _.Scalar(sql: string) =
        use connection = new SqliteConnection($"Data Source={database};Pooling=False")
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <- sql
        Convert.ToInt64(command.ExecuteScalar())
    member this.JournalRows = this.Scalar "SELECT COUNT(*) FROM journal"
    interface IDisposable with
        member _.Dispose() =
            releaseRunner.TrySetResult() |> ignore
            wait (api.Stop())
            if owned then deleteDatabase database

let private conflict (task: Task<Event<VersionEvent>>) =
    try
        result task |> ignore
        failtest "The guarded command succeeded instead of returning a version conflict."
    with :? AggregateVersionConflictException as error -> error

let private acceptsAndRejects =
    testCase "expected version: a stale command never reaches decide or the journal"
    <| fun _ ->
        use fixture = new Fixture()
        let first = result (fixture.Guard(0L, "account", Add 3))
        Expect.equal first.EventDetails (Added 3) "version zero accepts the first command"
        Expect.equal (version first) 1L "the first persisted event advances the version"
        let before = fixture.Commands.Length
        let mutable filterCalls = 0
        let error =
            fixture.Guard(0L, "account", Add 100, filter = (fun _ -> filterCalls <- filterCalls + 1; false))
            |> conflict
        Expect.equal error.AggregateId "account" "the conflict identifies the target aggregate"
        Expect.equal error.ExpectedVersion 0L "the required version is preserved"
        Expect.equal error.ActualVersion 1L "the conflict reports the actor's persisted version"
        Expect.equal fixture.Commands.Length before "rejected commands do not invoke decide"
        Expect.equal filterCalls 0 "event filters do not hide a version conflict"
        Expect.equal fixture.JournalRows 1L "rejection creates no domain event or journal record"
        let next = result (fixture.Guard(1L, "account", Add 4))
        Expect.equal (version next) 2L "a caller using the current version can continue"

let private concurrentSameCid =
    testCase "expected version: concurrent same-CID writers have one winner and distinct conflicts"
    <| fun _ ->
        use fixture = new Fixture()
        let cid = Fcqrs.newCid ()
        let attempts =
            [| for amount in 1..20 ->
                task {
                    try
                        let! event = fixture.Guard(0L, "race", Add amount, cid = cid)
                        return Choice1Of2(amount, event)
                    with :? AggregateVersionConflictException as error ->
                        return Choice2Of2 error
                } |]
        let outcomes = result (Task.WhenAll attempts)
        let winners = outcomes |> Array.choose (function Choice1Of2 winner -> Some winner | _ -> None)
        let conflicts = outcomes |> Array.choose (function Choice2Of2 error -> Some error | _ -> None)
        Expect.equal winners.Length 1 "exactly one command observes the required version before persisting"
        Expect.equal conflicts.Length 19 "losers cannot consume the winner's same-CID notification"
        let amount, event = winners.[0]
        Expect.equal event.EventDetails (Added amount) "the winner receives its own command's result"
        for error in conflicts do
            Expect.equal error.ExpectedVersion 0L "every request expected the same initial version"
            Expect.equal error.ActualVersion 1L "every conflict sees the completed winning write"
        Expect.equal fixture.Commands.Length 1 "only the winner executes the domain handler"
        Expect.equal fixture.JournalRows 1L "one event was persisted"

let private deferredVersion =
    testCase "expected version: deferred folds and no-op commands do not advance the persisted version"
    <| fun _ ->
        use fixture = new Fixture(commandTimeout = "2s")
        let deferred = result (fixture.Guard(0L, "deferred", DeferredAdd 8))
        Expect.equal (version deferred) 0L "a deferred state change does not consume a persisted version"
        Expect.equal deferred.Journaled (Some false) "the existing deferred delivery stamp remains intact"
        let observed = result (fixture.Guard(0L, "deferred", Inspect))
        Expect.equal observed.EventDetails (Observed 8) "the handler still receives its current live state"
        let quiet = fixture.Guard(0L, "deferred", Quiet)
        Expect.throwsT<TimeoutException> (fun () -> result quiet |> ignore) "a no-reply decision retains the ordinary command deadline"
        let written = result (fixture.Guard(0L, "deferred", Add 2))
        Expect.equal (version written) 1L "neither deferred nor no-op handling advances the persisted version"
        Expect.equal fixture.JournalRows 1L "only the final command is journaled"

let private batchVersion =
    testCase "expected version: PersistAll advances by the whole batch before the next guard"
    <| fun _ ->
        use fixture = new Fixture()
        let last = result (fixture.Guard(0L, "batch", Batch(3, 4), filter = (function Added 4 -> true | _ -> false)))
        Expect.equal (version last) 2L "the matching second event carries version two"
        let error = conflict (fixture.Guard(1L, "batch", Add 99))
        Expect.equal error.ActualVersion 2L "another command cannot enter in the middle of an atomic batch"
        let next = result (fixture.Guard(2L, "batch", Add 5))
        Expect.equal (version next) 3L "the next command starts after the complete batch"
        Expect.equal fixture.JournalRows 3L "the conflict added no journal row"

let private publishedReply =
    testCase "expected version: a published reply preserves request identity without advancing the version"
    <| fun _ ->
        use fixture = new Fixture()
        let cid = Fcqrs.newCid ()
        let published = result (fixture.Guard(0L, "published", PublishInitial, cid = cid))
        let command = fixture.Commands |> Array.exactlyOne
        Expect.equal published.Id command.Id "an application-created reply carries its originating command ID"
        Expect.equal published.CorrelationId cid "the reply retains the request correlation ID"
        Expect.equal published.Journaled (Some false) "publication does not claim journal persistence"
        Expect.equal (version published) 0L "the published initial-state reply retains version zero"
        let persisted = result (fixture.Guard(0L, "published", Add 2))
        Expect.equal (version persisted) 1L "the prior publication did not consume a persisted version"
        Expect.equal fixture.JournalRows 1L "only the later persisted event appears in the journal"

let private recoveredVersion =
    testCase "expected version: journal and snapshot recovery restore the comparison version"
    <| fun _ ->
        for snapshots in [ NoSnapshots; Every 1 ] do
            let database = databasePath ()
            try
                do
                    use first = new Fixture(database = database, snapshots = snapshots)
                    first.Guard(0L, "recovered", Add 6) |> result |> ignore
                    if snapshots <> NoSnapshots then
                        let until = Stopwatch.StartNew()
                        while first.Scalar("SELECT COUNT(*) FROM snapshot") = 0L && until.Elapsed < deadline do
                            Thread.Sleep 25
                        Expect.isGreaterThan (first.Scalar "SELECT COUNT(*) FROM snapshot") 0L "the snapshot is durable before restart"
                use recovered = new Fixture(database = database, snapshots = snapshots)
                let error = conflict (recovered.Guard(0L, "recovered", Add 100))
                Expect.equal error.ActualVersion 1L "recovery restores the persisted version before handling commands"
                Expect.equal recovered.Commands.Length 0 "a stale command after recovery still bypasses decide"
                let accepted = result (recovered.Guard(1L, "recovered", Add 2))
                Expect.equal (version accepted) 2L "a matching command continues after the recovered history"
                let observed = recovered.Send("recovered", Inspect)
                Expect.equal observed.EventDetails (Observed 8) "recovery also restores the domain state"
            finally
                deleteDatabase database

let private filterDoesNotRetractWrite =
    testCase "expected version: a nonmatching filter can time out after the accepted write commits"
    <| fun _ ->
        use fixture = new Fixture(commandTimeout = "2s")
        let waiting = fixture.Guard(0L, "filtered", Add 9, filter = (fun _ -> false))
        Expect.throwsT<TimeoutException> (fun () -> result waiting |> ignore) "ordinary event-filter timeout semantics are preserved"
        Expect.equal fixture.JournalRows 1L "timeout does not mean that the write failed"
        let error = conflict (fixture.Guard(0L, "filtered", Add 9))
        Expect.equal error.ActualVersion 1L "blindly resending with the old version cannot duplicate the write"

let private rejectedRunner =
    testCase "expected version: a stale asynchronous command never starts its runner"
    <| fun _ ->
        use fixture = new Fixture()
        fixture.Send("async", Add 1) |> ignore
        let before = fixture.Commands.Length
        conflict (fixture.Guard(0L, "async", StartAsync 10)) |> ignore
        Expect.equal fixture.RunnerCalls 0 "a rejected command starts no ephemeral effect"
        Expect.equal fixture.Commands.Length before "the original command was rejected before decide"

let private delayedRunnerRechecks =
    testCase "expected version: an asynchronous result rechecks the original version before decide"
    <| fun _ ->
        use fixture = new Fixture()
        let pending = fixture.Guard(0L, "async-race", StartAsync 10)
        wait fixture.RunnerEntered
        fixture.Send("async-race", Add 3) |> ignore
        let before = fixture.Commands.Length
        fixture.ReleaseRunner()
        let error = conflict pending
        Expect.equal error.ExpectedVersion 0L "the delayed self-command retains the caller's precondition"
        Expect.equal error.ActualVersion 1L "the delayed command sees the intervening write"
        Expect.equal fixture.Commands.Length before "the stale result is rejected before its domain handler"
        Expect.equal fixture.JournalRows 1L "the delayed result adds no event after its precondition fails"

let private runnerIdentityAndMetadata =
    testCase "expected version: a successful asynchronous result retains request identity without guard metadata"
    <| fun _ ->
        use fixture = new Fixture()
        let cid = Fcqrs.newCid ()
        let pending = fixture.Guard(0L, "async-success", StartAsync 7, cid = cid)
        wait fixture.RunnerEntered
        let original = fixture.Commands |> Array.exactlyOne
        fixture.ReleaseRunner()
        let event = result pending
        Expect.equal event.EventDetails (Added 7) "the eventual event completes its guarded request"
        Expect.equal event.Id original.Id "the delayed self-command retains the original request ID"
        Expect.equal event.CorrelationId cid "the caller's correlation ID survives the runner"
        Expect.equal fixture.Commands.Length 2 "both the initiating command and its result passed the same version guard"
        for command in fixture.Commands do
            Expect.equal command.Id original.Id "the control wrapper preserves request identity through self-dispatch"
            let controls = command.Metadata |> Map.remove Telemetry.TraceparentMetadataKey
            Expect.isEmpty controls "expected-version controls are not injected into domain metadata"
        for stored in fixture.Folded do
            let controls = stored.Metadata |> Map.remove Telemetry.TraceparentMetadataKey
            Expect.isEmpty controls "persisted event metadata contains no conditional-command control fields"

let private unstashRechecks =
    testCase "expected version: unstashing retains and rechecks the original wrapper"
    <| fun _ ->
        use fixture = new Fixture()
        fixture.Send("stashed", Hold) |> ignore
        let pending = fixture.Guard(1L, "stashed", AddWhenReleased 99)
        wait fixture.Stashed
        fixture.Send("stashed", Add 5) |> ignore
        fixture.Send("stashed", Release) |> ignore
        let before = fixture.Commands |> Array.filter (fun command -> command.CommandDetails = AddWhenReleased 99) |> Array.length
        let error = conflict pending
        Expect.equal error.ExpectedVersion 1L "unstashing does not discard the original precondition"
        Expect.equal error.ActualVersion 3L "the deferred command is checked after intervening persisted events"
        let attempts = fixture.Commands |> Array.filter (fun command -> command.CommandDetails = AddWhenReleased 99) |> Array.length
        Expect.equal attempts 1 "the stale unstashed command never re-enters decide"
        Expect.equal before 1 "the original command entered decide once to stash itself"
        Expect.equal fixture.JournalRows 3L "the unstashed stale command cannot persist its amount"

let private cancellation =
    testCase "expected version: cancellation before execution avoids dispatch and cancellation afterward does not retract it"
    <| fun _ ->
        use fixture = new Fixture()
        use alreadyCanceled = new CancellationTokenSource()
        alreadyCanceled.Cancel()
        let neverSent = fixture.Guard(0L, "canceled", Add 1, cancellationToken = alreadyCanceled.Token)
        canceled neverSent "a pre-canceled async does not start"
        Expect.equal fixture.Commands.Length 0 "pre-cancellation invokes no domain handler"
        let cid = Fcqrs.newCid ()
        use stopWaiting = new CancellationTokenSource()
        let pending = fixture.Guard(0L, "canceled", StartAsync 4, cid = cid, cancellationToken = stopWaiting.Token)
        wait fixture.RunnerEntered
        stopWaiting.Cancel()
        canceled pending "cancellation releases the caller"
        let subscriptions = Fcqrs.projection fixture.Api (Projection.single 0 (fun _ _ -> ()))
        use persisted = subscriptions.Subscribe(cid, 1)
        fixture.ReleaseRunner()
        wait persisted.Task
        Expect.equal fixture.JournalRows 1L "an already-dispatched command can still persist after its caller cancels"

let private invalidVersion =
    testCase "expected version: negative versions are rejected before dispatch"
    <| fun _ ->
        use fixture = new Fixture()
        Expect.throwsT<ArgumentException>
            (fun () -> fixture.Guard(-1L, "negative", Add 1) |> result |> ignore)
            "the API accepts only nonnegative persisted versions"
        Expect.equal fixture.Commands.Length 0 "invalid input never reaches the aggregate"

let private csharpFacade =
    testCase "expected version: C# overloads preserve success, conflict and cancellation semantics"
    <| fun _ ->
        use fixture = new Fixture()
        let factory = FCQRS.CSharp.AggregateFactory(fun id -> fixture.Handle.Factory id)
        let id = Fcqrs.aggregateId "csharp"
        let filter = Func<VersionEvent, bool>(fun _ -> true)
        let first =
            FCQRS.CSharp.ActorWiring.SendIfVersionAsync<VersionEvent, VersionCommand>(
                fixture.Api, factory, 0L, Fcqrs.newCid (), id, Add 5, filter)
            |> result
        Expect.equal first.EventDetails (Added 5) "the C# overload without a token returns the committed event"
        let error =
            FCQRS.CSharp.ActorWiring.SendIfVersionAsync<VersionEvent, VersionCommand>(
                fixture.Api, factory, 0L, Fcqrs.newCid (), id, Add 9, filter, CancellationToken.None)
            |> conflict
        Expect.equal error.ActualVersion 1L "the token overload exposes the same typed conflict"
        use token = new CancellationTokenSource()
        token.Cancel()
        let before = fixture.Commands.Length
        let neverSent =
            FCQRS.CSharp.ActorWiring.SendIfVersionAsync<VersionEvent, VersionCommand>(
                fixture.Api, factory, 1L, Fcqrs.newCid (), id, Add 20, filter, token.Token)
        canceled neverSent "the C# cancellation token prevents starting an already-canceled request"
        Expect.equal fixture.Commands.Length before "the canceled C# call reaches no domain handler"
        Expect.equal fixture.JournalRows 1L "only the successful C# call wrote an event"

let tests =
    testSequenced (
        testList "expected aggregate version"
            [ acceptsAndRejects; concurrentSameCid; deferredVersion; batchVersion; publishedReply
              recoveredVersion; filterDoesNotRetractWrite; rejectedRunner; delayedRunnerRechecks
              runnerIdentityAndMetadata; unstashRechecks; cancellation; invalidVersion; csharpFacade ])
