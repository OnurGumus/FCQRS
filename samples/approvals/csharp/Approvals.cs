using System.Data.Common;
using Akka.Persistence.Query;
using Dapper;
using FCQRS.Workflows;

public static class Approvals
{
    // docs:table
    // One row for each quote that waits for its approval, with the time its wait
    // times out. The table is in the journal's database.
    public const string CreateTable =
        """
        create table if not exists quote_approvals (
            id text primary key,
            due text)
        """;
    // docs:end

    // docs:handler
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
    // docs:end
}
