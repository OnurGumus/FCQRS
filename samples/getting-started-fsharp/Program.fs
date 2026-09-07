module App

open System
open System.Collections.Concurrent
open System.IO
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Common
open FCQRS.Model.Data
open FCQRS.FSharp
open Program

let run (args: string array) =
    async {
        let database =
            match Environment.GetEnvironmentVariable "DOCSTORE_DATABASE" with
            | null | "" -> Path.Combine(AppContext.BaseDirectory, "getting-started-fsharp.db")
            | path -> path
        let documentId = if args.Length > 1 then args[1] else Guid.NewGuid().ToString("N")
        let readModel = ConcurrentDictionary<string, Document>()
        let published = TaskCompletionSource<Event<DocumentEvent>>(TaskCreationOptions.RunContinuationsAsynchronously)
        let paused = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let pause = args.Length > 0 && args[0] = "--pause-publication"
        let publishing = pause || (args.Length > 0 && args[0] = "--publish")
        let handleProjection (_offset: int64) (message: obj) =
            match message with
            | :? Event<DocumentEvent> as event ->
                match event.EventDetails with
                | DocumentCreated document -> readModel[document.Id] <- document
                | DocumentEdited(id, content) ->
                    match readModel.TryGetValue id with
                    | true, document -> readModel[id] <- { document with Content = content }
                    | _ -> failwith "Projection received an edit before creation"
                | PublicationFinished(id, slug, _) when publishing && id = documentId && slug = args[2] ->
                    published.TrySetResult event |> ignore
                | _ -> ()
            | _ -> ()
        let config = ConfigurationBuilder().Build()
        use loggerFactory = LoggerFactory.Create(fun _ -> ())
        let api = Fcqrs.actor config loggerFactory
                      (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite $"Data Source={database};")) "getting-started-fsharp"
        try
            let documents =
                Fcqrs.aggregate api
                    { Name = "GettingStartedFSharpDocument"; Initial = initial; Decide = decide; Fold = fold
                      Snapshots = Default; Passivation = PassivationPolicy.Default }
            // docs:wire
            let slugs =
                Fcqrs.aggregate api
                    { Name = "GettingStartedFSharpSlug"; Initial = Publication.Slug.initial
                      Decide = Publication.Slug.decide; Fold = Publication.Slug.fold
                      Snapshots = Default; Passivation = PassivationPolicy.Default }
            let publication = Fcqrs.saga api (Publication.definition documents.Factory slugs.Factory pause paused)
            Fcqrs.wireSagaStarters api [ publication ]
            let subscriptions = Fcqrs.projection api (Projection.single 0 handleProjection)
            // docs:end
            let aggregateId = Fcqrs.aggregateId documentId
            let send command = documents.Send (Fcqrs.newCid ()) aggregateId command (fun _ -> true)
            let document = { Id = documentId; Title = "FCQRS notes"; Content = "first event" }
            match args with
            | [||] ->
                let correlationId = Fcqrs.newCid ()
                use projected = subscriptions.Subscribe(correlationId, 1)
                let! stored = documents.Send correlationId aggregateId (CreateDocument document) (fun _ -> true)
                do! projected.Task.WaitAsync(TimeSpan.FromSeconds 30.) |> Async.AwaitTask
                printfn "stored version %A; query returned '%s'" stored.Version readModel[documentId].Content
                let! repeated = send (CreateDocument { document with Content = "replacement attempt" })
                match repeated.EventDetails with
                | DocumentCreated original -> printfn "repeat reply version %A; document contains '%s'" repeated.Version original.Content
                | other -> failwithf "Unexpected create reply: %A" other
            | [| "--recover"; _ |] ->
                let! reply = send (CreateDocument { document with Content = "replacement attempt" })
                match reply.EventDetails with
                | DocumentCreated original -> printfn "recovery reply version %A; document contains '%s'" reply.Version original.Content
                | other -> failwithf "Unexpected create reply: %A" other
            | [| "--edit"; _; content |] ->
                // docs:edit-request
                let correlationId = Fcqrs.newCid ()
                use projected = subscriptions.Subscribe(correlationId, 1)
                let! reply = documents.Send correlationId aggregateId (EditDocument(documentId, content)) (fun _ -> true)
                match reply.EventDetails with
                | DocumentEdited(_, content) ->
                    if reply.Journaled = Some true then
                        do! projected.Task.WaitAsync(TimeSpan.FromSeconds 30.) |> Async.AwaitTask
                        printfn "edited version %A; query returned '%s'" reply.Version readModel[documentId].Content
                    else printfn "edit reply version %A; document contains '%s'" reply.Version content
                | DocumentRejected reason -> printfn "edit rejected: %s" reason
                | other -> failwithf "Unexpected edit reply: %A" other
                // docs:end
            | [| ("--publish" | "--pause-publication"); _; slug |] ->
                let! reply = send (PublishDocument slug)
                match reply.EventDetails with
                | DocumentRejected reason -> printfn "publication rejected: %s" reason
                | PublicationRequested _ | PublicationFinished _ ->
                    if pause then
                        do! paused.Task.WaitAsync(TimeSpan.FromSeconds 30.) |> Async.AwaitTask
                        printfn "publication paused after the reservation; restart with --publish %s %s" documentId slug
                    else
                        // The observer was installed before runtime startup. It also sees replay,
                        // including a recovered saga finishing under its original correlation id.
                        let! finished = published.Task.WaitAsync(TimeSpan.FromSeconds 30.) |> Async.AwaitTask
                        match finished.EventDetails with
                        | PublicationFinished(_, _, result) ->
                            printfn "publication version %A; %s -> %A" finished.Version slug result
                            printfn "query returned '%s'" readModel[documentId].Content
                        | _ -> failwith "Expected publication completion"
                | other -> failwithf "Unexpected publication reply: %A" other
            | _ -> invalidArg "args" "Unknown exercise"
            printfn "document id: %s" documentId
            printfn "journal: %s" database
        finally
            api.Stop().GetAwaiter().GetResult()
    }

[<EntryPoint>]
let main args =
    match args with
    | [| "--check" |] -> Checks.run (); 0
    | [||] | [| "--recover"; _ |] | [| ("--edit" | "--publish" | "--pause-publication"); _; _ |] ->
        run args |> Async.RunSynchronously; 0
    | _ ->
        eprintfn "Usage: dotnet run [-- --check | --recover ID | --edit ID CONTENT | --publish ID SLUG | --pause-publication ID SLUG]"
        1
