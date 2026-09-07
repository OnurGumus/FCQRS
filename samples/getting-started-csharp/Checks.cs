using System.Text.Json;
using Microsoft.FSharp.Core;
using static FCQRS.Common;
using static FCQRS.CSharp;
using static FCQRS.Serialization.Serialization;

public static class DocumentChecks
{
    static void Equal<T>(T expected, T actual, string message)
    {
        if (!Equals(expected, actual)) throw new Exception(message);
    }
    static void Roundtrip<T>(T value) => Equal(value,
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, jsonOptions), jsonOptions), "Serialized value did not round-trip");
    static Event<DocumentEvent> Event(DocumentEvent value, long version = 1) => TestEnvelope.Event(value, version);
    static Command<DocumentCommand> Command(DocumentCommand value) => TestEnvelope.Command(value);

    public static void Run()
    {
        DocumentRules(); SagaRules(); LegacyRules();
        Console.WriteLine("All document, replay, and saga checks passed.");
    }
    public static void DocumentRules()
    {
        var aggregate = new DocumentAggregate();
        // docs:checks
        var original = new Document("notes", "FCQRS notes", "first event");
        var replacement = original with { Content = "replacement attempt" };
        Equal(EventActions.Persist<DocumentEvent>(new DocumentCreated(original)),
            aggregate.HandleCommand(Command(new CreateDocument(original)), DocumentState.Initial), "First create must store");
        var created = aggregate.ApplyEvent(Event(new DocumentCreated(original)), DocumentState.Initial);
        Equal(EventActions.Defer<DocumentEvent>(new DocumentCreated(original)),
            aggregate.HandleCommand(Command(new CreateDocument(replacement)), created), "Repeated create must preserve the original");
        Equal(created, aggregate.ApplyEvent(Event(new DocumentCreated(original)), created), "Repeated reply must not change state");
        // docs:end
        // docs:edit-checks
        Equal(EventActions.Persist<DocumentEvent>(new DocumentEdited("notes", "second draft")),
            aggregate.HandleCommand(Command(new EditDocument("notes", "second draft")), created), "Edit must store new content");
        var edited = aggregate.ApplyEvent(Event(new DocumentEdited("notes", "second draft"), 2), created);
        Equal(EventActions.Defer<DocumentEvent>(new DocumentEdited("notes", "second draft")),
            aggregate.HandleCommand(Command(new EditDocument("notes", "second draft")), edited), "Repeating the latest edit must defer");
        Equal(edited, aggregate.ApplyEvent(Event(new DocumentEdited("notes", "second draft"), 2), edited), "Deferred edit must not change state");
        Equal(EventActions.Defer<DocumentEvent>(new DocumentRejected("Document does not exist")),
            aggregate.HandleCommand(Command(new EditDocument("missing", "draft")), DocumentState.Initial), "Missing document must reject");
        Equal(edited, aggregate.ApplyEvent(Event(new DocumentRejected("Content must not be blank"), 2), edited), "Deferred rejection must not change state");
        // docs:end
        var waiting = aggregate.ApplyEvent(Event(new PublicationRequested("notes", "guides/fcqrs"), 3), edited);
        var finished = aggregate.ApplyEvent(Event(new PublicationFinished("notes", "guides/fcqrs", PublicationResult.Published), 4), waiting);
        Equal(EventActions.Defer<DocumentEvent>(new PublicationFinished("notes", "guides/fcqrs", PublicationResult.Published)),
            aggregate.HandleCommand(Command(new FinishPublication("guides/fcqrs", PublicationResult.Published)), finished), "Repeated saga completion must defer");
        Equal(EventActions.Defer<DocumentEvent>(new DocumentRejected("Editing closes when publication starts")),
            aggregate.HandleCommand(Command(new EditDocument("notes", "too late")), finished), "Publication freezes editing");
        DocumentEvent[] history = [new DocumentCreated(original), new DocumentEdited("notes", "second draft"),
            new PublicationRequested("notes", "guides/fcqrs"), new PublicationFinished("notes", "guides/fcqrs", PublicationResult.Published)];
        var recovered = history.Select((e, i) => Event(e, i + 1)).Aggregate(DocumentState.Initial, (s, e) => aggregate.ApplyEvent(e, s));
        Equal(finished, recovered, "Mixed-history replay must rebuild the same state");
        foreach (var e in history) Roundtrip(Event(e));
        Roundtrip(Event(new DocumentRejected("reason"))); Roundtrip(finished);
        DocumentCommand[] commands = [new CreateDocument(original), new EditDocument("notes", "draft"),
            new PublishDocument("guides/fcqrs"), new FinishPublication("guides/fcqrs", PublicationResult.Published)];
        foreach (var command in commands) Roundtrip(Command(command));
    }
    static void SagaRules()
    {
        AggregateFactory factory = _ => throw new Exception("Pure tests never resolve actors");
        var saga = new PublicationSaga(factory, factory, false, new());
        SagaState<PublicationData, FSharpOption<PublicationState>> State(PublicationState value) => new(new(), FSharpOption<PublicationState>.Some(value));
        var expiry = new ExpectationExhausted("ReservingSlug", DateTime.UnixEpoch, 3);
        var reserving = new ReservingSlug("notes", "guides/fcqrs");
        var uncertain = new ReservationUncertain("notes", "guides/fcqrs");
        Equal(SagaEventActions.StateChanged<PublicationState>(uncertain), saga.HandleEvent(expiry, State(reserving)), "Exhaustion must remain unknown");
        Equal(SagaEventActions.StateChanged<PublicationState>(new ReportingResult("notes", "guides/fcqrs", PublicationResult.Published)),
            saga.HandleEvent(TestEnvelope.Event<SlugEvent>(new SlugReserved("notes"), 1), State(uncertain)), "Late success must be accepted");
        var reporting = new ReportingResult("notes", "guides/fcqrs", PublicationResult.Published);
        var reportUncertain = new ReportUncertain("notes", "guides/fcqrs", PublicationResult.Published);
        Equal(SagaEventActions.StateChanged<PublicationState>(reportUncertain), saga.HandleEvent(expiry, State(reporting)), "Missing completion reply must remain unknown");
        Equal(SagaEventActions.StateChanged<PublicationState>(new PublicationDone()),
            saga.HandleEvent(Event(new PublicationFinished("notes", "guides/fcqrs", PublicationResult.Published), 4), State(reportUncertain)), "Late completion must finish");
        foreach (PublicationState state in new PublicationState[] { reserving, reporting })
        {
            var effects = saga.ApplySideEffects(new(new(), state), true);
            if (effects.Expect is null || effects.Expect.Resend.Length != 1) throw new Exception("Recovery must re-drive one retry-safe command with a deadline");
        }
        foreach (PublicationState state in new PublicationState[] { reserving, uncertain, reporting, reportUncertain, new PublicationDone() }) Roundtrip(state);
        Roundtrip(TestEnvelope.Event<SlugEvent>(new SlugReserved("notes"), 1));
        Roundtrip(TestEnvelope.Event<SlugEvent>(new SlugUnavailable("other"), 1));
    }
    static void LegacyRules()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "document-created-v1-csharp.json")));
        var old = fixture.RootElement.GetProperty("payload").Deserialize<Event<DocumentCreated>>(jsonOptions)!;
        var adapted = (Event<DocumentEvent>)LegacyCreationReader.Read(old);
        Equal(old.Version, adapted.Version, "Legacy reader must preserve the version");
        Equal(old.CorrelationId, adapted.CorrelationId, "Legacy reader must preserve correlation");
        var aggregate = new DocumentAggregate();
        var recovered = aggregate.ApplyEvent(adapted, DocumentState.Initial);
        var edited = aggregate.ApplyEvent(Event(new DocumentEdited(recovered.Document!.Id, "upgraded"), 2), recovered);
        Equal("upgraded", edited.Document!.Content, "Old history must accept a new edit");
        var snapshot = "{\"Document\":" + JsonSerializer.Serialize(old.EventDetails.Document, jsonOptions) + "}";
        Equal(recovered, JsonSerializer.Deserialize<DocumentState>(snapshot, jsonOptions), "Old snapshot with no publication field must recover");
    }
}
