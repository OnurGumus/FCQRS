using System.Text.Json.Serialization;
using static FCQRS.Common;
using static FCQRS.CSharp;

public sealed record Document(string Id, string Title, string Content);
public enum PublicationResult { Published, Rejected }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$case")]
[JsonDerivedType(typeof(WaitingForSlug), "waiting")]
[JsonDerivedType(typeof(FinishedPublication), "finished")]
public abstract record PublicationProgress;
public sealed record WaitingForSlug(string Slug) : PublicationProgress;
public sealed record FinishedPublication(string Slug, PublicationResult Result) : PublicationProgress;

// Missing Publication in an older snapshot reads as null.
public sealed record DocumentState(Document? Document = null, PublicationProgress? Publication = null)
{
    public static readonly DocumentState Initial = new();
}

// docs:messages
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$case")]
[JsonDerivedType(typeof(CreateDocument), "create")]
[JsonDerivedType(typeof(EditDocument), "edit")]
[JsonDerivedType(typeof(PublishDocument), "publish")]
[JsonDerivedType(typeof(FinishPublication), "finish-publication")]
public abstract record DocumentCommand;
public sealed record CreateDocument(Document Document) : DocumentCommand;
public sealed record EditDocument(string Id, string Content) : DocumentCommand;
public sealed record PublishDocument(string Slug) : DocumentCommand;
public sealed record FinishPublication(string Slug, PublicationResult Result) : DocumentCommand;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$case")]
[JsonDerivedType(typeof(DocumentCreated), "created")]
[JsonDerivedType(typeof(DocumentEdited), "edited")]
[JsonDerivedType(typeof(DocumentRejected), "rejected")]
[JsonDerivedType(typeof(PublicationRequested), "publication-requested")]
[JsonDerivedType(typeof(PublicationFinished), "publication-finished")]
public abstract record DocumentEvent;
// The existing name and fields stay readable by the legacy event reader.
public sealed record DocumentCreated(Document Document) : DocumentEvent;
public sealed record DocumentEdited(string Id, string Content) : DocumentEvent;
public sealed record DocumentRejected(string Reason) : DocumentEvent;
public sealed record PublicationRequested(string Id, string Slug) : DocumentEvent;
public sealed record PublicationFinished(string Id, string Slug, PublicationResult Result) : DocumentEvent;
// docs:end

public sealed class DocumentAggregate : Aggregate<DocumentState, DocumentCommand, DocumentEvent>
{
    public override DocumentState InitialState => DocumentState.Initial;
    public override string EntityName => "GettingStartedCSharpDocument";

    public override EventAction<DocumentEvent> HandleCommand(Command<DocumentCommand> command, DocumentState state) =>
        (command.CommandDetails, state.Document, state.Publication) switch
        {
            // docs:create
            (CreateDocument create, null, _) => EventActions.Persist<DocumentEvent>(new DocumentCreated(create.Document)),
            (CreateDocument, { } existing, _) => EventActions.Defer<DocumentEvent>(new DocumentCreated(existing)),
            // docs:end
            // docs:edit
            (EditDocument edit, { } document, null) when edit.Id == document.Id =>
                string.IsNullOrWhiteSpace(edit.Content)
                    ? EventActions.Defer<DocumentEvent>(new DocumentRejected("Content must not be blank"))
                    : edit.Content == document.Content
                        ? EventActions.Defer<DocumentEvent>(new DocumentEdited(edit.Id, edit.Content))
                        : EventActions.Persist<DocumentEvent>(new DocumentEdited(edit.Id, edit.Content)),
            (EditDocument, null, _) => EventActions.Defer<DocumentEvent>(new DocumentRejected("Document does not exist")),
            (EditDocument, _, _) => EventActions.Defer<DocumentEvent>(new DocumentRejected("Editing closes when publication starts")),
            // docs:end
            // docs:publish
            (PublishDocument publish, { } document, null) => string.IsNullOrWhiteSpace(publish.Slug)
                ? EventActions.Defer<DocumentEvent>(new DocumentRejected("Slug must not be blank"))
                : EventActions.Persist<DocumentEvent>(new PublicationRequested(document.Id, publish.Slug)),
            (PublishDocument publish, { } document, WaitingForSlug waiting) when publish.Slug == waiting.Slug =>
                EventActions.Defer<DocumentEvent>(new PublicationRequested(document.Id, publish.Slug)),
            (PublishDocument publish, { } document, FinishedPublication finished) when publish.Slug == finished.Slug =>
                EventActions.Defer<DocumentEvent>(new PublicationFinished(document.Id, publish.Slug, finished.Result)),
            (FinishPublication finish, { } document, WaitingForSlug waiting) when finish.Slug == waiting.Slug =>
                EventActions.Persist<DocumentEvent>(new PublicationFinished(document.Id, finish.Slug, finish.Result)),
            (FinishPublication finish, { } document, FinishedPublication finished)
                when finish.Slug == finished.Slug && finish.Result == finished.Result =>
                EventActions.Defer<DocumentEvent>(new PublicationFinished(document.Id, finish.Slug, finish.Result)),
            _ => EventActions.Defer<DocumentEvent>(new DocumentRejected("Publication does not match the document's state"))
            // docs:end
        };

    public override DocumentState ApplyEvent(Event<DocumentEvent> stored, DocumentState state) =>
        stored.EventDetails switch
        {
            DocumentCreated created => state with { Document = created.Document },
            DocumentEdited edited when state.Document is { } document && document.Id == edited.Id =>
                state with { Document = document with { Content = edited.Content } },
            DocumentEdited => throw new InvalidOperationException("DocumentEdited requires an earlier DocumentCreated"),
            DocumentRejected => state,
            PublicationRequested requested => state with { Publication = new WaitingForSlug(requested.Slug) },
            PublicationFinished finished => state with { Publication = new FinishedPublication(finished.Slug, finished.Result) },
            _ => throw new InvalidOperationException("Unknown document event")
        };
}
