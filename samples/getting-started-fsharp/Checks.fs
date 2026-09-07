module Checks

open System
open System.IO
open System.Text.Json
open FCQRS.Common
open FCQRS.CSharp
open FCQRS.Serialization.Serialization
open Program

let equal expected actual message = if expected <> actual then failwith message
let command details = TestEnvelope.Command details
let event version details = TestEnvelope.Event(details, version)
let roundtrip<'T when 'T: equality> (value: 'T) =
    equal value (value |> encodeToBytes |> decodeFromBytes<'T>) "Serialized value did not round-trip"

let documentChecks () =
    // docs:checks
    let original = { Id = "notes"; Title = "FCQRS notes"; Content = "first event" }
    let replacement = { original with Content = "replacement attempt" }
    equal (PersistEvent(DocumentCreated original))
        (decide (command (CreateDocument original)) initial) "First create must store"
    let created = fold (event 1L (DocumentCreated original)) initial
    equal (DeferEvent(DocumentCreated original))
        (decide (command (CreateDocument replacement)) created) "Repeated create must preserve the original"
    equal created (fold (event 1L (DocumentCreated original)) created) "Repeated reply must not change state"
    // docs:end
    // docs:edit-checks
    equal (PersistEvent(DocumentEdited("notes", "second draft")))
        (decide (command (EditDocument("notes", "second draft"))) created) "Edit must store new content"
    let edited = fold (event 2L (DocumentEdited("notes", "second draft"))) created
    equal (DeferEvent(DocumentEdited("notes", "second draft")))
        (decide (command (EditDocument("notes", "second draft"))) edited) "Repeating the latest edit must defer"
    equal edited (fold (event 2L (DocumentEdited("notes", "second draft"))) edited) "Deferred edit must not change state"
    equal (DeferEvent(DocumentRejected "Document does not exist"))
        (decide (command (EditDocument("missing", "draft"))) initial) "Missing document must reject"
    equal edited (fold (event 2L (DocumentRejected "Content must not be blank")) edited) "Deferred rejection must not change state"
    // docs:end
    let waiting = fold (event 3L (PublicationRequested("notes", "guides/fcqrs"))) edited
    let finished = fold (event 4L (PublicationFinished("notes", "guides/fcqrs", Published))) waiting
    equal (DeferEvent(PublicationFinished("notes", "guides/fcqrs", Published)))
        (decide (command (FinishPublication("guides/fcqrs", Published))) finished) "Repeated saga completion must defer"
    equal (DeferEvent(DocumentRejected "Editing closes when publication starts"))
        (decide (command (EditDocument("notes", "too late"))) finished) "Publication freezes editing"
    let history = [DocumentCreated original; DocumentEdited("notes", "second draft");
                   PublicationRequested("notes", "guides/fcqrs"); PublicationFinished("notes", "guides/fcqrs", Published)]
    let recovered = history |> List.mapi (fun i e -> event (int64 i + 1L) e) |> List.fold (fun s e -> fold e s) initial
    equal finished recovered "Mixed-history replay must rebuild the same state"
    history |> List.iter (fun e -> roundtrip (event 1L e))
    roundtrip (event 1L (DocumentRejected "reason"))
    roundtrip finished
    [CreateDocument original; EditDocument("notes","draft"); PublishDocument "guides/fcqrs";
     FinishPublication("guides/fcqrs",Published)] |> List.iter (command >> roundtrip)

let sagaChecks () =
    let state value : SagaState<unit, Publication.State option> = { Data = (); State = Some value }
    let expiry = { StateName = "ReservingSlug"; EnteredAt = DateTime.UnixEpoch; Attempts = 3 }
    let reserving = Publication.ReservingSlug("notes", "guides/fcqrs")
    equal (StateChangedEvent(Publication.ReservationUncertain("notes", "guides/fcqrs")))
        (Publication.handleEvent expiry (state reserving)) "Exhaustion must remain unknown"
    let uncertain = Publication.ReservationUncertain("notes", "guides/fcqrs")
    equal (StateChangedEvent(Publication.ReportingResult("notes", "guides/fcqrs", Published)))
        (Publication.handleEvent (event 1L (Publication.Slug.SlugReserved "notes")) (state uncertain)) "Late success must be accepted"
    let reporting = Publication.ReportingResult("notes", "guides/fcqrs", Published)
    equal (StateChangedEvent(Publication.ReportUncertain("notes", "guides/fcqrs", Published)))
        (Publication.handleEvent expiry (state reporting)) "Missing completion reply must remain unknown"
    equal (StateChangedEvent Publication.Done)
        (Publication.handleEvent (event 4L (PublicationFinished("notes", "guides/fcqrs", Published)))
            (state (Publication.ReportUncertain("notes", "guides/fcqrs", Published)))) "Late completion must finish"
    let factory (_: string) : Akkling.Cluster.Sharding.IEntityRef<obj> = failwith "Pure tests never resolve actors"
    for current in [reserving; reporting] do
        let snapshot : SagaState<unit, Publication.State> = { Data = (); State = current }
        let (transition, _) = Publication.applySideEffects factory factory snapshot true
        match transition with
        | StayExpecting expectation -> equal 1 expectation.Resend.Length "Recovery must re-drive one retry-safe command"
        | _ -> failwith "Every active wait must have an expectation"
    [reserving; uncertain; reporting; Publication.ReportUncertain("notes","guides/fcqrs",Published); Publication.Done]
    |> List.iter roundtrip
    [Publication.Slug.SlugReserved "notes"; Publication.Slug.SlugUnavailable "other"] |> List.iter (event 1L >> roundtrip)

let legacyChecks () =
    use fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "document-created-v1-fsharp.json")))
    let root = fixture.RootElement
    let old : Event<DocumentEvent> = decode (root.GetProperty("payload"))
    let recovered = fold old initial
    match recovered.Document with
    | None -> failwith "Old creation did not recover"
    | Some document ->
        let edited = fold (event 2L (DocumentEdited(document.Id, "upgraded"))) recovered
        equal "upgraded" edited.Document.Value.Content "Old history must accept a new edit"
        let snapshot = "{\"Document\":" + encode (Some document) + "}"
        let oldState = decodeFromBytes<DocumentState> (Text.Encoding.UTF8.GetBytes snapshot)
        equal recovered oldState "Old snapshot with no publication field must recover"

let run () =
    documentChecks ()
    sagaChecks ()
    legacyChecks ()
    printfn "All document, replay, and saga checks passed."
