using System.Data.Common;
using Akka.Persistence.Query;
using Dapper;
using FCQRS.Workflows;

// docs:row
// A row, as the list reads it.
public sealed record Instance(
    string Workflow, string Id, string Status, string? LastStep);
// docs:end

public static class Instances
{
    // docs:table
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
    // docs:end

    // docs:handler
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
    // docs:end
}
