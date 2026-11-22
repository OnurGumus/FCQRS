/// Database servers the integration tests run on besides SQLite: PostgreSQL when FCQRS_TEST_POSTGRES
/// holds a connection string, SQL Server when FCQRS_TEST_SQLSERVER does, and Oracle when
/// FCQRS_TEST_ORACLE does. Each test makes its own
/// database on the server and drops it afterwards.
module TestDatabases

open System
open System.Data.Common
open Microsoft.Data.SqlClient
open Npgsql
open Oracle.ManagedDataAccess.Client
open FCQRS.ProjectionStorage

/// A kind of database server.
type Kind =
    | PostgreSql
    | SqlServer
    | Oracle

    member kind.Name =
        match kind with
        | PostgreSql -> "PostgreSQL"
        | SqlServer -> "SQL Server"
        | Oracle -> "Oracle"

    member kind.DBType =
        match kind with
        | PostgreSql -> FCQRS.Actor.DBType.PostgreSQL15
        | SqlServer -> FCQRS.Actor.DBType.SqlServer2022
        | Oracle -> FCQRS.Actor.DBType.Oracle

    member kind.Dialect =
        match kind with
        | PostgreSql -> ProjectionSqlDialect.PostgreSql
        | SqlServer -> ProjectionSqlDialect.SqlServer
        | Oracle -> ProjectionSqlDialect.Oracle

    /// A new, unopened connection.
    member kind.Connect(connectionString: string) : DbConnection =
        match kind with
        | PostgreSql -> new NpgsqlConnection(connectionString)
        | SqlServer -> new SqlConnection(connectionString)
        | Oracle -> new OracleConnection(connectionString)

    /// A prefix that names the kind in a child process's argument.
    member kind.Prefix =
        match kind with
        | PostgreSql -> "postgres:"
        | SqlServer -> "sqlserver:"
        | Oracle -> "oracle:"

    /// The kind and connection string in an argument `Prefix` begins, or None.
    static member Parse(argument: string) : (Kind * string) option =
        [ PostgreSql; SqlServer; Oracle ]
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

/// The Oracle server FCQRS_TEST_ORACLE names, as a user that can create and drop users. Each test
/// database is a user, which owns a schema of the same name.
let oracle = fromEnvironment "FCQRS_TEST_ORACLE" Oracle

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
    | Oracle ->
        let builder = OracleConnectionStringBuilder(server.Root)
        builder.UserID <- database
        builder.Password <- "Fcqrs-test-1"
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
    | Oracle ->
        admin server $"CREATE USER {database} IDENTIFIED BY \"Fcqrs-test-1\" QUOTA UNLIMITED ON USERS"
        admin server $"GRANT CREATE SESSION, CREATE TABLE, CREATE SEQUENCE, CREATE TRIGGER, CREATE VIEW, CREATE PROCEDURE TO {database}"

/// Drops a database, closing the connections that remain.
let dropDatabase (server: Server) (database: string) =
    match server.Kind with
    | PostgreSql ->
        NpgsqlConnection.ClearAllPools()
        admin server $"DROP DATABASE IF EXISTS {database} WITH (FORCE)"
    | SqlServer ->
        SqlConnection.ClearAllPools()
        admin server $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}] END"
    | Oracle ->
        OracleConnection.ClearAllPools()
        admin server $"BEGIN FOR s IN (SELECT sid, serial# AS serial FROM v$session WHERE username = UPPER('{database}')) LOOP EXECUTE IMMEDIATE 'ALTER SYSTEM KILL SESSION ''' || s.sid || ',' || s.serial || ''' IMMEDIATE'; END LOOP; EXECUTE IMMEDIATE 'DROP USER {database} CASCADE'; END;"

/// Runs `test` with the connection string of a new database named from `prefix`, and drops it.
let withDatabase (server: Server) (prefix: string) (test: string -> unit) =
    let database = $"{prefix}_{Guid.NewGuid():N}"
    createDatabase server database false
    try
        test (connectionString server database)
    finally
        dropDatabase server database

/// A whole-number column type for a test's own table: Oracle has no BIGINT.
let bigintType (kind: Kind option) =
    match kind with
    | Some Oracle -> "NUMBER(19)"
    | _ -> "BIGINT"

// The tables FCQRS and Akka create with quoted lower-case names, and the journal's columns, which
// Oracle would read in upper case unquoted. FCQRS's own tables have unquoted columns, so the
// journal's are quoted only in a statement that reads the journal.
let private oracleTables = [ "fcqrs_projection_progress_lock"; "fcqrs_projection_progress"; "fcqrs_schedule_index"; "journal"; "snapshot" ]
let private journalColumns = [ "persistence_id"; "sequence_number"; "ordering"; "deleted" ]

/// A test's SQL for the connection's database: on Oracle, its @name parameters become :name, and
/// the lower-case names Akka and FCQRS quote are quoted.
let sqlFor (connection: DbConnection) (sql: string) =
    match connection with
    | :? OracleConnection ->
        let quoteWords (words: string list) (text: string) =
            Text.RegularExpressions.Regex.Replace(text, "(?<![\"\w])(" + String.Join("|", words) + ")(?![\"\w])", "\"$1\"")
        let quoted = quoteWords oracleTables sql
        let quoted = if Text.RegularExpressions.Regex.IsMatch(sql, "\\bjournal\\b") then quoteWords journalColumns quoted else quoted
        Text.RegularExpressions.Regex.Replace(quoted, "@([A-Za-z_][A-Za-z0-9_]*)", ":$1")
    | _ -> sql

/// A command on `connection` with `parameters`, named @name, written for its database by `sqlFor`.
let command (connection: DbConnection) (transaction: DbTransaction option) (sql: string) (parameters: (string * obj) list) : DbCommand =
    let command = connection.CreateCommand()
    command.CommandText <- sqlFor connection sql
    transaction |> Option.iter (fun transaction -> command.Transaction <- transaction)
    match command with
    | :? OracleCommand as oracle -> oracle.BindByName <- true
    | _ -> ()
    for name, value in parameters do
        let parameter = command.CreateParameter()
        parameter.ParameterName <- (match connection with :? OracleConnection -> name.TrimStart '@' | _ -> name)
        parameter.Value <- value
        command.Parameters.Add parameter |> ignore
    command

/// A text type for a test's own key or compared column: SQL Server cannot compare TEXT.
let textType (kind: Kind option) =
    match kind with
    | Some SqlServer -> "NVARCHAR(255)"
    | Some Oracle -> "VARCHAR2(255)"
    | _ -> "TEXT"
