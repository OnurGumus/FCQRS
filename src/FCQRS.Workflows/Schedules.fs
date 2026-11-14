namespace FCQRS.Workflows

open System

/// When a schedule is due, written `ScheduleRule.Daily`, `ScheduleRule.Weekly` or `ScheduleRule.Every`.
/// A rule is stored in the schedule's journal, so its cases and fields are a persistent contract.
[<RequireQualifiedAccess>]
type ScheduleRule =
    /// Every day at a local time in a time zone. The zone is a time-zone ID such as "Europe/Oslo".
    | Daily of time: TimeOnly * zone: string
    /// On the listed days of the week at a local time in a time zone.
    | Weekly of days: DayOfWeek list * time: TimeOnly * zone: string
    /// Every interval, counted from the schedule's creation or last change.
    | Every of interval: TimeSpan

/// What a schedule does about due times that passed while nothing could start a run. The policy is
/// stored in the schedule's journal, so its cases are a persistent contract.
[<RequireQualifiedAccess>]
type CatchUp =
    /// Start one run for the most recent missed due time.
    | Latest
    /// Start one run for every missed due time, oldest first. At most the 1000 most recent are
    /// started; older ones are dropped.
    | All
    /// Start no run for a missed due time and wait for the next one in the future.
    | None

/// Due-time calculation for a `ScheduleRule`. Every `DateTime` it takes or returns is UTC.
module ScheduleRule =

    let private zoneOf (id: string) = TimeZoneInfo.FindSystemTimeZoneById id

    // The UTC instant of a local date and time. A time that does not exist (spring forward) moves to
    // the next local time that does. A time that occurs twice (fall back) is the first occurrence.
    let private toUtc (zone: TimeZoneInfo) (date: DateOnly) (time: TimeOnly) =
        let mutable local = DateTime(date, time, DateTimeKind.Unspecified)
        while zone.IsInvalidTime local do
            local <- local.AddMinutes 1.0
        let offset =
            if zone.IsAmbiguousTime local then
                zone.GetAmbiguousTimeOffsets local |> Array.max
            else
                zone.GetUtcOffset local
        DateTime.SpecifyKind(local - offset, DateTimeKind.Utc)

    // The first due time strictly after `after` on a day the predicate accepts.
    let private nextLocal zone (time: TimeOnly) (accepts: DayOfWeek -> bool) (after: DateTime) =
        let startDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(after, zone)).AddDays -1
        // A weekly rule with one day finds it within a week; a year bounds a zone that never matches.
        Seq.init 400 startDay.AddDays
        |> Seq.filter (fun day -> accepts day.DayOfWeek)
        |> Seq.map (fun day -> toUtc zone day time)
        |> Seq.tryFind (fun due -> due > after)
        |> Option.defaultWith (fun () -> failwith "No due time within a year of the given time.")

    /// The first due time strictly after `after`. `anchor` is the schedule's creation or last change
    /// time, which `Every` counts from. All times are UTC.
    let next (rule: ScheduleRule) (anchor: DateTime) (after: DateTime) : DateTime =
        match rule with
        | ScheduleRule.Daily(time, zone) -> nextLocal (zoneOf zone) time (fun _ -> true) after
        | ScheduleRule.Weekly(days, time, zone) -> nextLocal (zoneOf zone) time (fun day -> List.contains day days) after
        | ScheduleRule.Every interval ->
            let elapsed = after - anchor
            let intervals = if elapsed < interval then 1L else elapsed.Ticks / interval.Ticks + 1L
            anchor + TimeSpan.FromTicks(interval.Ticks * intervals)

    /// The most due times `missed` returns under `CatchUp.All`.
    [<Literal>]
    let MaxMissed = 1000

    // The indexes of the due times of an `Every` rule inside (lastDue, now], first and last. The
    // range is empty when the first exceeds the last. Ticks keep the arithmetic exact.
    let private everyRange (interval: TimeSpan) (anchor: DateTime) (lastDue: DateTime) (now: DateTime) =
        let first =
            let elapsed = lastDue - anchor
            if elapsed < interval then 1L else elapsed.Ticks / interval.Ticks + 1L
        let last = if now < anchor then 0L else (now - anchor).Ticks / interval.Ticks
        first, last

    /// The due times in `(lastDue, now]` that the policy runs, oldest first. All times are UTC.
    /// `CatchUp.All` returns at most the `MaxMissed` (1000) most recent of them. `CatchUp.Latest`
    /// returns at most one.
    let missed (rule: ScheduleRule) (anchor: DateTime) (lastDue: DateTime) (now: DateTime) (policy: CatchUp) : DateTime list =
        let keep = match policy with CatchUp.All -> MaxMissed | _ -> 1
        match policy, rule with
        | CatchUp.None, _ -> []
        | _, ScheduleRule.Every interval ->
            let first, last = everyRange interval anchor lastDue now
            let start = max first (last - int64 keep + 1L)
            [ for k in start..last -> anchor + TimeSpan.FromTicks(interval.Ticks * k) ]
        | _ ->
            // A window of the most recent due times: the whole list is never held.
            let window = System.Collections.Generic.Queue<DateTime>()
            let mutable due = next rule anchor lastDue
            while due <= now do
                window.Enqueue due
                if window.Count > keep then window.Dequeue() |> ignore
                due <- next rule anchor due
            List.ofSeq window

    /// Checks that a rule can produce due times: a known time zone, at least one day, and an interval of at
    /// least one second.
    let validate (rule: ScheduleRule) : Result<unit, string> =
        let checkZone (zone: string) =
            try
                zoneOf zone |> ignore
                Ok()
            with
            | :? TimeZoneNotFoundException
            | :? InvalidTimeZoneException -> Error $"Unknown time zone '{zone}'."
        match rule with
        | ScheduleRule.Daily(_, zone) -> checkZone zone
        | ScheduleRule.Weekly([], _, _) -> Error "A weekly schedule needs at least one day."
        | ScheduleRule.Weekly(_, _, zone) -> checkZone zone
        | ScheduleRule.Every interval when interval < TimeSpan.FromSeconds 1.0 -> Error "The interval must be at least 1 second."
        | ScheduleRule.Every _ -> Ok()

/// What a schedule starts and when. Stored in the schedule's journal, so its fields are a
/// persistent contract.
type ScheduleSpec =
    { /// The name of the registered workflow each run starts.
      Workflow: string
      /// The input every run gets, stored like a workflow input.
      Input: Payload
      /// When the schedule is due.
      Rule: ScheduleRule
      /// What the schedule does about due times that passed while nothing could start a run.
      CatchUp: CatchUp }

/// What the application, or the schedule dispatcher, can ask a schedule to do. Written
/// `ScheduleCommand.Create` and so on.
[<RequireQualifiedAccess>]
type ScheduleCommand =
    /// Stores a new schedule.
    | Create of ScheduleSpec
    /// Replaces the schedule's workflow, input, rule and catch-up policy.
    | Change of ScheduleSpec
    /// Stops the schedule from starting runs.
    | Pause
    /// Lets a paused schedule start runs again, from the first due time after the resume.
    | Resume
    /// Ends the schedule. Its ID cannot be used for a new schedule.
    | Delete
    /// Asks to start the run for a due time. The schedule decides whether that due time runs.
    | Fire of due: DateTime
    /// Reports that the run for an accepted due time has been started.
    | Started of due: DateTime

/// What a schedule replies, and stores. Written `ScheduleEvent.Created` and so on. Every case
/// except `Refused` is stored in the schedule's journal, so the cases and their fields are a
/// persistent contract.
[<RequireQualifiedAccess>]
type ScheduleEvent =
    /// The schedule was created at `at`, which an `Every` rule counts from.
    | Created of spec: ScheduleSpec * at: DateTime
    /// The schedule's spec was replaced at `at`, which an `Every` rule counts from. No due time at
    /// or before `at` runs.
    | Changed of spec: ScheduleSpec * at: DateTime
    /// The schedule stopped starting runs.
    | Paused of at: DateTime
    /// The schedule starts runs again. No due time at or before `at` runs.
    | Resumed of at: DateTime
    /// The schedule ended.
    | Deleted of at: DateTime
    /// A run for `due` was decided, with the input it gets. A later change does not alter it.
    | Firing of due: DateTime * input: Payload
    /// The run for `due` has been started.
    | Fired of due: DateTime
    /// A command was refused. Never stored.
    | Refused of reason: string

/// The last due time a schedule accepted.
type AcceptedDue =
    { /// The due time, in UTC.
      Due: DateTime
      /// The input its run gets.
      Input: Payload
      /// Whether the run's start is known to be stored.
      Started: bool }

/// What a schedule knows. All times are UTC.
type ScheduleState =
    { /// The spec the schedule was created with, and when. A repeated `Create` is compared with it.
      Created: (ScheduleSpec * DateTime) option
      /// The current spec.
      Spec: ScheduleSpec option
      /// The time of the creation or the last change. An `Every` rule counts from it.
      Anchor: DateTime
      /// When the schedule was paused, while it is paused.
      PausedAt: DateTime option
      /// When the schedule was last resumed.
      ResumedAt: DateTime option
      /// When the schedule was deleted.
      DeletedAt: DateTime option
      /// The last accepted due time.
      Last: AcceptedDue option }

/// The schedule aggregate's rules. One aggregate instance holds one schedule.
module Schedule =

    open FCQRS.Common

    /// The aggregate's entity name. A persistent contract.
    [<Literal>]
    let EntityName = "fcqrs-schedule"

    /// The state before the schedule's first event.
    let initial: ScheduleState =
        { Created = None
          Spec = None
          Anchor = DateTime.MinValue
          PausedAt = None
          ResumedAt = None
          DeletedAt = None
          Last = None }

    /// How late a `Fire` may arrive for a due time and still be on time under `CatchUp.None`. A due
    /// time older than this when its `Fire` arrives was missed, and `CatchUp.None` skips it.
    let OnTimeWindow = TimeSpan.FromMinutes 1.0

    // No due time at or before this is accepted: the last accepted due time, the creation or last
    // change, and the last resume. `ScheduleRule.next` and `missed` count only an `Every` rule from
    // the anchor, so this bound is what keeps a daily or weekly due time from before a change, or
    // inside a pause, from being accepted.
    let private lowerBound (state: ScheduleState) =
        let lastDue = state.Last |> Option.map _.Due |> Option.defaultValue DateTime.MinValue
        let resumed = state.ResumedAt |> Option.defaultValue DateTime.MinValue
        max state.Anchor (max lastDue resumed)

    /// The one due time a `Fire` arriving at `now` may name, or nothing for a schedule that does not
    /// exist, is deleted or is paused. It is the next due time after the last accepted one, the
    /// creation, the last change and the last resume. When that due time has already passed, the
    /// catch-up policy chooses among the passed ones: `Latest` the most recent, `All` the oldest of
    /// the most recent `ScheduleRule.MaxMissed`, and `None` the most recent if it passed within
    /// `OnTimeWindow`, otherwise the next due time after `now`. A pending `Firing` is not checked here.
    let allowedDue (state: ScheduleState) (now: DateTime) : DateTime option =
        match state.Spec with
        | Some spec when state.DeletedAt.IsNone && state.PausedAt.IsNone ->
            let bound = lowerBound state
            let next = ScheduleRule.next spec.Rule state.Anchor bound
            if next > now then
                Some next
            else
                match spec.CatchUp with
                | CatchUp.Latest -> ScheduleRule.missed spec.Rule state.Anchor bound now CatchUp.Latest |> List.tryLast
                | CatchUp.All -> ScheduleRule.missed spec.Rule state.Anchor bound now CatchUp.All |> List.tryHead
                | CatchUp.None ->
                    match ScheduleRule.missed spec.Rule state.Anchor bound now CatchUp.Latest with
                    | [ latest ] when now - latest <= OnTimeWindow -> Some latest
                    | _ -> Some(ScheduleRule.next spec.Rule state.Anchor now)
        | _ -> None

    let private refuse reason = DeferEvent(ScheduleEvent.Refused reason)

    let private validate (spec: ScheduleSpec) =
        if String.IsNullOrWhiteSpace spec.Workflow then
            Error "A schedule needs a workflow name."
        else
            ScheduleRule.validate spec.Rule

    /// Chooses what to do with a command. The command's `CreationDate` is the current time. A
    /// refusal is a deferred `Refused`. A repeat of an accepted command is answered with a deferred
    /// copy of the first verdict, checked before anything the first command changed.
    let decide (command: Command<ScheduleCommand>) (state: ScheduleState) : EventAction<ScheduleEvent> =
        let now = command.CreationDate
        match command.CommandDetails, state.Spec with
        | ScheduleCommand.Create _, _ when state.DeletedAt.IsSome ->
            refuse "The schedule was deleted. Use a new ID."
        | ScheduleCommand.Create spec, _ ->
            match state.Created with
            | Some(first, at) when first = spec -> DeferEvent(ScheduleEvent.Created(first, at))
            | Some _ -> refuse "A schedule with this ID exists with a different content."
            | None ->
                match validate spec with
                | Ok() -> PersistEvent(ScheduleEvent.Created(spec, now))
                | Error reason -> refuse reason

        // A run decided before a pause, a change or a delete still gets its answers.
        | ScheduleCommand.Fire due, _ when state.Last |> Option.exists (fun last -> last.Due = due) ->
            DeferEvent(ScheduleEvent.Firing(due, state.Last.Value.Input))
        | ScheduleCommand.Started due, _ ->
            match state.Last with
            | Some last when last.Due = due && last.Started -> DeferEvent(ScheduleEvent.Fired due)
            | Some last when last.Due = due -> PersistEvent(ScheduleEvent.Fired due)
            | _ -> refuse $"No run was decided for {due:O}."

        | _, None -> refuse "The schedule does not exist."
        | ScheduleCommand.Delete, Some _ ->
            match state.DeletedAt with
            | Some at -> DeferEvent(ScheduleEvent.Deleted at)
            | None -> PersistEvent(ScheduleEvent.Deleted now)
        | _, Some _ when state.DeletedAt.IsSome -> refuse "The schedule was deleted."

        | ScheduleCommand.Change spec, Some current ->
            match validate spec with
            | Error reason -> refuse reason
            // The same content as now: a repeat, which must not move the anchor.
            | Ok() when spec = current -> DeferEvent(ScheduleEvent.Changed(current, state.Anchor))
            | Ok() -> PersistEvent(ScheduleEvent.Changed(spec, now))
        | ScheduleCommand.Pause, Some _ ->
            match state.PausedAt with
            | Some at -> DeferEvent(ScheduleEvent.Paused at)
            | None -> PersistEvent(ScheduleEvent.Paused now)
        | ScheduleCommand.Resume, Some _ ->
            match state.PausedAt, state.ResumedAt with
            | Some _, _ -> PersistEvent(ScheduleEvent.Resumed now)
            | None, Some at -> DeferEvent(ScheduleEvent.Resumed at)
            | None, None -> refuse "The schedule is not paused."

        | ScheduleCommand.Fire _, Some _ when state.PausedAt.IsSome -> refuse "The schedule is paused."
        | ScheduleCommand.Fire _, Some _ when state.Last |> Option.exists (fun last -> not last.Started) ->
            refuse $"The run for {state.Last.Value.Due:O} has not been reported started."
        | ScheduleCommand.Fire due, Some spec ->
            match allowedDue state now with
            | Some allowed when allowed = due -> PersistEvent(ScheduleEvent.Firing(due, spec.Input))
            | Some allowed -> refuse $"{due:O} is not the due time to run; {allowed:O} is."
            | None -> refuse $"{due:O} is not the due time to run."

    /// Applies one event. It runs for stored events and for deferred replies, so a refusal or a
    /// repeated reply changes nothing.
    let fold (event: Event<ScheduleEvent>) (state: ScheduleState) : ScheduleState =
        match event.EventDetails with
        | ScheduleEvent.Created _ when state.Created.IsSome -> state
        | ScheduleEvent.Created(spec, at) ->
            { state with
                Created = Some(spec, at)
                Spec = Some spec
                Anchor = at }
        // A repeated change carries the current spec and anchor, so this leaves the state as it was.
        | ScheduleEvent.Changed(spec, at) -> { state with Spec = Some spec; Anchor = at }
        | ScheduleEvent.Paused _ when state.PausedAt.IsSome -> state
        | ScheduleEvent.Paused at -> { state with PausedAt = Some at }
        | ScheduleEvent.Resumed _ when state.PausedAt.IsNone -> state
        | ScheduleEvent.Resumed at -> { state with PausedAt = None; ResumedAt = Some at }
        | ScheduleEvent.Deleted _ when state.DeletedAt.IsSome -> state
        | ScheduleEvent.Deleted at -> { state with DeletedAt = Some at }
        | ScheduleEvent.Firing(due, _) when state.Last |> Option.exists (fun last -> last.Due = due) -> state
        | ScheduleEvent.Firing(due, input) ->
            { state with Last = Some { Due = due; Input = input; Started = false } }
        | ScheduleEvent.Fired due ->
            match state.Last with
            | Some last when last.Due = due -> { state with Last = Some { last with Started = true } }
            | _ -> state
        | ScheduleEvent.Refused _ -> state
