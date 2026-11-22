---
title: Find workflows that wait for a signal
category: Apply
categoryindex: 5
index: 11
---

# Find workflows that wait for a signal

The quote workflow in [Write a workflow](write-a-workflow.html) waits for an `approval` signal when
a quote is over 1000. Whoever approves quotes needs two answers: whether one quote waits for
approval, and which quotes wait. `Status` answers the first by asking that instance. A projection
over the workflow's journal answers the second, and a second projection lists every instance with
its status.

The complete programs are in `samples/approvals/fsharp` and `samples/approvals/csharp`. They compile
the quote workflow from `samples/workflows` unchanged, reference the projects under `src/` and
Dapper 2.1.66, and need FCQRS.Workflows 7.10.0 or later for `IsWaitingFor` and
`WorkflowJournal.InstanceOf`. The C# program also needs FCQRS 7.3.0 or later for
`AddKeyedTransactionalProjection`.

## Check one quote before approving it

<!-- sample: approvals/fsharp Program.fs approve -->
```fsharp
// Approves one quote. Status asks the instance, which replays its journal
// first when it is not in memory.
let approve id =
    async {
        let! status = quotes.Status id
        if status.IsWaitingFor "approval" then
            do! Workflows.signal quotes id "approval" true
            return true
        else
            return false
    }
```

<div class="cs-alt"></div>

<!-- sample: approvals/csharp Program.cs approve -->
```csharp
// Approves one quote. StatusAsync asks the instance, which replays its journal
// first when it is not in memory.
async Task<bool> Approve(string id)
{
    var status = await quotes.StatusAsync(id);
    if (!status.IsWaitingFor("approval"))
        return false;
    await quotes.SignalAsync(id, "approval", true);
    return true;
}
```

`Status` gives `Running` with the steps the body waits on. A signal wait's step is named after its
signal, so `IsWaitingFor "approval"` is true while the quote waits for this signal.

- **The check tells the caller whether an approval is expected.** A signal is stored even when no
  wait is pending: one sent to a quote that is still fetching its price is taken when the wait
  starts. One sent to a quote that has ended raises `InvalidOperationException`.
- **The answer can be out of date when the signal arrives.** A wait that times out between the two
  calls ends the quote unapproved, and the signal then raises `InvalidOperationException`. Catch it
  where the approver's request is handled.
- **`Status` asks the instance.** One that is not in memory replays its journal and stays in memory
  until it has had no request for `akka.fcqrs.workflow-idle-timeout`. Ask about one ID at a time;
  to find the waiting quotes, use the projection below.

## List the quotes that wait

A workflow stores the start and the end of each step in its journal, as `WorkflowRecord` events, so
a projection can read them like an aggregate's events. This one keeps a table of the quotes that
wait for approval, in the journal's database:

<!-- sample: approvals/fsharp Approvals.fs table -->
```fsharp
// One row for each quote that waits for its approval, with the time its wait
// times out. The table is in the journal's database.
let createTable =
    "create table if not exists quote_approvals (
         id text primary key,
         due text)"
```

<div class="cs-alt"></div>

<!-- sample: approvals/csharp Approvals.cs table -->
```csharp
// One row for each quote that waits for its approval, with the time its wait
// times out. The table is in the journal's database.
public const string CreateTable =
    """
    create table if not exists quote_approvals (
        id text primary key,
        due text)
    """;
```

The handler adds a row when the wait starts and deletes it when the wait or the quote ends:

<!-- sample: approvals/fsharp Approvals.fs handler -->
```fsharp
let private waits =
    "insert or replace into quote_approvals (id, due) values (@Id, @Due)"
let private ends = "delete from quote_approvals where id = @Id"

let handle (connection: DbConnection) (transaction: DbTransaction)
           (envelope: EventEnvelope) : Task =
    task {
        match envelope.Event with
        | :? WorkflowRecord as record ->
            // A workflow record does not hold its instance's ID. The
            // persistence ID it is journaled under does.
            let quote = WorkflowJournal.InstanceOf envelope.PersistenceId
            let run (sql: string) (row: obj) =
                connection.ExecuteAsync(sql, row, transaction) :> Task
            match record with
            // The wait for "approval" starts. Its detail is when it times
            // out, and None for a wait without a timeout.
            | WorkflowRecord.StepScheduled(_, "approval", _, _, detail) ->
                do! run waits {| Id = quote.Id; Due = Option.toObj detail |}
            // The wait ends: the signal arrived, the time ran out, or the
            // quote was cancelled.
            | WorkflowRecord.StepCompleted(_, "approval", _, _, _)
            | WorkflowRecord.StepFailed(_, "approval", _, _, _)
            // The quote ended in some other way.
            | WorkflowRecord.Finished _
            | WorkflowRecord.Faulted _
            | WorkflowRecord.Cancelled _ ->
                do! run ends {| Id = quote.Id |}
            | _ -> ()
        | _ -> ()
    }
```

<div class="cs-alt"></div>

<!-- sample: approvals/csharp Approvals.cs handler -->
```csharp
private const string Waits =
    "insert or replace into quote_approvals (id, due) values (@Id, @Due)";
private const string Ends = "delete from quote_approvals where id = @Id";

public static async Task Handle(
    DbConnection connection, DbTransaction transaction, EventEnvelope envelope)
{
    if (envelope.Event is not WorkflowRecord record)
        return;
    // A workflow record does not hold its instance's ID. The
    // persistence ID it is journaled under does.
    var quote = WorkflowJournal.InstanceOf(envelope.PersistenceId);
    switch (record)
    {
        // The wait for "approval" starts. Its detail is when it times
        // out, and null for a wait without a timeout.
        case WorkflowRecord.StepScheduled { name: "approval" } scheduled:
            var row = new { quote.Id, Due = scheduled.detail?.Value };
            await connection.ExecuteAsync(Waits, row, transaction);
            break;
        // The wait ends: the signal arrived, the time ran out, or the
        // quote was cancelled.
        case WorkflowRecord.StepCompleted { name: "approval" }:
        case WorkflowRecord.StepFailed { name: "approval" }:
        // The quote ended in some other way.
        case WorkflowRecord.Finished:
        case WorkflowRecord.Faulted:
        case WorkflowRecord.Cancelled:
            await connection.ExecuteAsync(Ends, new { quote.Id }, transaction);
            break;
    }
}
```

- **`StepScheduled` is the start of the wait.** Its detail is the time the wait times out, in
  round-trip format. A wait without a timeout, `waitSignal`, stores `None`.
- **`StepCompleted` ends the wait** when the signal arrives and when the time runs out. A cancel
  stores `StepFailed` for every pending step. The handler also deletes the row when the quote
  ends, so an ended quote is never listed.
- **`WorkflowJournal.InstanceOf` gives the quote's ID.** A record does not hold the ID of its
  instance. The persistence ID it is journaled under does, as
  `<escaped workflow name>/<shard>/<escaped ID>`.
- **The handler matches the step by its name.** Step names are already persistent contracts.
  Renaming the `approval` step parks running quotes and also changes what this handler must match.

Register the handler as a transactional projection that reads only the quote workflow:

<!-- sample: approvals/fsharp Program.fs projection -->
```fsharp
using (new SqliteConnection(connectionString)) (fun connection ->
    connection.Execute Approvals.createTable |> ignore)

// The projection reads only the quote workflow's journal, and commits each
// row with its read position.
let store =
    SqlProjectionStore(
        ProjectionSqlDialect.Sqlite,
        Func<DbConnection>(fun () -> new SqliteConnection(connectionString)))
let options = TransactionalProjectionOptions("quote-approvals", store)
options.Entities <- [ "quote" ]
let approvals = Fcqrs.transactionalProjection api options Approvals.handle
```

<div class="cs-alt"></div>

<!-- sample: approvals/csharp Program.cs projection -->
```csharp
using (var connection = new SqliteConnection(connectionString))
    connection.Execute(Approvals.CreateTable);

// The projection reads only the quote workflow's journal, and commits each
// row with its read position.
var store = new SqlProjectionStore(
    ProjectionSqlDialect.Sqlite, () => new SqliteConnection(connectionString));
var options = new TransactionalProjectionOptions("quote-approvals", store)
{
    Entities = ["quote"]
};

// Keyed, so it runs beside the read model an application already projects.
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
var fcqrs = builder.Services.AddFcqrs(connectionString, "approvals")
    .AddKeyedTransactionalProjection(options, Approvals.Handle);
builder.Services.AddSingleton<IPriceService, FixedPriceService>();
builder.Services.AddSingleton<IMailer, QuietMailer>();
builder.Services.AddWorkflow<QuoteWorkflow, QuoteRequest, Quote>();
```

A C# host registers one projection with `AddProjection` or `AddTransactionalProjection`, and an
application often uses it for its own read model already. `AddKeyedTransactionalProjection` adds
this one beside it, and the program resolves it with
`GetRequiredKeyedService<IProjection>("quote-approvals")`. In F#, each
`Fcqrs.transactionalProjection` call starts another projection.

The entity name is the workflow's name, `quote`. Instances journal under it, so the projection
reads no other workflow's or aggregate's events. FCQRS commits each row with the projection's read
position for that instance, so a record is applied once even when the process stops in between.
The projection's name, `quote-approvals`, identifies that progress. A new name reads the whole
journal again. [Catch up projections](catch-up-projections.html) explains the transactional
projection.

The list is a query on the table:

<!-- sample: approvals/fsharp Program.fs list -->
```fsharp
// The quotes that wait for a person. The list lags the workflows by one
// projection pass.
let waiting () =
    use connection = new SqliteConnection(connectionString)
    connection.Query<string>("select id from quote_approvals order by id")
    |> List.ofSeq
```

<div class="cs-alt"></div>

<!-- sample: approvals/csharp Program.cs list -->
```csharp
// The quotes that wait for a person. The list lags the workflows by one
// projection pass.
List<string> Waiting()
{
    using var connection = new SqliteConnection(connectionString);
    return connection
        .Query<string>("select id from quote_approvals order by id")
        .ToList();
}
```

The table lags the workflows by one projection pass. `CatchUpAsync` waits until the projection has
handled every record stored before the call, and the sample calls it before each query. A listed
quote can stop waiting before an approver acts on it, so approve through the check above.

## List every instance and its status

The same records give each instance's status. This projection keeps one row per instance:

<!-- sample: approvals/fsharp Instances.fs row -->
```fsharp
// A row, as the list reads it.
[<CLIMutable>]
type Instance = {
    Workflow: string
    Id: string
    Status: string
    LastStep: string | null
}
```

<div class="cs-alt"></div>

<!-- sample: approvals/csharp Instances.cs row -->
```csharp
// A row, as the list reads it.
public sealed record Instance(
    string Workflow, string Id, string Status, string? LastStep);
```

<!-- sample: approvals/fsharp Instances.fs table -->
```fsharp
// One row for each workflow instance: its status and the last step it
// started. The table is in the journal's database.
let createTable =
    "create table if not exists workflow_instances (
         workflow text not null,
         id text not null,
         status text not null,
         last_step text,
         updated text not null,
         primary key (workflow, id))"
```

<div class="cs-alt"></div>

<!-- sample: approvals/csharp Instances.cs table -->
```csharp
// One row for each workflow instance: its status and the last step it
// started. The table is in the journal's database.
public const string CreateTable =
    """
    create table if not exists workflow_instances (
        workflow text not null,
        id text not null,
        status text not null,
        last_step text,
        updated text not null,
        primary key (workflow, id))
    """;
```

<!-- sample: approvals/fsharp Instances.fs handler -->
```fsharp
let private started =
    "insert or replace into workflow_instances
         (workflow, id, status, last_step, updated)
     values (@Workflow, @Id, 'running', null, @At)"
let private stepped =
    "update workflow_instances set last_step = @Step, updated = @At
     where workflow = @Workflow and id = @Id"
let private ended =
    "update workflow_instances set status = @Status, updated = @At
     where workflow = @Workflow and id = @Id"

let handle (connection: DbConnection) (transaction: DbTransaction)
           (envelope: EventEnvelope) : Task =
    task {
        match envelope.Event with
        | :? WorkflowRecord as record ->
            let instance = WorkflowJournal.InstanceOf envelope.PersistenceId
            let run (sql: string) (at: DateTime) (fields: (string * obj) list) =
                let row = DynamicParameters()
                row.Add("Workflow", instance.Workflow)
                row.Add("Id", instance.Id)
                row.Add("At", at.ToString "O")
                for name, value in fields do
                    row.Add(name, value)
                connection.ExecuteAsync(sql, row, transaction) :> Task
            match record with
            | WorkflowRecord.Started(_, at, _, _) -> do! run started at []
            | WorkflowRecord.StepScheduled(_, step, _, at, _) ->
                do! run stepped at [ "Step", box step ]
            | WorkflowRecord.Resumed(_, at) ->
                do! run ended at [ "Status", box "running" ]
            | WorkflowRecord.CancelRequested at ->
                do! run ended at [ "Status", box "cancelling" ]
            | WorkflowRecord.Finished(_, at) ->
                do! run ended at [ "Status", box "finished" ]
            | WorkflowRecord.Faulted(_, at) ->
                do! run ended at [ "Status", box "faulted" ]
            | WorkflowRecord.Cancelled at ->
                do! run ended at [ "Status", box "cancelled" ]
            | _ -> ()
        | _ -> ()
    }
```

<div class="cs-alt"></div>

<!-- sample: approvals/csharp Instances.cs handler -->
```csharp
private const string Started =
    """
    insert or replace into workflow_instances
        (workflow, id, status, last_step, updated)
    values (@Workflow, @Id, 'running', null, @At)
    """;
private const string Stepped =
    """
    update workflow_instances set last_step = @Step, updated = @At
    where workflow = @Workflow and id = @Id
    """;
private const string Ended =
    """
    update workflow_instances set status = @Status, updated = @At
    where workflow = @Workflow and id = @Id
    """;

public static async Task Handle(
    DbConnection connection, DbTransaction transaction, EventEnvelope envelope)
{
    if (envelope.Event is not WorkflowRecord record)
        return;
    var instance = WorkflowJournal.InstanceOf(envelope.PersistenceId);

    Task Run(
        string sql, DateTime at, string? step = null, string? status = null)
    {
        var row = new
        {
            instance.Workflow, instance.Id, At = at.ToString("O"),
            Step = step, Status = status
        };
        return connection.ExecuteAsync(sql, row, transaction);
    }

    switch (record)
    {
        case WorkflowRecord.Started started:
            await Run(Started, started.at);
            break;
        case WorkflowRecord.StepScheduled scheduled:
            await Run(Stepped, scheduled.at, step: scheduled.name);
            break;
        case WorkflowRecord.Resumed resumed:
            await Run(Ended, resumed.at, status: "running");
            break;
        case WorkflowRecord.CancelRequested requested:
            await Run(Ended, requested.at, status: "cancelling");
            break;
        case WorkflowRecord.Finished finished:
            await Run(Ended, finished.at, status: "finished");
            break;
        case WorkflowRecord.Faulted faulted:
            await Run(Ended, faulted.at, status: "faulted");
            break;
        case WorkflowRecord.Cancelled cancelled:
            await Run(Ended, cancelled.at, status: "cancelled");
            break;
    }
}
```

`Started` adds the row, `StepScheduled` records the step the instance started last, and
`Resumed`, `CancelRequested`, `Finished`, `Faulted`, and `Cancelled` set the status. `WorkflowJournal.InstanceOf`
gives the workflow name too, so one projection can list several workflows: name each one in
`Entities`.

Register it as a second projection, with its own name:

<!-- sample: approvals/fsharp Program.fs instances -->
```fsharp
using (new SqliteConnection(connectionString)) (fun connection ->
    connection.Execute Instances.createTable |> ignore)

// A second projection, with its own name and progress.
let instanceOptions =
    TransactionalProjectionOptions("workflow-instances", store)
instanceOptions.Entities <- [ "quote" ]
let instances =
    Fcqrs.transactionalProjection api instanceOptions Instances.handle

let listInstances () =
    use connection = new SqliteConnection(connectionString)
    connection.Query<Instances.Instance>(
        "select workflow, id, status, last_step as LastStep
         from workflow_instances order by workflow, id")
    |> List.ofSeq
```

<div class="cs-alt"></div>

<!-- sample: approvals/csharp Program.cs instances -->
```csharp
using (var connection = new SqliteConnection(connectionString))
    connection.Execute(Instances.CreateTable);

// A second projection, with its own name and progress.
var instanceOptions =
    new TransactionalProjectionOptions("workflow-instances", store)
    {
        Entities = ["quote"]
    };
fcqrs.AddKeyedTransactionalProjection(instanceOptions, Instances.Handle);

List<Instance> ListInstances()
{
    using var connection = new SqliteConnection(connectionString);
    return connection
        .Query<Instance>(
            """
            select workflow, id, status, last_step as LastStep
            from workflow_instances order by workflow, id
            """)
        .ToList();
}
```

- **A parked instance is listed as `running`.** An instance is parked when its journal does not
  match its code. That is decided when the instance replays and is not stored, so no record tells
  the projection. `Status` reports `Parked` with the reason, the `fcqrs.workflow.parked` gauge
  counts the parked instances in memory, and each one logs an error in the `FCQRS.Workflows`
  category. See [Observe your system](observability.html#Workflows).
- **A `running` row survives a stop.** An instance that was running when the process stopped
  resumes when its workflow is registered at the next start, and its row goes on from there.
  Nothing restarts a `faulted` or `cancelled` instance by itself. `Resume` continues a faulted
  one at its failed step, and its `Resumed` record sets the row back to `running`. A start with
  `WorkflowIdReuse.AfterFailure` begins a new run under the ID, and the run's `Started` record does
  the same.

## Run the sample

```bash
dotnet run --project samples/approvals/fsharp -c Release
```

The program starts three quotes, waits until two of them have reached the approval, approves
two, and lists the instances:

```text
waiting for approval: [q-2; q-3]
approve q-1: false
approve q-2: true
q-2: quote of 1250.0 sent to Ada
waiting for approval: [q-3]
quote q-1: finished (last step send-quote)
quote q-2: finished (last step send-quote)
quote q-3: running (last step approval)
```

- `q-1` costs 125, so it ended without a wait, and the check refused to approve it.
- `q-2` left the list when its wait completed.
- `q-3` waits until it is approved or its day runs out. Each run of the sample deletes its database
  first, so the output is the same every time.

Use `samples/approvals/csharp` for the C# program.

## Record the request in a step

When the request for approval is a business fact, such as a task in another system or an email to
the approver, send it from an activity just before the wait. The activity runs at least once, so
the system it calls must honour its idempotency key. The body does not know its own workflow ID, so
put the ID that the approver signals, such as a quote number, in the workflow's input.

Adding that step to a workflow whose instances are running changes the steps those instances
stored. Follow [Change a workflow while instances are
running](../concepts/workflows.html#Change-a-workflow-while-instances-are-running).

Next: [Apply only the newest change](apply-the-newest-change.html) uses signals to apply only the
newest of the changes that arrive while one is applied.
