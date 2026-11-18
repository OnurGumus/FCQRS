module Approvals

open System.Data.Common
open System.Threading.Tasks
open Akka.Persistence.Query
open Dapper
open FCQRS.Workflows

// docs:table
// One row for each quote that waits for its approval, with the time its wait
// times out. The table is in the journal's database.
let createTable =
    "create table if not exists quote_approvals (
         id text primary key,
         due text)"
// docs:end

// docs:handler
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
// docs:end
