module Transfer

open System
open FCQRS.Common
open FCQRS.FSharp
open Account

// docs:states
// What a transfer moves, and between which accounts.
type TransferDetails =
    { Id: string
      Source: string
      Target: string
      Amount: decimal }

// Where a transfer is.
type TransferState =
    // Waiting for the target account to take the money.
    | Delivering of TransferDetails
    // The target turned it down: waiting for the source account's refund.
    | Refunding of TransferDetails
    | Completed
// docs:end

// docs:react
// Turns an event into the next state to store.
let handleEvent (message: obj) (saga: SagaState<unit, TransferState option>) =
    match message, saga.State with
    | (:? Event<AccountEvent> as event), state ->
        // Sender is the ID of the account that stored the event.
        let sender = string event.Sender.Value
        match event.EventDetails, state with
        | TransferSent(id, target, amount), None ->
            let transfer =
                { Id = id; Source = sender; Target = target; Amount = amount }
            StateChangedEvent(Delivering transfer)
        | TransferReceived(id, _, _), Some(Delivering transfer)
            when id = transfer.Id -> StateChangedEvent Completed
        | Rejected _, Some(Delivering transfer) when sender = transfer.Target ->
            StateChangedEvent(Refunding transfer)
        | TransferRefunded(id, _, _), Some(Refunding transfer)
            when id = transfer.Id -> StateChangedEvent Completed
        | _ -> UnhandledEvent
    // No answer in time. The outcome is unknown, so enter the same state again,
    // which sends the command again.
    | :? ExpectationExhausted, Some(Delivering _ | Refunding _ as waiting) ->
        StateChangedEvent waiting
    | _ -> UnhandledEvent
// docs:end

// docs:effects
// Returns the commands for a stored state. It runs again after recovery; the
// last argument says whether FCQRS is recovering, and this saga ignores it.
let applySideEffects accounts (saga: SagaState<unit, TransferState>) _ =
    // Send now and every 5 seconds; after 30 seconds, tell handleEvent.
    let deliver command =
        let retry = FixedInterval(TimeSpan.FromSeconds 5.)
        expecting (TimeSpan.FromSeconds 30.) retry [ command ], []
    match saga.State with
    | Delivering transfer ->
        let command =
            ReceiveTransfer(transfer.Id, transfer.Source, transfer.Amount)
        deliver (toAggregate accounts transfer.Target command)
    | Refunding transfer ->
        let command =
            RefundTransfer(transfer.Id, transfer.Target, transfer.Amount)
        deliver (toOriginator accounts command)
    | Completed -> StopSaga, []
// docs:end

// docs:definition
// A transfer starts when an account stores TransferSent.
let startsOn (event: Event<AccountEvent>) =
    match event.EventDetails with
    | TransferSent _ -> true
    | _ -> false

let definition accounts =
    { Name = "Transfer"
      InitialData = ()
      Originator = accounts
      HandleEvent = handleEvent
      ApplySideEffects = applySideEffects accounts
      StartOn = startsOn
      Snapshots = Default }
// docs:end
