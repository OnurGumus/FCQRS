// Keep this module name: earlier sample journals name Program+DocumentEvent.
module Program

open FCQRS.Common

type Document = { Id: string; Title: string; Content: string }
type PublicationResult = Published | Rejected
type PublicationProgress = WaitingForSlug of string | Finished of string * PublicationResult
// An absent publication field in an older snapshot reads as None.
type DocumentState = { Document: Document option; Publication: PublicationProgress option }
type DocumentCommand =
    | CreateDocument of Document
    | EditDocument of id: string * content: string
    | PublishDocument of slug: string
    | FinishPublication of slug: string * PublicationResult
type DocumentEvent =
    | DocumentCreated of Document
    | DocumentEdited of id: string * content: string
    | DocumentRejected of reason: string
    | PublicationRequested of id: string * slug: string
    | PublicationFinished of id: string * slug: string * PublicationResult

let initial = { Document = None; Publication = None }

let decide (command: Command<DocumentCommand>) state =
    match command.CommandDetails, state.Document, state.Publication with
    // docs:create
    | CreateDocument document, None, _ -> DocumentCreated document |> PersistEvent
    | CreateDocument _, Some existing, _ -> DocumentCreated existing |> DeferEvent
    // docs:end
    // docs:edit
    | EditDocument(id, content), Some document, None when document.Id = id ->
        if System.String.IsNullOrWhiteSpace content then
            DocumentRejected "Content must not be blank" |> DeferEvent
        elif document.Content = content then
            DocumentEdited(id, content) |> DeferEvent
        else
            DocumentEdited(id, content) |> PersistEvent
    | EditDocument _, None, _ -> DocumentRejected "Document does not exist" |> DeferEvent
    | EditDocument _, _, _ -> DocumentRejected "Editing closes when publication starts" |> DeferEvent
    // docs:end
    // docs:publish
    | PublishDocument slug, Some document, None ->
        if System.String.IsNullOrWhiteSpace slug then
            DocumentRejected "Slug must not be blank" |> DeferEvent
        else PublicationRequested(document.Id, slug) |> PersistEvent
    | PublishDocument slug, Some document, Some(WaitingForSlug current) when slug = current ->
        PublicationRequested(document.Id, slug) |> DeferEvent
    | PublishDocument slug, Some document, Some(Finished(current, result)) when slug = current ->
        PublicationFinished(document.Id, slug, result) |> DeferEvent
    | FinishPublication(slug, result), Some document, Some(WaitingForSlug current) when slug = current ->
        PublicationFinished(document.Id, slug, result) |> PersistEvent
    | FinishPublication(slug, result), Some document, Some(Finished(current, previous))
        when slug = current && result = previous ->
        PublicationFinished(document.Id, slug, result) |> DeferEvent
    | _ -> DocumentRejected "Publication does not match the document's state" |> DeferEvent
    // docs:end

let fold (event: Event<DocumentEvent>) state =
    match event.EventDetails with
    | DocumentCreated document -> { state with Document = Some document }
    | DocumentEdited(id, content) ->
        match state.Document with
        | Some document when document.Id = id ->
            { state with Document = Some { document with Content = content } }
        | _ -> failwith "DocumentEdited requires an earlier DocumentCreated"
    | DocumentRejected _ -> state
    | PublicationRequested(_, slug) -> { state with Publication = Some(WaitingForSlug slug) }
    | PublicationFinished(_, slug, result) -> { state with Publication = Some(Finished(slug, result)) }
