module Publication

open System
open FCQRS.Common
open FCQRS.FSharp
open Program

module Slug =
    type State = { ReservedFor: string option }
    type Command = ReserveSlug of documentId: string
    type Event = SlugReserved of documentId: string | SlugUnavailable of documentId: string
    let initial = { ReservedFor = None }

    // docs:reserve
    let decide (command: FCQRS.Common.Command<Command>) state =
        match command.CommandDetails, state.ReservedFor with
        | ReserveSlug id, None -> SlugReserved id |> PersistEvent
        | ReserveSlug id, Some owner when id = owner -> SlugReserved id |> DeferEvent
        | ReserveSlug id, Some _ -> SlugUnavailable id |> DeferEvent

    let fold (event: FCQRS.Common.Event<Event>) state =
        match event.EventDetails with
        | SlugReserved id -> { ReservedFor = Some id }
        | SlugUnavailable _ -> state
    // docs:end

// Each state carries what recovery needs for the next command.
type State =
    | ReservingSlug of documentId: string * slug: string
    | ReservationUncertain of documentId: string * slug: string
    | ReportingResult of documentId: string * slug: string * PublicationResult
    | ReportUncertain of documentId: string * slug: string * PublicationResult
    | Done

// docs:react
let handleEvent (message: obj) (saga: SagaState<unit, State option>) =
    match message, saga.State with
    | (:? Event<DocumentEvent> as event), None ->
        match event.EventDetails with
        | PublicationRequested(id, slug) -> StateChangedEvent(ReservingSlug(id, slug))
        | _ -> UnhandledEvent
    | (:? Event<Slug.Event> as event), Some(ReservingSlug(id, slug) | ReservationUncertain(id, slug)) ->
        match event.EventDetails with
        | Slug.SlugReserved owner when owner = id -> StateChangedEvent(ReportingResult(id, slug, Published))
        | Slug.SlugUnavailable rejected when rejected = id -> StateChangedEvent(ReportingResult(id, slug, Rejected))
        | _ -> UnhandledEvent
    | (:? Event<DocumentEvent> as event), Some(ReportingResult(id, slug, result) | ReportUncertain(id, slug, result)) ->
        match event.EventDetails with
        | PublicationFinished(foundId, foundSlug, foundResult)
            when (foundId, foundSlug, foundResult) = (id, slug, result) -> StateChangedEvent Done
        | _ -> UnhandledEvent
    | :? ExpectationExhausted, Some(ReservingSlug(id, slug)) ->
        StateChangedEvent(ReservationUncertain(id, slug))
    | :? ExpectationExhausted, Some(ReportingResult(id, slug, result)) ->
        StateChangedEvent(ReportUncertain(id, slug, result))
    | _ -> UnhandledEvent
// docs:end

// docs:effects
let applySideEffects documentFactory slugFactory (saga: SagaState<unit, State>) _recovering =
    let waitFor command =
        expecting (TimeSpan.FromMinutes 5.) (FixedInterval(TimeSpan.FromSeconds 2.)) [ command ], []
    match saga.State with
    | ReservingSlug(id, slug) -> waitFor (toAggregate slugFactory slug (Slug.ReserveSlug id))
    | ReportingResult(_, slug, result) -> waitFor (toOriginator documentFactory (FinishPublication(slug, result)))
    | ReservationUncertain _ | ReportUncertain _ -> Stay, []
    | Done -> StopSaga, []
// docs:end

let startsOn (event: Event<DocumentEvent>) =
    match event.EventDetails with PublicationRequested _ -> true | _ -> false

let definition documentFactory slugFactory pauseAfterReservation (paused: System.Threading.Tasks.TaskCompletionSource<unit>) =
    { Name = "GettingStartedFSharpPublication"
      InitialData = ()
      Originator = documentFactory
      HandleEvent = handleEvent
      ApplySideEffects = fun state recovering ->
          // Only the recovery exercise pauses here, after progress is persisted.
          match pauseAfterReservation, state.State with
          | true, ReportingResult _ -> paused.TrySetResult() |> ignore; Stay, []
          | _ -> applySideEffects documentFactory slugFactory state recovering
      StartOn = startsOn
      Snapshots = Default }
