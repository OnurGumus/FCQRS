using System.Text.Json.Serialization;
using Microsoft.FSharp.Core;
using static FCQRS.Common;
using static FCQRS.CSharp;

public sealed record SlugState(string? ReservedFor = null);
public sealed record ReserveSlug(string DocumentId);
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$case")]
[JsonDerivedType(typeof(SlugReserved), "reserved")]
[JsonDerivedType(typeof(SlugUnavailable), "unavailable")]
public abstract record SlugEvent;
public sealed record SlugReserved(string DocumentId) : SlugEvent;
public sealed record SlugUnavailable(string DocumentId) : SlugEvent;

public sealed class SlugAggregate : Aggregate<SlugState, ReserveSlug, SlugEvent>
{
    public override SlugState InitialState => new();
    public override string EntityName => "GettingStartedCSharpSlug";
    // docs:reserve
    public override EventAction<SlugEvent> HandleCommand(Command<ReserveSlug> command, SlugState state) =>
        state.ReservedFor switch
        {
            null => EventActions.Persist<SlugEvent>(new SlugReserved(command.CommandDetails.DocumentId)),
            var owner when owner == command.CommandDetails.DocumentId =>
                EventActions.Defer<SlugEvent>(new SlugReserved(owner)),
            _ => EventActions.Defer<SlugEvent>(new SlugUnavailable(command.CommandDetails.DocumentId))
        };

    public override SlugState ApplyEvent(Event<SlugEvent> stored, SlugState state) =>
        stored.EventDetails is SlugReserved reserved ? new(reserved.DocumentId) : state;
    // docs:end
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$case")]
[JsonDerivedType(typeof(ReservingSlug), "reserving")]
[JsonDerivedType(typeof(ReservationUncertain), "reservation-uncertain")]
[JsonDerivedType(typeof(ReportingResult), "reporting")]
[JsonDerivedType(typeof(ReportUncertain), "report-uncertain")]
[JsonDerivedType(typeof(PublicationDone), "done")]
public abstract record PublicationState;
public sealed record ReservingSlug(string Id, string Slug) : PublicationState;
public sealed record ReservationUncertain(string Id, string Slug) : PublicationState;
public sealed record ReportingResult(string Id, string Slug, PublicationResult Result) : PublicationState;
public sealed record ReportUncertain(string Id, string Slug, PublicationResult Result) : PublicationState;
public sealed record PublicationDone : PublicationState;
public sealed record PublicationData;

public sealed class PublicationSaga(AggregateFactory documents, AggregateFactory slugs,
    bool pauseAfterReservation, TaskCompletionSource paused)
    : Saga<DocumentEvent, PublicationData, PublicationState>
{
    public override PublicationData InitialData => new();
    public override string SagaName => "GettingStartedCSharpPublication";
    public override AggregateFactory Originator => documents;
    public static bool StartsOn(object message) => message is Event<DocumentEvent> { EventDetails: PublicationRequested };

    // docs:react
    public override EventAction<PublicationState> HandleEvent(object message,
        SagaState<PublicationData, FSharpOption<PublicationState>> saga) =>
        (message, saga.State?.Value) switch
        {
            (Event<DocumentEvent> { EventDetails: PublicationRequested requested }, null) =>
                StateChanged(new ReservingSlug(requested.Id, requested.Slug)),
            (Event<SlugEvent> reply, ReservingSlug state) => ReservationReply(reply, state.Id, state.Slug),
            (Event<SlugEvent> reply, ReservationUncertain state) => ReservationReply(reply, state.Id, state.Slug),
            (Event<DocumentEvent> reply, ReportingResult state) => CompletionReply(reply, state.Id, state.Slug, state.Result),
            (Event<DocumentEvent> reply, ReportUncertain state) => CompletionReply(reply, state.Id, state.Slug, state.Result),
            (ExpectationExhausted, ReservingSlug state) => StateChanged(new ReservationUncertain(state.Id, state.Slug)),
            (ExpectationExhausted, ReportingResult state) => StateChanged(new ReportUncertain(state.Id, state.Slug, state.Result)),
            _ => Unhandled()
        };

    static EventAction<PublicationState> ReservationReply(Event<SlugEvent> reply, string id, string slug) =>
        reply.EventDetails switch
        {
            SlugReserved reserved when reserved.DocumentId == id => StateChanged(new ReportingResult(id, slug, PublicationResult.Published)),
            SlugUnavailable unavailable when unavailable.DocumentId == id => StateChanged(new ReportingResult(id, slug, PublicationResult.Rejected)),
            _ => Unhandled()
        };

    static EventAction<PublicationState> CompletionReply(Event<DocumentEvent> reply, string id, string slug, PublicationResult result) =>
        reply.EventDetails is PublicationFinished finished && (finished.Id, finished.Slug, finished.Result) == (id, slug, result)
            ? StateChanged(new PublicationDone()) : Unhandled();
    // docs:end

    public override SagaSideEffectResult<PublicationState> ApplySideEffects(
        SagaState<PublicationData, PublicationState> saga, bool recovering)
    {
        // Only the recovery exercise pauses here, after progress is persisted.
        if (pauseAfterReservation && saga.State is ReportingResult)
        {
            paused.TrySetResult();
            return new() { Transition = Stay(), Commands = [] };
        }
        return Effects(saga.State);
    }

    // docs:effects
    public SagaSideEffectResult<PublicationState> Effects(PublicationState state) => state switch
    {
        ReservingSlug reserving => WaitFor(SagaCommands.ToAggregate(slugs, reserving.Slug, new ReserveSlug(reserving.Id))),
        ReportingResult reporting => WaitFor(SagaCommands.ToOriginator(documents, new FinishPublication(reporting.Slug, reporting.Result))),
        ReservationUncertain or ReportUncertain => new() { Transition = Stay(), Commands = [] },
        PublicationDone => new() { Transition = StopSaga(), Commands = [] },
        _ => throw new InvalidOperationException("Unknown publication state")
    };

    static SagaSideEffectResult<PublicationState> WaitFor(ExecuteCommand command) => new()
    {
        Transition = Stay(),
        Expect = Expectations.Create([command], TimeSpan.FromMinutes(5), RetrySchedules.Fixed(TimeSpan.FromSeconds(2)))
    };
    // docs:end
}
