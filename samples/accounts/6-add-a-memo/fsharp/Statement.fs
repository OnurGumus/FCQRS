module Statement

open System.Data.Common
open System.Threading.Tasks
open Akka.Persistence.Query
open Dapper
open FCQRS.Common
open Account

// docs:table
// A new read model with a memo column. It is a new table, so the projection
// fills it from the first event in the journal.
let createTable (connection: DbConnection) =
    connection.Execute
        "CREATE TABLE IF NOT EXISTS statement_v2 (
             account TEXT NOT NULL,
             version INTEGER NOT NULL,
             entry TEXT NOT NULL,
             memo TEXT,
             amount NUMERIC NOT NULL,
             balance NUMERIC NOT NULL,
             PRIMARY KEY (account, version))"
    |> ignore
// docs:end

// docs:handle
// Adds a row whose balance continues from the account's previous row.
let private addRow =
    "INSERT INTO statement_v2 (account, version, entry, memo, amount, balance)
     SELECT @Account, @Version, @Entry, @Memo, @Amount,
            COALESCE((SELECT balance FROM statement_v2 WHERE account = @Account
                      ORDER BY version DESC LIMIT 1), 0) + @Amount"

// FCQRS calls this for each stored event, inside a transaction it commits.
let handle (connection: DbConnection) (transaction: DbTransaction)
           (envelope: EventEnvelope) =
    task {
        match envelope.Event with
        // Only account events go on a statement; Sender is the account's ID.
        | :? Event<AccountEvent> as stored ->
            let add (entry: string) (memo: string option) (amount: decimal) =
                let row =
                    {| Account = string stored.Sender.Value
                       Version = envelope.SequenceNr
                       Entry = entry
                       Memo = Option.toObj memo
                       Amount = amount |}
                connection.ExecuteAsync(addRow, row, transaction) :> Task
            match stored.EventDetails with
            | Opened owner -> do! add $"Opened for {owner}" None 0m
            | Deposited amount -> do! add "Deposit" None amount
            | Withdrawn amount -> do! add "Withdrawal" None -amount
            // The same code handles old events, whose memo is None.
            | TransferSent(id, target, amount, memo) ->
                do! add $"Transfer {id} to {target}" memo -amount
            | TransferReceived(id, source, amount, memo) ->
                do! add $"Transfer {id} from {source}" memo amount
            | TransferRefunded(id, _, amount) ->
                do! add $"Refund of transfer {id}" None amount
            // A rejection is a reply only; the journal never holds one.
            | Rejected _ -> ()
        | _ -> ()
    }
    :> Task
// docs:end
