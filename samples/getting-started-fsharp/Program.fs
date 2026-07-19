open System
open System.Collections.Concurrent
open System.IO
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Common
open FCQRS.Model.Data
open FCQRS.FSharp

type Document =
    { Id: string
      Title: string
      Content: string }

type DocumentState = { Document: Document option }
type DocumentCommand = CreateDocument of Document
type DocumentEvent = DocumentCreated of Document

let initial = { Document = None }

let decide (command: Command<DocumentCommand>) state =
    match command.CommandDetails, state.Document with
    | CreateDocument document, None -> DocumentCreated document |> PersistEvent
    | CreateDocument _, Some _ -> IgnoreEvent

let fold (event: Event<DocumentEvent>) _state =
    match event.EventDetails with
    | DocumentCreated document -> { Document = Some document }

let readModel = ConcurrentDictionary<string, Document>()

let handleProjection (_offset: int64) (message: obj) =
    match message with
    | :? Event<DocumentEvent> as event ->
        match event.EventDetails with
        | DocumentCreated document -> readModel[document.Id] <- document
    | _ -> ()

let run () =
    async {
        let config = ConfigurationBuilder().Build()
        let loggerFactory = LoggerFactory.Create(fun _ -> ())
        let database = Path.Combine(AppContext.BaseDirectory, "getting-started-fsharp.db")
        let connection = Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={database};"
        let api = Fcqrs.actor config loggerFactory (Some connection) "getting-started-fsharp"

        let documents =
            Fcqrs.aggregate api
                { Name = "GettingStartedFSharpDocument"
                  Initial = initial
                  Decide = decide
                  Fold = fold
                  Snapshots = Default }

        Fcqrs.wireSagaStarters api []
        let subscriptions = Fcqrs.projection api (Projection.single 0 handleProjection)

        let documentId = Guid.NewGuid().ToString("N")
        let document =
            { Id = documentId
              Title = "FCQRS notes"
              Content = "created from F#" }

        let correlationId = Fcqrs.newCid ()
        let aggregateId = Fcqrs.aggregateId documentId

        // Subscribe before sending so the projection cannot publish first.
        use projected = subscriptions.Subscribe(correlationId, 1)

        let! stored =
            documents.Send correlationId aggregateId (CreateDocument document) (fun _ -> true)

        do! projected.Task |> Async.AwaitTask

        let queried = readModel[documentId]
        printfn "stored version %A; query returned '%s'" stored.Version queried.Content
        printfn "journal: %s" database

        do! api.Stop() |> Async.AwaitTask
    }

[<EntryPoint>]
let main _ =
    run () |> Async.RunSynchronously
    0
