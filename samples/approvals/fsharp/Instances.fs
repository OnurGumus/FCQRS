module Instances

open System
open System.Data.Common
open System.Threading.Tasks
open Akka.Persistence.Query
open Dapper
open FCQRS.Workflows

// docs:row
// A row, as the list reads it.
[<CLIMutable>]
type Instance = {
    Workflow: string
    Id: string
    Status: string
    LastStep: string | null
}
// docs:end

// docs:table
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
// docs:end

// docs:handler
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
// docs:end
