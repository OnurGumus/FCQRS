module Reservation

open System
open System.Threading.Tasks
open FCQRS.Workflows

// docs:messages
// One change to a reservation, as its event describes it. Each change has
// the next version, and the last change of a reservation is final.
type ReservationChanged =
    { Reservation: string
      Version: int
      Final: bool }

// The external system that applies a change. A call gets a key that is the
// same for every attempt of one step.
type IBooking =
    abstract Apply: key: string * change: ReservationChanged -> Task<int>
// docs:end

// docs:fed
// What happened to a change sent to the reservation's workflow.
type Fed =
    // The workflow stored it.
    | Taken
    // The workflow had ended and had applied this version or a newer one:
    // the projection handed the event over again.
    | Repeated
    // The workflow had ended before this version. Nothing will apply it.
    | Late
// docs:end

let private policy = Retry.times 5 (TimeSpan.FromSeconds 30.0)

// docs:workflow
// How long the workflow waits for a reservation's next change.
let quietLimit = TimeSpan.FromDays 30.0

// How long it waits after the booking system failed every attempt.
let retryAfter = TimeSpan.FromMinutes 10.0

// Takes every change that is already stored and keeps the newest. A wait of
// zero takes a stored signal or gives None at once.
let rec newest (current: ReservationChanged) =
    workflow {
        match! waitSignalFor<ReservationChanged> "changed" TimeSpan.Zero with
        | Some next when next.Version > current.Version -> return! newest next
        | Some _ -> return! newest current
        | None -> return current
    }

// Applies the newest stored change. When the booking system fails every
// attempt, it waits and applies the newest change again, so the instance
// keeps taking signals.
let rec applyNewest (booking: IBooking) (change: ReservationChanged) =
    workflow {
        let! latest = newest change
        try
            let! _ =
                activity "apply" policy (fun attempt ->
                    booking.Apply(attempt.IdempotencyKey, latest))
            return latest
        with :? WorkflowStepFailedException ->
            let! next = waitSignalFor<ReservationChanged> "changed" retryAfter
            match next with
            | Some newer when newer.Version > latest.Version ->
                return! applyNewest booking newer
            | _ -> return! applyNewest booking latest
    }

// Applies one change at a time. Changes that arrive while one is applied
// are stored, and only the newest of them is applied next.
let rec follow (booking: IBooking) (applied: int) =
    workflow {
        match! waitSignalFor<ReservationChanged> "changed" quietLimit with
        | None -> return applied
        | Some change when change.Version <= applied ->
            return! follow booking applied
        | Some change ->
            let! latest = applyNewest booking change
            if latest.Final then
                return latest.Version
            else
                return! follow booking latest.Version
    }

let definition booking =
    Workflow.define "reservation" (fun (_: string) -> follow booking 0)
// docs:end
