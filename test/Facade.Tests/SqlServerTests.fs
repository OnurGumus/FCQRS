/// SQL Server's collations. A case-insensitive collation, the server's usual default, treats
/// "Account/0/abc" and "Account/0/ABC" as one journal key, so the actor system refuses a database
/// without a binary one. These tests run when FCQRS_TEST_SQLSERVER names a server.
module SqlServerTests

open System
open Expecto
open Microsoft.Data.SqlClient
open Microsoft.Extensions.Logging.Abstractions
open FCQRS.Common
open FCQRS.FSharp
open ProjectionCatchUpTests

/// Each event holds the aggregate's running total, so an aggregate that recovered another's events
/// reports the wrong total.
let private totals (api: IActor) =
    Fcqrs.aggregate api
        { Name = "CaseCounter"
          Initial = 0
          Decide = fun (command: Command<int>) state -> PersistEvent(Added(state + command.CommandDetails))
          Fold = fun (event: Event<CatchUpEvent>) _ -> let (Added total) = event.EventDetails in total
          Snapshots = NoSnapshots
          Passivation = PassivationPolicy.Default }

let private actorOn (connectionString: string) =
    Fcqrs.actor (VerifySerialization.configuration().Build()) NullLoggerFactory.Instance
        (Some(Fcqrs.connect FCQRS.Actor.DBType.SqlServer2022 connectionString)) ("Case" + Guid.NewGuid().ToString "N")

let private add (counter: AggregateHandle<int, CatchUpEvent>) (id: string) (amount: int) =
    let event =
        counter.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId id) amount (fun _ -> true)
        |> fun work -> Async.RunSynchronously(work, 20000)
    let (Added total) = event.EventDetails
    total

let private run (sql: string) (connectionString: string) =
    use connection = new SqlConnection(connectionString)
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- sql
    command.ExecuteNonQuery() |> ignore

let private refused (connectionString: string) (expected: string) =
    let error = Expect.throwsC (fun () -> actorOn connectionString |> ignore) id
    match error with
    | :? InvalidOperationException as error -> Expect.stringContains error.Message expected "the error explains the refusal"
    | other -> failtest $"expected InvalidOperationException, got {other.GetType().Name}: {other.Message}"

let tests =
    match TestDatabases.sqlServer with
    | None -> ptestCase "SQL Server collations: set FCQRS_TEST_SQLSERVER to run" ignore
    | Some server ->
        testSequenced (
            testList "SQL Server collations" [
                testCase "a database whose collation ignores case is refused at startup" <| fun _ ->
                    let database = $"fcqrs_case_{Guid.NewGuid():N}"
                    // The server's default collation, case-insensitive on a default installation.
                    run $"CREATE DATABASE [{database}] COLLATE SQL_Latin1_General_CP1_CI_AS" server.Root
                    try
                        refused (TestDatabases.connectionString server database) "binary collation"
                    finally
                        TestDatabases.dropDatabase server database

                testCase "a journal column whose collation differs from the database's is refused at startup" <| fun _ ->
                    TestDatabases.withDatabase server "fcqrs_case" <| fun connectionString ->
                        run "CREATE TABLE journal (ordering BIGINT IDENTITY PRIMARY KEY, persistence_id NVARCHAR(255) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL)" connectionString
                        refused connectionString "persistence_id"

                testCase "two aggregate IDs that differ only in case keep separate histories, also after recovery" <| fun _ ->
                    TestDatabases.withDatabase server "fcqrs_case" <| fun connectionString ->
                        do
                            let api = actorOn connectionString
                            try
                                let counter = totals api
                                Fcqrs.wireSagaStarters api []
                                Expect.equal (add counter "abc" 2) 2 "abc starts at 2"
                                Expect.equal (add counter "ABC" 5) 5 "ABC starts at 5, without abc's event"
                                Expect.equal (add counter "abc" 1) 3 "abc continues from its own total"
                            finally
                                api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore
                        let api = actorOn connectionString
                        try
                            let counter = totals api
                            Fcqrs.wireSagaStarters api []
                            Expect.equal (add counter "ABC" 10) 15 "ABC recovers only its own event"
                            Expect.equal (add counter "abc" 10) 13 "abc recovers only its own events"
                        finally
                            api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore
            ])
