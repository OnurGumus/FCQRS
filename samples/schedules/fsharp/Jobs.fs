module Jobs

open System
open System.Globalization
open System.Threading.Tasks
open FCQRS.Workflows

// docs:services
// The systems the jobs call. Each does its work once per key, so a repeated
// call with the same key changes nothing. Both return whether they did it.
type IBackupStore =
    abstract Copy: key: string * folder: string -> Task<bool>

type IBilling =
    abstract Issue: invoice: string * customer: string -> Task<bool>
// docs:end

// docs:backup
// A run's input is a ScheduledRun: the schedule's ID, the due time the run
// serves, and the schedule's input, here the folder to back up.
let backup (store: IBackupStore) =
    Workflow.define "backup" (fun (run: ScheduledRun<string>) -> workflow {
        // The key holds the run's ID, which holds the due time. It is the same
        // for every attempt of one run and differs between runs.
        return!
            activity "copy" (Retry.times 3) (fun attempt ->
                store.Copy(attempt.IdempotencyKey, run.Input))
    })
// docs:end

// docs:invoice
// The time zone of both schedules.
let zone = "Europe/Oslo"

// The invoice number: the customer and the ISO week of the due time in the
// schedule's zone, such as "Ada-2026-W47".
let invoiceNumber (run: ScheduledRun<string>) =
    let local =
        TimeZoneInfo.ConvertTimeFromUtc(
            run.Due, TimeZoneInfo.FindSystemTimeZoneById zone)
    let week = ISOWeek.GetWeekOfYear local
    $"{run.Input}-{ISOWeek.GetYear local}-W{week:D2}"

let invoice (billing: IBilling) =
    Workflow.define "invoice" (fun (run: ScheduledRun<string>) -> workflow {
        // Keyed by the week, not by the run: a second run in the same week
        // finds the invoice issued.
        return!
            activity "issue" (Retry.times 3) (fun _ ->
                billing.Issue(invoiceNumber run, run.Input))
    })
// docs:end
