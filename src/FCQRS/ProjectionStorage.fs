/// SQL storage for journal-wide, transactional projection catch-up.
module FCQRS.ProjectionStorage

open System
open System.Data
open System.Data.Common
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

/// Database SQL syntax supported by the transactional projection store.
type ProjectionSqlDialect =
    /// SQLite with a provider such as Microsoft.Data.Sqlite.
    | Sqlite = 0
    /// PostgreSQL with a provider such as Npgsql.
    | PostgreSql = 1

/// Stores one durable, contiguous sequence position per projection and persistence ID.
/// Connection factories must return new, unopened connections. The journal factory must
/// read the authoritative journal database, not a replica. Read-model writes must use the
/// connection and transaction supplied to the projection handler, so they commit with progress.
/// Journal history must remain available until every required projection has processed it.
/// Table and column names are quoted as individual identifiers; schemas are separate arguments.
/// The progress table and its companion lock table are owned by FCQRS and must not be edited.
/// Journal source settings are compared conservatively. Password changes are excluded from the
/// persisted source binding so a new store can resume existing progress after credential rotation.
[<Sealed>]
type SqlProjectionStore
    (dialect: ProjectionSqlDialect,
     journalConnectionFactory: Func<DbConnection>,
     projectionConnectionFactory: Func<DbConnection>,
     ?journalTable: string,
     ?journalSchema: string,
     ?progressTable: string,
     ?progressSchema: string,
     ?journalPersistenceIdColumn: string,
     ?journalSequenceNumberColumn: string) =

    let validateIdentifier argument (value: string) =
        if String.IsNullOrWhiteSpace value || value.Contains '\000' then
            invalidArg argument "A SQL identifier must be nonempty and cannot contain a null character."
        // PostgreSQL silently truncates longer identifiers, which can alias another table.
        if dialect = ProjectionSqlDialect.PostgreSql && Encoding.UTF8.GetByteCount value > 63 then
            invalidArg argument "PostgreSQL identifiers must not exceed 63 UTF-8 bytes."
        value

    let quote argument value =
        "\"" + (validateIdentifier argument value).Replace("\"", "\"\"") + "\""

    let qualified argument schema name =
        match schema with
        | Some schema -> quote (argument + "Schema") schema + "." + quote argument name
        | None -> quote argument name

    let journalName = defaultArg journalTable "journal"
    let progressName = defaultArg progressTable "fcqrs_projection_progress"
    let lockName = progressName + "_lock"
    let journalSql = qualified "journalTable" journalSchema journalName
    let progressSql = qualified "progressTable" progressSchema progressName
    let lockSql = qualified "progressTableLock" progressSchema lockName
    let persistenceIdSql = quote "journalPersistenceIdColumn" (defaultArg journalPersistenceIdColumn "persistence_id")
    let sequenceNumberSql = quote "journalSequenceNumberColumn" (defaultArg journalSequenceNumberColumn "sequence_number")

    let normalizedSchema schema =
        match schema with
        | Some value when not (String.IsNullOrWhiteSpace value) -> value
        | _ when dialect = ProjectionSqlDialect.PostgreSql -> "public"
        | _ -> "main"

    let schemaIdentity schema =
        if dialect = ProjectionSqlDialect.Sqlite then
            Some(normalizedSchema schema)
        else
            // PostgreSQL's unqualified lookup follows search_path, which can resolve
            // to a different table from an explicitly qualified public schema.
            schema |> Option.filter (String.IsNullOrWhiteSpace >> not)

    do
        if dialect <> ProjectionSqlDialect.Sqlite && dialect <> ProjectionSqlDialect.PostgreSql then
            invalidArg (nameof dialect) "Only SQLite and PostgreSQL are supported."
        if isNull (box journalConnectionFactory) then nullArg (nameof journalConnectionFactory)
        if isNull (box projectionConnectionFactory) then nullArg (nameof projectionConnectionFactory)
        if String.Equals(normalizedSchema journalSchema, normalizedSchema progressSchema, StringComparison.OrdinalIgnoreCase)
           && (String.Equals(journalName, progressName, StringComparison.OrdinalIgnoreCase)
               || String.Equals(journalName, lockName, StringComparison.OrdinalIgnoreCase)) then
            invalidArg (nameof progressTable) "Projection tables must have different names from the journal table."
        if String.Equals(persistenceIdSql, sequenceNumberSql, StringComparison.OrdinalIgnoreCase) then
            invalidArg (nameof journalSequenceNumberColumn) "Journal persistence ID and sequence number columns must be different."

    let validateName argument (value: string) =
        if String.IsNullOrWhiteSpace value then invalidArg argument "The name must be nonempty."

    let connectionIdentity (connectionString: string) =
        let builder = DbConnectionStringBuilder()
        builder.ConnectionString <- connectionString
        builder.Keys
        |> Seq.cast<string>
        |> Seq.map (fun key ->
            match Convert.ToString(builder.[key], Globalization.CultureInfo.InvariantCulture) with
            | null -> invalidArg (nameof connectionString) "Connection-string values cannot be null."
            | value -> key.ToUpperInvariant(), value)
        |> Map.ofSeq

    let freshConnection (factory: Func<DbConnection>) =
        let connection = factory.Invoke()
        if isNull (box connection) then invalidOp "The projection connection factory returned null."
        if connection.State <> ConnectionState.Closed then
            connection.Dispose()
            invalidOp "Projection connection factories must return new, unopened connections."
        connection

    let journalIdentity =
        lazy
            use connection = freshConnection journalConnectionFactory
            connectionIdentity connection.ConnectionString

    let projectionIdentity =
        lazy
            use connection = freshConnection projectionConnectionFactory
            connectionIdentity connection.ConnectionString

    let sourceFingerprint =
        lazy
            // Length-prefix every component so embedded punctuation cannot alias another source.
            let parts =
                [ string dialect;
                  (match schemaIdentity journalSchema with None -> "unqualified" | Some schema -> "qualified:" + schema);
                  journalName;
                  defaultArg journalPersistenceIdColumn "persistence_id";
                  defaultArg journalSequenceNumberColumn "sequence_number" ]
                @ (journalIdentity.Value
                   |> Map.toList
                   // Authentication secrets can rotate without changing the journal source.
                   // Full credentials are still checked against the active reader/writer config.
                   |> List.filter (fun (key, _) -> key <> "PASSWORD" && key <> "PWD")
                   |> List.collect (fun (key, value) -> [ key; value ]))
            let canonical = parts |> List.map (fun value -> string value.Length + ":" + value) |> String.concat ""
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes canonical))

    let openConnection (factory: Func<DbConnection>) validate (ct: CancellationToken) =
        task {
            ct.ThrowIfCancellationRequested()
            let connection = freshConnection factory
            try
                validate connection
                do! connection.OpenAsync(ct)
                return connection
            with error ->
                connection.Dispose()
                return raise error
        }

    let validateJournalConnection (connection: DbConnection) =
        if connectionIdentity connection.ConnectionString <> journalIdentity.Value then
            invalidOp "The journal connection factory changed its database configuration."

    let validateProjectionConnection (connection: DbConnection) =
        if connectionIdentity connection.ConnectionString <> projectionIdentity.Value then
            invalidOp "The projection connection factory changed its database configuration."

    let parameter (command: DbCommand) name dbType value =
        let parameter = command.CreateParameter()
        parameter.ParameterName <- name
        parameter.DbType <- dbType
        parameter.Value <- value
        command.Parameters.Add parameter |> ignore

    let transactionCommand (connection: DbConnection) (transaction: DbTransaction) sql =
        if isNull (box transaction) || not (obj.ReferenceEquals(connection, transaction.Connection)) then
            invalidArg (nameof transaction) "An active transaction on the supplied connection is required."
        let command = connection.CreateCommand()
        command.Transaction <- transaction
        command.CommandText <- sql
        command

    let readPositions (command: DbCommand) (ct: CancellationToken) =
        task {
            use! reader = command.ExecuteReaderAsync(ct)
            let mutable positions = Map.empty
            let mutable reading = true
            while reading do
                let! available = reader.ReadAsync(ct)
                if available then
                    let persistenceId = reader.GetString 0
                    let sequenceNumber = reader.GetInt64 1
                    if String.IsNullOrWhiteSpace persistenceId || sequenceNumber < 0L then
                        invalidOp "The journal or projection store contains an invalid persistence ID or sequence number."
                    positions <- positions.Add(persistenceId, sequenceNumber)
                else
                    reading <- false
            return positions
        }

    /// Uses one database for both the journal and the transactional read model.
    new(dialect: ProjectionSqlDialect, connectionFactory: Func<DbConnection>) =
        SqlProjectionStore(dialect, connectionFactory, connectionFactory)

    /// Uses separate journal and read-model databases with the default table mappings.
    new(dialect: ProjectionSqlDialect, journalConnectionFactory: Func<DbConnection>, projectionConnectionFactory: Func<DbConnection>) =
        SqlProjectionStore(dialect, journalConnectionFactory, projectionConnectionFactory,
                           ?journalTable = None, ?journalSchema = None,
                           ?progressTable = None, ?progressSchema = None,
                           ?journalPersistenceIdColumn = None, ?journalSequenceNumberColumn = None)

    /// The SQL dialect used by this store.
    member _.Dialect = dialect

    /// Ensures snapshot capture and Akka's per-entity reader address the same journal.
    /// Connection-string comparison deliberately rejects aliases it cannot prove equivalent.
    member internal _.ValidateJournal(expectedConnectionString: string, expectedTable: string, expectedSchema: string option,
                                      expectedPersistenceIdColumn: string, expectedSequenceColumn: string) =
        if connectionIdentity expectedConnectionString <> journalIdentity.Value
           || expectedTable <> journalName
           || schemaIdentity expectedSchema <> schemaIdentity journalSchema
           || expectedPersistenceIdColumn <> defaultArg journalPersistenceIdColumn "persistence_id"
           || expectedSequenceColumn <> defaultArg journalSequenceNumberColumn "sequence_number" then
            invalidArg "store" "The projection store must use the same connection string, schema, table and columns as Akka's SQL journal reader."

    /// Creates a fresh read-model connection. The caller owns and disposes it.
    member internal _.OpenProjectionAsync(ct: CancellationToken) : Task<DbConnection> =
        openConnection projectionConnectionFactory validateProjectionConnection ct

    /// Creates FCQRS-owned progress and locking tables without altering the journal.
    member internal _.InitializeAsync(ct: CancellationToken) : Task =
        task {
            use! connection = openConnection projectionConnectionFactory validateProjectionConnection ct
            use! transaction = connection.BeginTransactionAsync(ct)
            if dialect = ProjectionSqlDialect.PostgreSql then
                // IF NOT EXISTS alone can still race on PostgreSQL catalog entries.
                // This transaction-scoped, database-local lock only serializes FCQRS DDL.
                use initializeLock = transactionCommand connection transaction "SELECT pg_advisory_xact_lock(@lock)"
                parameter initializeLock "@lock" DbType.Int64 0x464351525350524AL
                let! _ = initializeLock.ExecuteScalarAsync(ct)
                ()
            use createProgress =
                transactionCommand connection transaction ($"CREATE TABLE IF NOT EXISTS {progressSql} (projection_name TEXT NOT NULL, persistence_id TEXT NOT NULL, sequence_number BIGINT NOT NULL CHECK (sequence_number >= 0), PRIMARY KEY (projection_name, persistence_id))")
            let! _ = createProgress.ExecuteNonQueryAsync(ct)
            use createLock =
                transactionCommand connection transaction ($"CREATE TABLE IF NOT EXISTS {lockSql} (projection_name TEXT NOT NULL PRIMARY KEY, source_fingerprint TEXT NOT NULL)")
            let! _ = createLock.ExecuteNonQueryAsync(ct)
            do! transaction.CommitAsync(ct)
        }

    /// Captures a fixed committed snapshot using one SELECT on a fresh authoritative connection.
    /// Per-persistence-ID targets do not assume global journal ordering follows commit ordering.
    /// Akka cluster sharding stores its own bookkeeping, such as remembered saga entities, under
    /// persistence IDs that start with "/sharding/", and deletes their early history after each of
    /// its snapshots. Those records are not application events, so the snapshot excludes them.
    member internal _.CaptureAsync(ct: CancellationToken) : Task<Map<string, int64>> =
        task {
            use! connection = openConnection journalConnectionFactory validateJournalConnection ct
            use command = connection.CreateCommand()
            command.CommandText <- $"SELECT {persistenceIdSql}, MAX({sequenceNumberSql}) FROM {journalSql} GROUP BY {persistenceIdSql}"
            let! positions = readPositions command ct
            return positions |> Map.filter (fun persistenceId _ -> not (persistenceId.StartsWith("/sharding/", StringComparison.Ordinal)))
        }

    /// Reads committed progress for a batch. Recheck an individual position under the lock before applying an event.
    member internal this.ReadPositionsAsync(projectionName: string, ct: CancellationToken) : Task<Map<string, int64>> =
        task {
            validateName (nameof projectionName) projectionName
            use! connection = openConnection projectionConnectionFactory validateProjectionConnection ct
            use! transaction = connection.BeginTransactionAsync(ct)
            // Bind before trusting existing progress, even if there are no new events to apply.
            do! this.LockProjectionAsync(connection, transaction, projectionName, ct)
            use command =
                transactionCommand connection transaction ($"SELECT persistence_id, sequence_number FROM {progressSql} WHERE projection_name = @projection")
            parameter command "@projection" DbType.String projectionName
            let! positions = readPositions command ct
            do! transaction.CommitAsync(ct)
            return positions
        }

    /// Serializes this projection's transactions across processes until commit or rollback.
    /// Acquire the lock before reading a position or invoking the read-model handler.
    member internal _.LockProjectionAsync(connection: DbConnection, transaction: DbTransaction, projectionName: string, ct: CancellationToken) : Task =
        task {
            validateName (nameof projectionName) projectionName
            use insert =
                transactionCommand connection transaction ($"INSERT INTO {lockSql} (projection_name, source_fingerprint) VALUES (@projection, @source) ON CONFLICT (projection_name) DO NOTHING")
            parameter insert "@projection" DbType.String projectionName
            parameter insert "@source" DbType.String sourceFingerprint.Value
            let! _ = insert.ExecuteNonQueryAsync(ct)
            // PostgreSQL obtains a row lock; SQLite obtains its single writer lock.
            use acquire =
                transactionCommand connection transaction ($"UPDATE {lockSql} SET projection_name = @projection WHERE projection_name = @projection AND source_fingerprint = @source")
            parameter acquire "@projection" DbType.String projectionName
            parameter acquire "@source" DbType.String sourceFingerprint.Value
            let! changed = acquire.ExecuteNonQueryAsync(ct)
            if changed <> 1 then invalidOp "This projection name is already bound to a different journal source. Use a new projection name and rebuild the read model."
        }

    /// Reads one position inside the transaction that will apply its next event.
    member internal _.ReadPositionAsync(connection: DbConnection, transaction: DbTransaction, projectionName: string, persistenceId: string, ct: CancellationToken) : Task<int64> =
        task {
            validateName (nameof projectionName) projectionName
            validateName (nameof persistenceId) persistenceId
            use command =
                transactionCommand connection transaction ($"SELECT sequence_number FROM {progressSql} WHERE projection_name = @projection AND persistence_id = @persistenceId")
            parameter command "@projection" DbType.String projectionName
            parameter command "@persistenceId" DbType.String persistenceId
            let! value = command.ExecuteScalarAsync(ct)
            if isNull value || Convert.IsDBNull value then
                return 0L
            else
                let position = Convert.ToInt64 value
                if position < 0L then invalidOp "The projection store contains a negative sequence number."
                return position
        }

    /// Advances exactly one contiguous event inside the handler's transaction.
    /// A changed or noncontiguous position fails the transaction instead of certifying an incomplete prefix.
    member internal _.WritePositionAsync(connection: DbConnection, transaction: DbTransaction, projectionName: string, persistenceId: string, expected: int64, sequenceNumber: int64, ct: CancellationToken) : Task =
        task {
            validateName (nameof projectionName) projectionName
            validateName (nameof persistenceId) persistenceId
            if expected < 0L || expected = Int64.MaxValue || sequenceNumber <> expected + 1L then
                invalidArg (nameof sequenceNumber) "A projection position must advance by exactly one event."
            use insert =
                transactionCommand connection transaction ($"INSERT INTO {progressSql} (projection_name, persistence_id, sequence_number) VALUES (@projection, @persistenceId, 0) ON CONFLICT (projection_name, persistence_id) DO NOTHING")
            parameter insert "@projection" DbType.String projectionName
            parameter insert "@persistenceId" DbType.String persistenceId
            let! _ = insert.ExecuteNonQueryAsync(ct)
            use update =
                transactionCommand connection transaction ($"UPDATE {progressSql} SET sequence_number = @next WHERE projection_name = @projection AND persistence_id = @persistenceId AND sequence_number = @expected")
            parameter update "@next" DbType.Int64 sequenceNumber
            parameter update "@projection" DbType.String projectionName
            parameter update "@persistenceId" DbType.String persistenceId
            parameter update "@expected" DbType.Int64 expected
            let! changed = update.ExecuteNonQueryAsync(ct)
            if changed <> 1 then invalidOp "The projection position changed during its transaction."
        }
