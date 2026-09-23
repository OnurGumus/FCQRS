---
title: Catch up projections
category: Apply
categoryindex: 4
index: 6
---

# Catch up projections

A document export can require every change already saved across the journal, including changes to
other documents. Call `CatchUpAsync` after the aggregate reply, then query the transactional
projection's read model:

```fsharp
let! reply = documents.Send cid documentId (CreateDocument doc) (fun _ -> true)
do! projection.CatchUpAsync(cancellationToken) |> Async.AwaitTask
// Inspect the command outcome, then query the projected documents.
```

<div class="cs-alt"></div>

```csharp
var reply = await documents(
    _ => true, cid, documentId, new CreateDocument(document));
await projection.CatchUpAsync(cancellationToken);
// Inspect the command outcome, then query the projected documents.
```

These transactional projection APIs are available in FCQRS 6.4.0 and later.

`CatchUpAsync` captures one **journal snapshot**: the highest committed sequence number for each
persistence identity visible in one database read. A persistence identity identifies one actor's
journal history. The call succeeds after this projection has committed every event through those
captured sequence numbers. The snapshot includes aggregate events and saga journal records. It
excludes Akka's cluster-sharding records, whose persistence identities start with `/sharding/`.
Akka deletes their early history itself, and they are not application events.

The order matters. Await the aggregate reply before calling `CatchUpAsync` so the snapshot includes
that event when the reply was journaled. It also includes every other event committed before the
snapshot, across all application persistence identities in this journal. Writes arriving after the
snapshot do not extend this call's target.

The checkpoint is durable, so this wait does not require a correlation subscription before sending.
It also works after a deferred reply, although a deferred reply itself adds no journal event. Check
the command outcome separately: projection completion does not turn a rejected command into a
successful one.

The examples use the document types from `samples/getting-started-fsharp/Document.fs` and
`samples/getting-started-csharp/Document.cs`.

## Register a transactional projection

Use `Fcqrs.transactionalProjection` in F# or `AddTransactionalProjection` in C#. These are separate
from the offset-based registration in [Add a projection](add-a-projection.html). The transactional
runner stores one checkpoint per persistence identity under a stable projection name.

The application supplies a SQL connection factory and a handler. FCQRS opens a transaction, passes
its connection and transaction to the handler, records progress, and commits them together. Return
from the handler only when its writes have completed. Use the supplied transaction for every
read-model change covered by this projection.

Use the `Documents` table from [Add a projection](add-a-projection.html). The following SQLite
registration uses one database for the journal, read model, and FCQRS-owned progress tables:

```fsharp
// NuGet: Dapper, Microsoft.Data.Sqlite
open System
open System.Data.Common
open System.Threading.Tasks
open Akka.Persistence.Query
open Dapper
open Microsoft.Data.Sqlite
open FCQRS.Common
open FCQRS.FSharp
open FCQRS.ProjectionStorage
open FCQRS.Projections
open Program

let handle (connection: DbConnection) (transaction: DbTransaction)
           (envelope: EventEnvelope) : Task =
    task {
        match envelope.Event with
        | :? Event<DocumentEvent> as stored ->
            match stored.EventDetails with
            | DocumentCreated doc ->
                let! _ = connection.ExecuteAsync(
                    "insert into Documents (Id, Title, Body) values (@Id, @Title, @Body) " +
                    "on conflict (Id) do update set Title = excluded.Title, Body = excluded.Body",
                    {| Id = doc.Id; Title = doc.Title; Body = doc.Content |},
                    transaction)
                ()
            | DocumentEdited(id, content) ->
                let! rows = connection.ExecuteAsync(
                    "update Documents set Body = @Body where Id = @Id",
                    {| Id = id; Body = content |}, transaction)
                if rows <> 1 then failwith "DocumentEdited requires an earlier DocumentCreated"
            | _ -> ()
        | _ -> ()
    } :> Task

let store =
    SqlProjectionStore(
        ProjectionSqlDialect.Sqlite,
        Func<DbConnection>(fun () -> new SqliteConnection(connectionString) :> DbConnection))
let options = TransactionalProjectionOptions("DocumentProjection", store)
let projection = Fcqrs.transactionalProjection api options handle
```

<div class="cs-alt"></div>

```csharp
// NuGet: Dapper, Microsoft.Data.Sqlite
using System.Data.Common;
using Akka.Persistence.Query;
using Dapper;
using Microsoft.Data.Sqlite;
using FCQRS;
using static FCQRS.Common;
using static FCQRS.ProjectionStorage;
using static FCQRS.Projections;

static async Task Handle(
    DbConnection connection, DbTransaction transaction, EventEnvelope envelope)
{
    if (envelope.Event is not Event<DocumentEvent> stored) return;
    switch (stored.EventDetails)
    {
        case DocumentCreated created:
            await connection.ExecuteAsync(
                "insert into Documents (Id, Title, Body) values (@Id, @Title, @Body) " +
                "on conflict (Id) do update set Title = excluded.Title, Body = excluded.Body",
                new {
                    Id = created.Document.Id,
                    Title = created.Document.Title,
                    Body = created.Document.Content
                }, transaction);
            break;
        case DocumentEdited edited:
            var rows = await connection.ExecuteAsync(
                "update Documents set Body = @Body where Id = @Id",
                new { Id = edited.Id, Body = edited.Content }, transaction);
            if (rows != 1)
                throw new InvalidOperationException("DocumentEdited requires an earlier DocumentCreated");
            break;
    }
}

var store = new SqlProjectionStore(
    ProjectionSqlDialect.Sqlite, () => new SqliteConnection(connectionString));
var options = new TransactionalProjectionOptions("DocumentProjection", store);

builder.Services.AddFcqrs(connectionString, "document-system")
    .AddAggregate<DocumentAggregate>()
    .AddTransactionalProjection(options, Handle);
```

The C# registration adds `IProjection` to dependency injection. Inject it into the caller that waits
for catch-up. The F# facade returns the same interface. It also implements `ISubscribe`, so existing
correlation subscriptions remain available, with aggregate notifications published after commit.

The runner commits persistence identities in key order, not in causal order, so a saga's follow-up
event can commit before the event that caused it. Both carry the same correlation ID. A subscription
for that correlation ID, such as `Subscribe(cid, ...)` or `sendAwaiting`, therefore receives its
notifications after the whole snapshot commits. A waiter woken by the follow-up can read the event
that caused it. It can also wait longer than the commit of its own event, and it is cancelled if the
projection fails before the snapshot commits. A subscription without a correlation ID, including a
filter on the correlation ID, receives each notification when its event commits.

Subscriptions belong to the local worker. If several workers share the same projection name and
store, a worker can observe progress committed by another worker without publishing that other
worker's notifications. Use `CatchUpAsync` for the durable completion boundary across those workers.

The factory must return a new, unopened connection. For separate journal and read-model databases,
use the constructor taking `journalConnectionFactory` and `projectionConnectionFactory`. Both
databases must use the selected dialect. The journal factory must read the authoritative database;
the projection factory must open the store updated by the handler. Journal schema, table, and
persistence-ID and sequence-number column overrides must match the Akka journal configuration.

The runner validates the effective HOCON settings for both SQL journal readers and writers.
`DataOptionsSetup` and `MultiDataOptionsSetup` overrides are unsupported because they can replace
those settings. If `akka.persistence.query.journal.sql.write-plugin` is set, it must identify the
active write journal. Configured Akka event adapters are also unsupported by this reader.

For PostgreSQL, add the Npgsql package to the application, choose `ProjectionSqlDialect.PostgreSql`,
and configure the Akka journal for the same database. The handler above uses SQL accepted by both
SQLite and PostgreSQL:

```fsharp
open Npgsql

let store =
    SqlProjectionStore(
        ProjectionSqlDialect.PostgreSql,
        Func<DbConnection>(fun () -> new NpgsqlConnection(connectionString) :> DbConnection))
let options = TransactionalProjectionOptions("DocumentProjection", store)

let api =
    Fcqrs.actor configuration loggerFactory
        (Some(Fcqrs.connect FCQRS.Actor.DBType.PostgreSQL15 connectionString)) "document-system"
let projection = Fcqrs.transactionalProjection api options handle
```

<div class="cs-alt"></div>

```csharp
var store = new SqlProjectionStore(
    ProjectionSqlDialect.PostgreSql, () => new Npgsql.NpgsqlConnection(connectionString));
var options = new TransactionalProjectionOptions("DocumentProjection", store);

builder.Services.AddFcqrs(
        connectionString, "document-system", FCQRS.Actor.DBType.PostgreSQL15)
    .AddAggregate<DocumentAggregate>()
    .AddTransactionalProjection(options, Handle);
```

The snapshot and completion contract uses per-identity checkpoints on both databases.

FCQRS owns the transaction and progress tables. Keep the handler's connection and transaction inside
the handler, and let FCQRS commit or roll back. Dispose the F# projection handle when its runtime
scope ends; the C# host manages the registered projection's lifetime.

## Understand completion and ordering

Transactional projections handle events in sequence within each persistence identity. They do not
promise a global processing order across identities. A handler combining facts from different
aggregates must tolerate their arrival order or explicitly coordinate those dependencies.

The runner requires a complete history through each captured target. It does not treat the largest
observed sequence number as proof that missing earlier events were handled. Preserve journal events
needed by this projection, and begin with a new read model when starting a new checkpoint history.
An existing global offset cannot establish these per-identity checkpoints.

The completion boundary covers one projection and the database transaction used by its handler.
It does not wait for another projection, an external HTTP call, or work started without awaiting it.
A query can observe later committed changes too; completion does not freeze the read model at the
captured snapshot.

Catch-up suppresses an ambient `TransactionScope` so the snapshot sees freshly committed journal
data. Projection commits belong to FCQRS transactions independently of the caller's transaction;
rolling back the caller's scope does not roll back projection work.

For a user-disable workflow, this wait can establish that the selected projection processed every
event committed before the disable event and the subsequent snapshot. Rejecting later user commands
remains an aggregate or application rule. The wait does not drain commands that were sent earlier
but have not yet been persisted, and it does not stop future writes.

## Configure discovery and waiting

`TransactionalProjectionOptions` configures the runner:

| Property | Default | Meaning |
|---|---|---|
| `PollInterval` | 1 second | Background delay before discovering new journal heads after the previous batch finishes |
| `BatchSize` | 500 | Maximum events fetched for one persistence identity per query |
| `CatchUpTimeout` | 30 seconds | Time allowed for the entire catch-up call, including snapshot capture |

Background discovery follows new events as the application runs. Each `CatchUpAsync` call captures
its own fixed target once; it does not repeatedly replace the target with a newer journal tail.
Discovery queries the journal with `GROUP BY persistence_id` to find each history's head. This query
can be costly for a large journal. Increase `PollInterval` to reduce background query frequency when
the application's latency requirements allow it. Each explicit catch-up call also captures these
heads once.

The catch-up timeout belongs to `TransactionalProjectionOptions`, separately from
`akka.fcqrs.command-timeout` used by [Read your writes](read-your-writes.html).

## Recover without losing progress

Read-model updates and their checkpoint share one transaction. If the handler fails before commit,
neither becomes durable. After restart, use the same projection name and store to resume from the
committed checkpoints. The handler can run again for an event whose transaction did not commit;
external side effects therefore need their own retry and idempotency policy.

An error while processing journal events stops the runner and faults `IProjection.Completion`.
Catch-up calls then report the error. Observe this task in the application's worker or health
monitoring, correct the cause, and restart the projection. Missing sequence numbers stop the runner
instead of silently advancing its checkpoint.

Cancellation or `TimeoutException` ends the caller's wait. It does not undo the aggregate command or read-model
transactions that have already committed. Treat the result as an incomplete confirmation, then
retry catch-up or report that the read model has not yet been confirmed current.

The repository's facade tests cover catch-up and recovery:

```sh
dotnet run --project test/Facade.Tests/Facade.Tests.fsproj
```

Set `FCQRS_TEST_POSTGRES` to a PostgreSQL test-server connection string to include the PostgreSQL
integration cases. The test account needs permission to create and drop the tests' isolated
databases. CI runs these cases against its PostgreSQL service. [Test your domain](test-your-domain.html)
covers tests for the application's own decisions and replay rules.

For rebuilding query data, continue with [Rebuild a read model](rebuild-a-read-model.html). For a
request that needs one matching notification, use [Read your writes](read-your-writes.html).
