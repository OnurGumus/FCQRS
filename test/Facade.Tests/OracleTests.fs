/// The Akka.Persistence.Sql tables on Oracle, which FCQRS creates itself: Akka's own creation fails
/// at a second start and adds no unique key. These tests run when FCQRS_TEST_ORACLE names a server.
module OracleTests

open System
open System.Collections.Generic
open Expecto
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging.Abstractions
open Oracle.ManagedDataAccess.Client
open FCQRS.Common
open FCQRS.FSharp
open ProjectionCatchUpTests

/// Each event holds the aggregate's running total, so an aggregate that recovered another's events,
/// or its own twice, reports the wrong total.
let private totals (api: IActor) =
    Fcqrs.aggregate api
        { Name = "OracleCounter"
          Initial = 0
          Decide = fun (command: Command<int>) state -> PersistEvent(Added(state + command.CommandDetails))
          Fold = fun (event: Event<CatchUpEvent>) _ -> let (Added total) = event.EventDetails in total
          Snapshots = Every 2
          Passivation = PassivationPolicy.Default }

let private actorWith (settings: (string * string) list) (connectionString: string) =
    let configuration =
        VerifySerialization.configuration()
            .AddInMemoryCollection([ for key, value in settings -> KeyValuePair<string, string | null>(key, value) ])
            .Build()
    Fcqrs.actor configuration NullLoggerFactory.Instance
        (Some(Fcqrs.connect FCQRS.Actor.DBType.Oracle connectionString)) ("Oracle" + Guid.NewGuid().ToString "N")

let private add (counter: AggregateHandle<int, CatchUpEvent>) (id: string) (amount: int) =
    let event =
        counter.Send (Fcqrs.newCid ()) (Fcqrs.aggregateId id) amount (fun _ -> true)
        |> fun work -> Async.RunSynchronously(work, 20000)
    let (Added total) = event.EventDetails
    total

/// Starts an actor system on the journal, runs `work` with a counter, and stops it.
let private session (connectionString: string) (work: AggregateHandle<int, CatchUpEvent> -> unit) =
    let api = actorWith [] connectionString
    try
        let counter = totals api
        Fcqrs.wireSagaStarters api []
        work counter
    finally
        api.Stop().Wait(TimeSpan.FromSeconds 30.0) |> ignore

let private run (connectionString: string) (sql: string) =
    use connection = new OracleConnection(connectionString)
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- sql
    command.ExecuteNonQuery() |> ignore

/// The table's constraints of a type, each as its columns in order.
let private constraints (connectionString: string) (table: string) (kind: string) =
    use connection = new OracleConnection(connectionString)
    connection.Open()
    use command = connection.CreateCommand()
    command.BindByName <- true
    command.CommandText <-
        "SELECT LISTAGG(cc.column_name, ',') WITHIN GROUP (ORDER BY cc.position) FROM user_constraints c "
        + "JOIN user_cons_columns cc ON cc.constraint_name = c.constraint_name "
        + "WHERE c.table_name = :name AND c.constraint_type = :kind GROUP BY c.constraint_name"
    command.Parameters.Add("name", table) |> ignore
    command.Parameters.Add("kind", kind) |> ignore
    use reader = command.ExecuteReader()
    set [ while reader.Read() do reader.GetString 0 ]

let private refused (settings: (string * string) list) (connectionString: string) (expected: string) =
    let error = Expect.throwsC (fun () -> actorWith settings connectionString |> ignore) id
    match error with
    | :? InvalidOperationException as error -> Expect.stringContains error.Message expected "the error explains the refusal"
    | other -> failtest $"expected InvalidOperationException, got {other.GetType().Name}: {other.Message}"

let tests =
    match TestDatabases.oracle with
    | None -> ptestCase "Oracle tables: set FCQRS_TEST_ORACLE to run" ignore
    | Some server ->
        testSequenced (
            testList "Oracle tables" [
                testCase "the journal and snapshot tables have their keys, and later starts recover from them" <| fun _ ->
                    TestDatabases.withDatabase server "fcqrs_oracle" <| fun connectionString ->
                        session connectionString (fun counter ->
                            Expect.equal (add counter "a" 2) 2 "the first event is stored"
                            Expect.equal (add counter "a" 3) 5 "the second event is stored, and a snapshot taken")
                        Expect.equal (constraints connectionString "journal" "P") (set [ "ordering" ]) "the journal's primary key is its number"
                        Expect.equal (constraints connectionString "journal" "U") (set [ "persistence_id,sequence_number" ])
                            "one persistence ID cannot store a sequence number twice"
                        Expect.equal (constraints connectionString "snapshot" "P") (set [ "persistence_id,sequence_number" ]) "the snapshot table has its key"
                        // Akka's own creation failed here, at the second start, with ORA-00955.
                        session connectionString (fun counter -> Expect.equal (add counter "a" 4) 9 "the second start recovers the snapshot and event")
                        session connectionString (fun counter -> Expect.equal (add counter "a" 1) 10 "so does the third")

                testCase "two aggregate IDs that differ only in case keep separate histories, also after recovery" <| fun _ ->
                    TestDatabases.withDatabase server "fcqrs_oracle" <| fun connectionString ->
                        session connectionString (fun counter ->
                            Expect.equal (add counter "abc" 2) 2 "abc starts at 2"
                            Expect.equal (add counter "ABC" 5) 5 "ABC starts at 5, without abc's event")
                        session connectionString (fun counter ->
                            Expect.equal (add counter "ABC" 10) 15 "ABC recovers only its own event"
                            Expect.equal (add counter "abc" 10) 12 "abc recovers only its own event")

                testCase "a journal that holds an event number twice is refused at startup" <| fun _ ->
                    TestDatabases.withDatabase server "fcqrs_oracle" <| fun connectionString ->
                        // A journal Akka created: no unique key, and two writers stored sequence 1.
                        run connectionString
                            "CREATE TABLE \"journal\" (\"ordering\" NUMBER(19) NOT NULL PRIMARY KEY, \"created\" NUMBER(19) NOT NULL, \"deleted\" NUMBER(1) NOT NULL, \"persistence_id\" VARCHAR2(255) NOT NULL, \"sequence_number\" NUMBER(19) NOT NULL, \"message\" BLOB NOT NULL, \"manifest\" VARCHAR2(500), \"identifier\" NUMBER(10), \"writer_uuid\" VARCHAR2(128), \"tags\" VARCHAR2(100))"
                        for ordering in [ 1; 2 ] do
                            run connectionString $"INSERT INTO \"journal\" VALUES ({ordering}, 0, 0, 'OracleCounter/0/a', 1, HEXTORAW('00'), NULL, NULL, NULL, NULL)"
                        let error = Expect.throwsC (fun () -> actorWith [] connectionString |> ignore) id
                        Expect.stringContains error.Message "ORA-02299" "the unique key cannot be added over the repeated number"

                testCase "Akka's own table creation is refused" <| fun _ ->
                    TestDatabases.withDatabase server "fcqrs_oracle" <| fun connectionString ->
                        refused [ "config:akka:persistence:journal:sql:auto-initialize", "true" ] connectionString "auto-initialize"

                testCase "a tag table is refused, for writes and for reads" <| fun _ ->
                    TestDatabases.withDatabase server "fcqrs_oracle" <| fun connectionString ->
                        refused [ "config:akka:persistence:journal:sql:tag-write-mode", "TagTable" ] connectionString "tag-write-mode"
                        refused [ "config:akka:persistence:query:journal:sql:tag-read-mode", "TagTable" ] connectionString "tag-read-mode"
            ])
