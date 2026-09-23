using System.Data.Common;
using Akka.Persistence.Query;
using Dapper;
using static FCQRS.Common;

public static class Statement
{
    // The read model: one row per stored event, with the balance after it.
    public static void CreateTable(DbConnection connection) =>
        connection.Execute(
            """
            CREATE TABLE IF NOT EXISTS statement (
                account TEXT NOT NULL,
                version INTEGER NOT NULL,
                entry TEXT NOT NULL,
                amount NUMERIC NOT NULL,
                balance NUMERIC NOT NULL,
                PRIMARY KEY (account, version))
            """);

    // Adds a row whose balance continues from the account's previous row.
    const string AddRow =
        """
        INSERT INTO statement (account, version, entry, amount, balance)
        SELECT @Account, @Version, @Entry, @Amount,
               COALESCE((SELECT balance FROM statement WHERE account = @Account
                         ORDER BY version DESC LIMIT 1), 0) + @Amount
        """;

    // FCQRS calls this for each stored event, inside a transaction it commits.
    public static async Task Handle(
        DbConnection connection, DbTransaction transaction, EventEnvelope envelope)
    {
        // Only account events go on a statement; Sender is the account's ID.
        if (envelope.Event is not Event<AccountEvent> { Sender: { } sender } stored)
            return;

        Task Add(string entry, decimal amount)
        {
            var row = new
            {
                Account = sender.Value.ToString(),
                Version = envelope.SequenceNr,
                Entry = entry,
                Amount = amount
            };
            return connection.ExecuteAsync(AddRow, row, transaction);
        }

        await (stored.EventDetails switch
        {
            Opened opened => Add($"Opened for {opened.Owner}", 0m),
            Deposited deposited => Add("Deposit", deposited.Amount),
            Withdrawn withdrawn => Add("Withdrawal", -withdrawn.Amount),
            TransferSent sent =>
                Add($"Transfer {sent.TransferId} to {sent.Target}", -sent.Amount),
            TransferReceived received =>
                Add($"Transfer {received.TransferId} from {received.Source}", received.Amount),
            TransferRefunded refunded =>
                Add($"Refund of transfer {refunded.TransferId}", refunded.Amount),
            // A rejection is a reply only; the journal never holds one.
            Rejected => Task.CompletedTask
        });
    }
}
