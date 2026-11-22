/// Creates the Akka.Persistence.Sql tables on Oracle, which Akka cannot do safely by itself.
module internal FCQRS.Oracle

open System
open System.Data.Common

// Akka.Persistence.Sql creates its Oracle tables through linq2db, which has two faults there. Its
// "create if not exists" guards only the table, so the sequence and trigger that emulate the
// journal's identity are created again at each start, and the second start fails with ORA-00955.
// And Akka adds no unique key or index on Oracle: two writers could store the same sequence number
// of one persistence ID. FCQRS therefore turns Akka's own creation off on Oracle and creates the
// same tables, under the names linq2db gives them, with the key and indexes Akka adds elsewhere.
//
// Tags are kept in the journal's own column (tag mode Csv), not in a tag table. With a tag table,
// Akka's read journal wraps each ordered page in a query that adds the tags, and Oracle does not keep
// the inner order through it: a page can end on an earlier event, and the next page then repeats
// events. FCQRS stores no tags, so the column stays empty.

/// An Oracle identifier in double quotes: the tables are created with lower-case names.
let private quote (name: string) = "\"" + name.Replace("\"", "\"\"") + "\""

// A command for the caller to dispose.
let private execute (connection: DbConnection) (sql: string) (parameters: (string * obj) list) : DbCommand =
    let command = connection.CreateCommand()
    command.CommandText <- sql
    for name, value in parameters do
        let parameter = command.CreateParameter()
        parameter.ParameterName <- name
        parameter.Value <- value
        command.Parameters.Add parameter |> ignore
    command

let private count (connection: DbConnection) (sql: string) (parameters: (string * obj) list) : int =
    use command = execute connection sql parameters
    Convert.ToInt32(command.ExecuteScalar())

let private run (connection: DbConnection) (sql: string) =
    use command = execute connection sql []
    command.ExecuteNonQuery() |> ignore

/// The table's owner: the configured schema, or the session's.
let private owner (connection: DbConnection) (schema: string option) =
    match schema with
    | Some schema -> schema
    | None ->
        use command = execute connection "SELECT SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') FROM DUAL" []
        string (command.ExecuteScalar())

let private exists (connection: DbConnection) (owner: string) (objectType: string) (name: string) =
    count connection "SELECT COUNT(*) FROM ALL_OBJECTS WHERE OWNER = :owner AND OBJECT_TYPE = :kind AND OBJECT_NAME = :name"
        [ "owner", owner :> obj; "kind", objectType :> obj; "name", name :> obj ] > 0

/// Runs a DDL statement unless it would only repeat a key or index that exists under another name:
/// ORA-00955 (the name is used), ORA-02261 (the key exists), ORA-01408 (the columns are indexed).
let private ensure (connection: DbConnection) (ddl: string) =
    let escaped = ddl.Replace("'", "''")
    run connection
        $"BEGIN EXECUTE IMMEDIATE '{escaped}'; EXCEPTION WHEN OTHERS THEN IF SQLCODE NOT IN (-955, -2261, -1408) THEN RAISE; END IF; END;"

/// A table to create: its name, columns, primary key, and the identity column, if any.
type private Table =
    { Name: string
      Columns: (string * string) list
      Key: string list
      Identity: string option
      Unique: (string * string list) list
      Indexes: (string * string list) list }

let private create (connection: DbConnection) (schema: string option) (table: Table) =
    let owner = owner connection schema
    let qualified name =
        match schema with
        | Some schema -> quote schema + "." + quote name
        | None -> quote name
    let columns name = name |> List.map quote |> String.concat ", "
    if not (exists connection owner "TABLE" table.Name) then
        let primaryKey = quote ("PK_" + table.Name)
        let body =
            (table.Columns |> List.map (fun (name, definition) -> $"{quote name} {definition}"))
            @ [ $"CONSTRAINT {primaryKey} PRIMARY KEY ({columns table.Key})" ]
            |> String.concat ", "
        ensure connection $"CREATE TABLE {qualified table.Name} ({body})"
    match table.Identity with
    | Some column ->
        let sequence = "SIDENTITY_" + table.Name
        let trigger = "TIDENTITY_" + table.Name
        if not (exists connection owner "SEQUENCE" sequence) then
            // Start above any value a table created without the sequence already holds.
            let start =
                use command = execute connection $"SELECT NVL(MAX({quote column}), 0) + 1 FROM {qualified table.Name}" []
                Convert.ToInt64(command.ExecuteScalar())
            ensure connection $"CREATE SEQUENCE {qualified sequence} START WITH {start}"
        if not (exists connection owner "TRIGGER" trigger) then
            ensure connection
                $"CREATE OR REPLACE TRIGGER {qualified trigger} BEFORE INSERT ON {qualified table.Name} FOR EACH ROW BEGIN SELECT {qualified sequence}.NEXTVAL INTO :NEW.{quote column} FROM DUAL; END;"
    | None -> ()
    for name, key in table.Unique do
        if not (exists connection owner "INDEX" name) then
            ensure connection $"ALTER TABLE {qualified table.Name} ADD CONSTRAINT {quote name} UNIQUE ({columns key})"
    for name, key in table.Indexes do
        if not (exists connection owner "INDEX" name) then
            ensure connection $"CREATE INDEX {qualified name} ON {qualified table.Name} ({columns key})"

// A setting of an Akka.Persistence.Sql plugin, read under its table mapping.
type private Plugin =
    { Config: Akka.Configuration.Config
      Path: string
      Mapping: string }

    member plugin.Text(key: string, fallback: string) =
        plugin.Config.GetString($"{plugin.Mapping}.{key}", fallback)

    member plugin.Schema = plugin.Config.GetString(plugin.Mapping + ".schema-name", null) |> Option.ofObj

let private journalTables (plugin: Plugin) =
    let journal = plugin.Text("journal.table-name", "journal")
    let column (name: string) = plugin.Text($"journal.columns.{name}", name.Replace('-', '_'))
    let ordering = column "ordering"
    let persistenceId = column "persistence-id"
    let sequenceNumber = column "sequence-number"
    let created = column "created"
    [ { Name = journal
        Columns =
          [ ordering, "NUMBER(19,0) NOT NULL"
            created, "NUMBER(19,0) NOT NULL"
            column "deleted", "NUMBER(1,0) NOT NULL"
            persistenceId, "VARCHAR2(255) NOT NULL"
            sequenceNumber, "NUMBER(19,0) NOT NULL"
            column "message", "BLOB NOT NULL"
            column "manifest", "VARCHAR2(500)"
            column "identifier", "NUMBER(10,0)"
            column "writer-uuid", "VARCHAR2(128)"
            column "tags", "VARCHAR2(100)" ]
        Key = [ ordering ]
        Identity = Some ordering
        Unique = [ $"UQ_{journal}", [ persistenceId; sequenceNumber ] ]
        Indexes = [ $"IX_{journal}_{sequenceNumber}", [ sequenceNumber ]; $"IX_{journal}_{created}", [ created ] ] } ]

let private snapshotTables (plugin: Plugin) =
    let snapshot = plugin.Text("snapshot.table-name", "snapshot")
    let column (name: string) = plugin.Text($"snapshot.columns.{name}", name.Replace('-', '_'))
    let persistenceId = column "persistence-id"
    let sequenceNumber = column "sequence-number"
    let created = column "created"
    [ { Name = snapshot
        Columns =
          [ persistenceId, "VARCHAR2(255) NOT NULL"
            sequenceNumber, "NUMBER(19,0) NOT NULL"
            created, "NUMBER(19,0) NOT NULL"
            column "snapshot", "BLOB"
            column "manifest", "VARCHAR2(500)"
            column "serializer-id", "NUMBER(10,0)" ]
        Key = [ persistenceId; sequenceNumber ]
        Identity = None
        Unique = []
        Indexes = [ $"IX_{snapshot}_{sequenceNumber}", [ sequenceNumber ]; $"IX_{snapshot}_{created}", [ created ] ] } ]

// The plugin at a path when it is the SQL plugin on Oracle.
let private oraclePlugin (config: Akka.Configuration.Config) (path: string) =
    let plugin = config.GetConfig path
    if isNull plugin then None
    else
        let provider = plugin.GetString("provider-name", "")
        if not (provider.StartsWith("Oracle", StringComparison.OrdinalIgnoreCase)) then None
        else
            if plugin.GetBoolean("auto-initialize", false) then
                invalidOp
                    $"'{path}.auto-initialize' is on with the Oracle provider '{provider}'. Akka.Persistence.Sql cannot create its Oracle tables \
                      at a second start, and creates no unique key on the journal; FCQRS creates them instead. Turn the setting off."
            // Akka's default for both keys is a tag table.
            let tagKey =
                if path.StartsWith "akka.persistence.journal." then Some "tag-write-mode"
                elif path.StartsWith "akka.persistence.query." then Some "tag-read-mode"
                else None
            for key in Option.toList tagKey do
                let mode = plugin.GetString(key, "TagTable")
                if not (mode.Equals("Csv", StringComparison.OrdinalIgnoreCase)) then
                    invalidOp
                        $"'{path}.{key}' is '{mode}' with the Oracle provider '{provider}'; FCQRS needs 'Csv'. With a tag table, \
                          Akka's read journal wraps each ordered page of events in a query that Oracle may return out of order, so a reader \
                          can receive an event twice or skip one."
            Some(provider, plugin.GetString("connection-string", ""), { Config = plugin; Path = path; Mapping = plugin.GetString("table-mapping", "default") })

/// Creates the tables of the actor system's SQL journal and snapshot store when they are on Oracle:
/// those that do not exist, and the unique key and indexes an existing table lacks. A unique key
/// cannot be added to a journal that holds two events with one persistence ID and sequence number;
/// the start then fails. Raises when such a plugin has `auto-initialize` on.
let preparePersistence (system: Akka.Actor.ActorSystem) : unit =
    let config = system.Settings.Config.WithFallback(Akka.Persistence.Sql.SqlPersistence.Get(system).DefaultConfig)
    let pluginAt key expected =
        let path = config.GetString(key, "")
        if path = expected then oraclePlugin config path else None
    let plugins =
        [ pluginAt "akka.persistence.journal.plugin" "akka.persistence.journal.sql" |> Option.map (fun (p, c, plugin) -> p, c, plugin.Schema, journalTables plugin)
          pluginAt "akka.persistence.snapshot-store.plugin" "akka.persistence.snapshot-store.sql" |> Option.map (fun (p, c, plugin) -> p, c, plugin.Schema, snapshotTables plugin) ]
        |> List.choose id
    // The query journal reads the journal's tables; it must not create them either.
    oraclePlugin config "akka.persistence.query.journal.sql" |> ignore
    for provider, connectionString, schema, tables in plugins do
        let dataProvider =
            match LinqToDB.Data.DataConnection.GetDataProvider(provider, connectionString) with
            | null -> invalidOp $"No data provider is available for the Oracle provider '{provider}'. Install Oracle.ManagedDataAccess.Core."
            | value -> value
        use connection = dataProvider.CreateConnection connectionString
        connection.Open()
        for table in tables do
            create connection schema table
