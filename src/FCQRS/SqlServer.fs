/// Checks that a SQL Server database can hold an FCQRS journal and its projections.
module internal FCQRS.SqlServer

open System
open System.Data.Common

/// Whether a SQL Server collation compares strings by their code points, as .NET's ordinal
/// comparison does: the binary collations, such as Latin1_General_100_BIN2 or
/// Latin1_General_100_BIN2_UTF8.
let isBinary (collation: string) : bool =
    collation.Split('_') |> Array.exists (fun part -> part.Equals("BIN2", StringComparison.OrdinalIgnoreCase) || part.Equals("BIN", StringComparison.OrdinalIgnoreCase))

// The first column of the first row, or None for no row or a NULL.
let private scalar (connection: DbConnection) (sql: string) (parameters: (string * obj) list) : obj option =
    use command = connection.CreateCommand()
    command.CommandText <- sql
    for name, value in parameters do
        let parameter = command.CreateParameter()
        parameter.ParameterName <- name
        parameter.Value <- value
        command.Parameters.Add parameter |> ignore
    match command.ExecuteScalar() with
    | null -> None
    | :? DBNull -> None
    | value -> Some value

let private text (value: obj option) =
    match value with
    | Some(:? string as value) -> value
    | _ -> invalidOp "SQL Server returned no value for a database property."

/// The database's default collation.
let databaseCollation (connection: DbConnection) : string =
    scalar connection "SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'))" [] |> text

/// Raises unless the open connection's database can hold FCQRS data. Its default collation must be
/// binary: Akka creates the journal and snapshot tables without a collation, so they take the
/// database's, and a case- or accent-insensitive one would make two persistence IDs that differ
/// only in case, such as "Account/0/a" and "Account/0/A", one key, so one aggregate could recover
/// the other's events. Each existing column in `columns` (schema, table, column) must use that
/// same collation, so FCQRS's queries can compare it with values the database parses. With
/// `json`, the database must also read JSON with OPENJSON: compatibility level 130 (SQL Server
/// 2016) or higher.
let checkDatabase (connection: DbConnection) (json: bool) (columns: (string option * string * string) list) : unit =
    let database = scalar connection "SELECT DB_NAME()" [] |> text
    let collation = databaseCollation connection
    if not (isBinary collation) then
        invalidOp
            $"The SQL Server database '{database}' uses the collation '{collation}', which treats some different persistence IDs as equal. \
              FCQRS needs a binary collation: create the database with one, such as 'CREATE DATABASE [{database}] COLLATE Latin1_General_100_BIN2_UTF8', \
              or change it with ALTER DATABASE before any table exists."
    if json then
        let level =
            match scalar connection "SELECT compatibility_level FROM sys.databases WHERE database_id = DB_ID()" [] with
            | Some value -> Convert.ToInt32 value
            | None -> invalidOp "SQL Server returned no compatibility level for the database."
        if level < 130 then
            invalidOp $"Projections on SQL Server need compatibility level 130 (SQL Server 2016) or higher; the database '{database}' has {level}."
    for schema, table, column in columns do
        let found =
            scalar
                connection
                "SELECT c.collation_name FROM sys.columns c WHERE c.object_id = OBJECT_ID(CASE WHEN @schema IS NULL THEN QUOTENAME(@table) ELSE QUOTENAME(@schema) + '.' + QUOTENAME(@table) END, N'U') AND c.name = @column"
                [ "@schema", (match schema with Some value -> value :> obj | None -> DBNull.Value :> obj)
                  "@table", table :> obj
                  "@column", column :> obj ]
        match found with
        | Some(:? string as existing) when not (String.Equals(existing, collation, StringComparison.OrdinalIgnoreCase)) ->
            invalidOp
                $"The column '{column}' of the table '{table}' in the SQL Server database '{database}' uses the collation '{existing}'; \
                  FCQRS needs the database's collation, '{collation}'."
        | _ -> ()

// The persistence-ID column of an Akka.Persistence.Sql plugin, the provider and connection string
// it uses, or None when the plugin is not the SQL plugin on SQL Server.
let private pluginColumn (config: Akka.Configuration.Config) (pluginKey: string) (expected: string) (kind: string) =
    let path = config.GetString(pluginKey, "")
    if path <> expected then None
    else
        let plugin = config.GetConfig path
        if isNull plugin then None
        else
            let provider = plugin.GetString("provider-name", "")
            if not (provider.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase)) then None
            else
                let mapping = plugin.GetString("table-mapping", "default")
                let schema = plugin.GetString(mapping + ".schema-name", null) |> Option.ofObj
                let table = plugin.GetString($"{mapping}.{kind}.table-name", kind)
                let column = plugin.GetString($"{mapping}.{kind}.columns.persistence-id", "persistence_id")
                Some(provider, plugin.GetString("connection-string", ""), (schema, table, column))

/// Raises, as `checkDatabase` does, unless the databases of the actor system's SQL journal and
/// snapshot store can hold FCQRS data, when they are on SQL Server. Other providers pass unchecked.
let checkPersistence (system: Akka.Actor.ActorSystem) : unit =
    let config = system.Settings.Config.WithFallback(Akka.Persistence.Sql.SqlPersistence.Get(system).DefaultConfig)
    let plugins =
        [ pluginColumn config "akka.persistence.journal.plugin" "akka.persistence.journal.sql" "journal"
          pluginColumn config "akka.persistence.snapshot-store.plugin" "akka.persistence.snapshot-store.sql" "snapshot" ]
        |> List.choose id
    for (provider, connectionString), group in plugins |> List.groupBy (fun (provider, connectionString, _) -> provider, connectionString) do
        let dataProvider =
            match LinqToDB.Data.DataConnection.GetDataProvider(provider, connectionString) with
            | null -> invalidOp $"No data provider is available for the SQL Server provider '{provider}'. Install Microsoft.Data.SqlClient."
            | value -> value
        use connection = dataProvider.CreateConnection connectionString
        connection.Open()
        checkDatabase connection false (group |> List.map (fun (_, _, column) -> column))
