module Statement

open System.Data.Common
open System.Threading.Tasks
open Akka.Persistence.Query
open Dapper
open FCQRS.Common
open Account

// The read model: one row per stored event, with the balance after it.
let createTable (connection: DbConnection) =
    connection.Execute
        "CREATE TABLE IF NOT EXISTS statement (
             account TEXT NOT NULL,
             version INTEGER NOT NULL,
             entry TEXT NOT NULL,
             amount NUMERIC NOT NULL,
             balance NUMERIC NOT NULL,
             PRIMARY KEY (account, version))"
    |> ignore

// Adds a row whose balance continues from the account's previous row.
let private addRow =
    "INSERT INTO statement (account, version, entry, amount, balance)
     SELECT @Account, @Version, @Entry, @Amount,
            COALESCE((SELECT balance FROM statement WHERE account = @Account
                      ORDER BY version DESC LIMIT 1), 0) + @Amount"

// FCQRS calls this for each stored event, inside a transaction it commits.
let handle (connection: DbConnection) (transaction: DbTransaction)
           (envelope: EventEnvelope) =
    task {
        match envelope.Event with
        // Only account events go on a statement; Sender is the account's ID.
        | :? Event<AccountEvent> as stored ->
            let add (entry: string) (amount: decimal) =
                let row =
                    {| Account = string stored.Sender.Value
                       Version = envelope.SequenceNr
                       Entry = entry
                       Amount = amount |}
                connection.ExecuteAsync(addRow, row, transaction) :> Task
            match stored.EventDetails with
            | Opened owner -> do! add $"Opened for {owner}" 0m
            | Deposited amount -> do! add "Deposit" amount
            | Withdrawn amount -> do! add "Withdrawal" -amount
            | TransferSent(id, target, amount) -> do! add $"Transfer {id} to {target}" -amount
            | TransferReceived(id, source, amount) -> do! add $"Transfer {id} from {source}" amount
            | TransferRefunded(id, _, amount) -> do! add $"Refund of transfer {id}" amount
            // A rejection is a reply only; the journal never holds one.
            | Rejected _ -> ()
        | _ -> ()
    }
    :> Task
