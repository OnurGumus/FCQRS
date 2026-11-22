/// Database servers the integration tests run on besides SQLite: PostgreSQL when FCQRS_TEST_POSTGRES
/// holds a connection string, and SQL Server when FCQRS_TEST_SQLSERVER does. Each test makes its own
/// database on the server and drops it afterwards.
module TestDatabases

open System
open System.Data.Common
open Microsoft.Data.SqlClient
open Npgsql
open FCQRS.ProjectionStorage

/// A kind of database server.
type Kind =
    | PostgreSql
    | SqlServer

    member kind.Name =
        match kind with
        | PostgreSql -> "PostgreSQL"
        | SqlServer -> "SQL Server"

    member kind.DBType =
        match kind with
        | PostgreSql -> FCQRS.Actor.DBType.PostgreSQL15
        | SqlServer -> FCQRS.Actor.DBType.SqlServer2022

    member kind.Dialect =
        match kind with
        | PostgreSql -> ProjectionSqlDialect.PostgreSql
        | SqlServer -> ProjectionSqlDialect.SqlServer

    /// A new, unopened connection.
    member kind.Connect(connectionString: string) : DbConnection =
        match kind with
        | PostgreSql -> new NpgsqlConnection(connectionString)
        | SqlServer -> new SqlConnection(connectionString)

    /// A prefix that names the kind in a child process's argument.
    member kind.Prefix =
        match kind with
        | PostgreSql -> "postgres:"
        | SqlServer -> "sqlserver:"

    /// The kind and connection string in an argument `Prefix` begins, or None.
    static member Parse(argument: string) : (Kind * string) option =
        [ PostgreSql; SqlServer ]
        |> List.tryFind (fun kind -> argument.StartsWith(kind.Prefix, StringComparison.Ordinal))
        |> Option.map (fun kind -> kind, argument.Substring kind.Prefix.Length)

/// A server, and a connection string to its administrative database.
type Server = { Kind: Kind; Root: string }

let private fromEnvironment name kind =
    match Environment.GetEnvironmentVariable name with
    | null | "" -> None
    | root -> Some { Kind = kind; Root = root }

/// The PostgreSQL server FCQRS_TEST_POSTGRES names.
let postgres = fromEnvironment "FCQRS_TEST_POSTGRES" PostgreSql

/// The SQL Server FCQRS_TEST_SQLSERVER names.
let sqlServer = fromEnvironment "FCQRS_TEST_SQLSERVER" SqlServer

let private admin (server: Server) (sql: string) =
    use connection = server.Kind.Connect server.Root
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- sql
    command.ExecuteNonQuery() |> ignore

/// The connection string of a database on the server.
let connectionString (server: Server) (database: string) =
    match server.Kind with
    | PostgreSql ->
        let builder = NpgsqlConnectionStringBuilder(server.Root)
        builder.Database <- database
        builder.ConnectionString
    | SqlServer ->
        let builder = SqlConnectionStringBuilder(server.Root)
        builder.InitialCatalog <- database
        builder.ConnectionString

/// Creates a database. On SQL Server it has the binary collation FCQRS requires, and with
/// `snapshotReads` a read sees the last committed version of a row instead of waiting for a writer
/// to commit, as on PostgreSQL.
let createDatabase (server: Server) (database: string) (snapshotReads: bool) =
    match server.Kind with
    | PostgreSql -> admin server $"CREATE DATABASE {database}"
    | SqlServer ->
        admin server $"CREATE DATABASE [{database}] COLLATE Latin1_General_100_BIN2_UTF8"
        if snapshotReads then admin server $"ALTER DATABASE [{database}] SET READ_COMMITTED_SNAPSHOT ON"

/// Drops a database, closing the connections that remain.
let dropDatabase (server: Server) (database: string) =
    match server.Kind with
    | PostgreSql ->
        NpgsqlConnection.ClearAllPools()
        admin server $"DROP DATABASE IF EXISTS {database} WITH (FORCE)"
    | SqlServer ->
        SqlConnection.ClearAllPools()
        admin server $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}] END"

/// Runs `test` with the connection string of a new database named from `prefix`, and drops it.
let withDatabase (server: Server) (prefix: string) (test: string -> unit) =
    let database = $"{prefix}_{Guid.NewGuid():N}"
    createDatabase server database false
    try
        test (connectionString server database)
    finally
        dropDatabase server database

/// A text type for a test's own key or compared column: SQL Server cannot compare TEXT.
let textType (kind: Kind option) =
    match kind with
    | Some SqlServer -> "NVARCHAR(255)"
    | _ -> "TEXT"
