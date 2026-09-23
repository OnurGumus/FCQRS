using System.Data.Common;
using Akka.Persistence.Query;
using Dapper;
using static FCQRS.Common;

public static class Statement
{
    // docs:table
    // A new read model with a memo column. It is a new table, so the projection
    // fills it from the first event in the journal.
    public static void CreateTable(DbConnection connection) =>
        connection.Execute(
            """
            CREATE TABLE IF NOT EXISTS statement_v2 (
                account TEXT NOT NULL,
                version INTEGER NOT NULL,
                entry TEXT NOT NULL,
                memo TEXT,
                amount NUMERIC NOT NULL,
                balance NUMERIC NOT NULL,
                PRIMARY KEY (account, version))
            """);
    // docs:end

    // docs:handle
    // Adds a row whose balance continues from the account's previous row.
    const string AddRow =
        """
        INSERT INTO statement_v2 (account, version, entry, memo, amount, balance)
        SELECT @Account, @Version, @Entry, @Memo, @Amount,
               COALESCE((SELECT balance FROM statement_v2 WHERE account = @Account
                         ORDER BY version DESC LIMIT 1), 0) + @Amount
        """;

    // FCQRS calls this for each stored event, inside a transaction it commits.
    public static async Task Handle(
        DbConnection connection, DbTransaction transaction, EventEnvelope envelope)
    {
        // Only account events go on a statement; Sender is the account's ID.
        if (envelope.Event is not Event<AccountEvent> { Sender: { } sender } stored)
            return;

        Task Add(string entry, string? memo, decimal amount)
        {
            var row = new
            {
                Account = sender.Value.ToString(),
                Version = envelope.SequenceNr,
                Entry = entry,
                Memo = memo,
                Amount = amount
            };
            return connection.ExecuteAsync(AddRow, row, transaction);
        }

        await (stored.EventDetails switch
        {
            Opened opened => Add($"Opened for {opened.Owner}", null, 0m),
            Deposited deposited => Add("Deposit", null, deposited.Amount),
            Withdrawn withdrawn => Add("Withdrawal", null, -withdrawn.Amount),
            // The same code handles old events, whose memo is null.
            TransferSent sent => Add(
                $"Transfer {sent.TransferId} to {sent.Target}",
                sent.Memo, -sent.Amount),
            TransferReceived received => Add(
                $"Transfer {received.TransferId} from {received.Source}",
                received.Memo, received.Amount),
            TransferRefunded refunded => Add(
                $"Refund of transfer {refunded.TransferId}", null, refunded.Amount),
            // A rejection is a reply only; the journal never holds one.
            Rejected => Task.CompletedTask
        });
    }
    // docs:end
}
