open System
open FCQRS.Model.Data
open FCQRS.Common
open FCQRS.FSharp
// Payloads for the publication workflow shown on this page.
type DocumentId = string
module Document =
    type PublicationResult = Published | Rejected
    type Command = Publish of DocumentId * string | FinishPublication of PublicationResult
    type Event =
        | PublicationRequested of DocumentId * string
        | PublicationFinished of DocumentId * string * PublicationResult
    type State = { Id: DocumentId; Slug: string; Result: PublicationResult option }
    let initial = { Id = ""; Slug = ""; Result = None }
    let decide (command: Command<Command>) state =
        match command.CommandDetails with
        | Publish(id, slug) -> PublicationRequested(id, slug) |> PersistEvent
        | FinishPublication result ->
            persistIf state.Result.IsNone (PublicationFinished(state.Id, state.Slug, defaultArg state.Result result))
    let fold (event: Event<Event>) state =
        match event.EventDetails with
        | PublicationRequested(id, slug) -> { state with Id = id; Slug = slug }
        | PublicationFinished(_, _, result) -> { state with Result = Some result }
module Slug =
    type Command = Reserve of DocumentId
    type Event = SlugReserved of DocumentId | SlugUnavailable of DocumentId
    let initial: DocumentId option = None
    let decide (command: Command<Command>) state =
        let (Reserve id) = command.CommandDetails
        match state with
        | None -> SlugReserved id |> PersistEvent
        | Some owner when owner = id -> SlugReserved id |> DeferEvent
        | Some _ -> SlugUnavailable id |> DeferEvent
    let fold (event: Event<Event>) state =
        match event.EventDetails with
        | SlugReserved id -> Some id
        | SlugUnavailable _ -> state
// snippet: 1
// snippet: 2
module DeadlineExample =
    type State = ReservingSlug of DocumentId * string | PublicationFailed
    let applySideEffects documentFactory slugFactory (sagaState: SagaState<unit, State>) =
        match sagaState.State with
        // snippet: 3
    let handleEvent (message: obj) (sagaState: SagaState<unit, State option>) =
        match message, sagaState.State with
        // snippet: 4
        | _ -> UnhandledEvent
module CommitExample =
    type State = Committing of Set<DocumentId>
    let handleEvent (message: obj) (sagaState: SagaState<unit, State option>) =
        match message, sagaState.State with
        // snippet: 5
        | _ -> UnhandledEvent
// snippet: 6
let register (api: IActor) =
    // snippet: 7
