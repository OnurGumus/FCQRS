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

    /// How late a `Fire` may arrive for a due time and still be on time under `CatchUp.None`: one
    /// minute. A due time more than a minute old when its `Fire` arrives was missed, and
    /// `CatchUp.None` skips it. For a rule whose due times are less than a minute apart, a gap
    /// shorter than a minute therefore runs its latest due time, as `CatchUp.Latest` does.
    let OnTimeWindow = TimeSpan.FromMinutes 1.0

    /// How early a `Fire` may arrive for a due time: one second, which covers timer imprecision. A
    /// `Fire` that arrives earlier is refused and changes nothing, so a dispatcher fault cannot
    /// start a run early. The same due time is accepted when its time comes.
    let EarlyTolerance = TimeSpan.FromSeconds 1.0

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
    /// `OnTimeWindow`, otherwise the next due time after `now`. The result can be later than `now`:
    /// `decide` accepts it only from `EarlyTolerance` before it. A pending `Firing` is not checked here.
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
            | Some allowed when allowed = due && due - now > EarlyTolerance ->
                refuse $"The due time {due:O} has not come; it is {now:O}."
            // The allowed value is stored, not the one named: a run ID formats the due time, and a
            // due time named with another DateTimeKind would format differently.
            | Some allowed when allowed = due -> PersistEvent(ScheduleEvent.Firing(DateTime.SpecifyKind(allowed, DateTimeKind.Utc), spec.Input))
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

open FCQRS.Common
open Microsoft.Extensions.Logging

/// The input of a run that a schedule starts. A workflow body cannot read its own ID, so the run
/// gets the ID of its schedule and the due time it serves.
type ScheduledRun<'Input> =
    { /// The schedule's ID.
      Schedule: string
      /// The due time the run serves, in UTC.
      Due: DateTime
      /// The schedule's input.
      Input: 'Input }

/// A registered workflow that schedules may start, with its input type erased. Make one with
/// `ScheduledWorkflow.ofHandle`, or `ScheduleHost.Workflow` in C#.
[<Sealed>]
type ScheduledWorkflow internal (name: string, inputType: Type, start: string -> string -> DateTime -> Payload -> Async<unit>) =
    /// The workflow's name.
    member _.Name = name
    /// The type of the schedule input the workflow's runs take.
    member internal _.InputType = inputType
    /// Starts a run: run ID, schedule ID, due time, and the stored schedule input. A run that
    /// exists already is left as it is.
    member internal _.Start = start

/// Makes a `ScheduledWorkflow`.
module ScheduledWorkflow =

    /// A workflow registered with `Workflows.register` whose input is a `ScheduledRun`.
    let ofHandle (handle: WorkflowHandle<ScheduledRun<'Input>, 'Result>) : ScheduledWorkflow =
        ScheduledWorkflow(
            handle.Name,
            typeof<'Input>,
            fun runId schedule due input ->
                async {
                    let run =
                        { Schedule = schedule
                          Due = due
                          Input = Payload.toValue<'Input> input }

                    let! _ = handle.Start runId run
                    return ()
                }
        )

/// A schedule as the dispatcher's index knows it. The index follows the schedule's events through a
/// projection, so it can lag the schedule by one projection pass. All times are UTC.
type ScheduleView =
    { /// The schedule's ID.
      Id: string
      /// The workflow each run starts.
      Workflow: string
      /// When the schedule is due.
      Rule: ScheduleRule
      /// What the schedule does about missed due times.
      CatchUp: CatchUp
      /// The creation or last change, which an `Every` rule counts from.
      Anchor: DateTime
      /// Whether the schedule is paused.
      Paused: bool
      /// Whether the schedule was deleted.
      Deleted: bool
      /// The last due time the schedule accepted.
      LastDue: DateTime option
      /// Whether the run for `LastDue` is known to have started.
      LastStarted: bool
      /// The due time the schedule accepts next, or nothing while it is paused or deleted.
      NextDue: DateTime option }

/// The one schedule dispatcher of an actor system. It keeps an index of every schedule from a
/// projection over the schedule aggregate's events, sleeps until the earliest due time, and starts
/// runs. The index is a hint: every run is decided by the schedule aggregate.
type internal Dispatcher
    (
        api: IActor,
        send: string -> FCQRS.Model.Data.CID -> ScheduleCommand -> Async<Event<ScheduleEvent>>,
        workflows: Collections.Generic.IReadOnlyDictionary<string, ScheduledWorkflow>,
        checkInterval: TimeSpan,
        logger: ILogger
    ) =

    let gate = obj ()
    let index = Collections.Generic.Dictionary<string, ScheduleState>()
    // A due time not to fire again before a time, after a refusal or a failure.
    let deferred = Collections.Generic.Dictionary<string, DateTime * DateTime>()
    // The due time each schedule whose workflow is not registered was last reported for.
    let reported = Collections.Generic.Dictionary<string, DateTime>()
    // Each schedule's next due time while it is in the future. It stays right until the schedule's
    // entry changes, so a pass over the index computes only the due times that passed or changed.
    let upcoming = Collections.Generic.Dictionary<string, DateTime>()
    let held = Collections.Generic.Queue<string * Event<ScheduleEvent>>()
    let mutable holding = false
    let mutable refusals = 0
    let mutable afterFiring: string -> DateTime -> bool = fun _ _ -> true
    let wake = new Threading.SemaphoreSlim(0, 1)
    let lifetime = new Threading.CancellationTokenSource()

    // How many schedules fire at once when several are due.
    let parallelFires = 16
    // How long a failed fire waits before it is tried again.
    let retryDelay = min checkInterval (TimeSpan.FromSeconds 5.0)
    // The longest single wait; a semaphore wait takes at most Int32.MaxValue milliseconds.
    let longestWait = TimeSpan.FromDays 1.0

    let blank: Payload = { Type = ""; Json = "" }

    // The index keeps no inputs: a run's input comes from the schedule's reply to Fire.
    let strip (state: ScheduleState) =
        { state with
            Created = state.Created |> Option.map (fun (spec, at) -> { spec with Input = blank }, at)
            Spec = state.Spec |> Option.map (fun spec -> { spec with Input = blank })
            Last = state.Last |> Option.map (fun last -> { last with Input = blank }) }

    let signal () =
        try
            if wake.CurrentCount = 0 then wake.Release() |> ignore
        with :? Threading.SemaphoreFullException ->
            ()

    let now () = api.TimeProvider.GetUtcNow().UtcDateTime

    let runIdOf (id: string) (due: DateTime) =
        let due = DateTime.SpecifyKind(due, DateTimeKind.Utc)
        $"""{id}@{due.ToString "O"}"""

    let cidOf (text: string) : FCQRS.Model.Data.CID =
        FCQRS.FSharp.Fcqrs.cid text

    // Folds one event into the index; true when the entry changed. The dispatcher folds the replies
    // to its own Fire and Started before the projection delivers them, and accepted due times only
    // grow, so a Firing or a Fired older than the entry's last due time changes nothing.
    let apply (id: string) (event: Event<ScheduleEvent>) =
        let state =
            match index.TryGetValue id with
            | true, state -> state
            | _ -> Schedule.initial

        let next =
            match event.EventDetails with
            | ScheduleEvent.Firing(due, _) when state.Last |> Option.exists (fun last -> last.Due >= due) -> state
            | ScheduleEvent.Fired due when state.Last |> Option.exists (fun last -> last.Due > due) -> state
            | ScheduleEvent.Refused _ -> state
            | _ -> Schedule.fold event state |> strip

        if next <> state then
            index[id] <- next
            upcoming.Remove id |> ignore
            true
        else
            false

    // An event the projection read. A change clears a deferred due time, which the change may
    // have made right or wrong.
    let observed (id: string) (event: Event<ScheduleEvent>) =
        if apply id event then
            deferred.Remove id |> ignore

    let report (id: string) (spec: ScheduleSpec) (due: DateTime) =
        match reported.TryGetValue id with
        | true, seen when seen = due -> ()
        | _ ->
            reported[id] <- due
            logger.LogError(
                "Schedule {Schedule} is due at {Due:O}, but its workflow {Workflow} is not registered with the schedule dispatcher. No run starts until it is.",
                id,
                due,
                spec.Workflow
            )

    // What is due at `now`, and when to look again.
    let plan (now: DateTime) =
        lock gate (fun () ->
            let due = ResizeArray<string * DateTime * ScheduledWorkflow>()
            let mutable wakeAt = now + checkInterval
            let later (at: DateTime) = if at < wakeAt then wakeAt <- at

            for KeyValue(id, state) in index do
                try
                    match state.Spec with
                    | None -> ()
                    | Some spec ->
                        // A Firing without its Fired: the run may not have started. Its start is
                        // repeated whatever the schedule's state is now.
                        let pending = state.Last |> Option.filter (fun last -> not last.Started) |> Option.map _.Due

                        let next =
                            match pending with
                            | Some _ -> pending
                            | None ->
                                match upcoming.TryGetValue id with
                                | true, cached when cached > now -> Some cached
                                | _ ->
                                    let allowed = Schedule.allowedDue state now

                                    match allowed with
                                    | Some allowed when allowed > now -> upcoming[id] <- allowed
                                    | _ -> upcoming.Remove id |> ignore

                                    allowed

                        match next with
                        | None -> ()
                        | Some next ->
                            match deferred.TryGetValue id with
                            | true, (refused, until) when refused = next && until > now -> later until
                            | _ when next > now -> later next
                            | _ ->
                                match workflows.TryGetValue spec.Workflow with
                                | true, workflow -> due.Add((id, next, workflow))
                                | _ ->
                                    report id spec next
                                    // Report again at the next due time.
                                    if pending.IsNone then
                                        later (ScheduleRule.next spec.Rule state.Anchor now)
                with error ->
                    logger.LogError(error, "The schedule dispatcher could not compute the next due time of schedule {Schedule}.", id)

            List.ofSeq due, wakeAt)

    let deferTo (id: string) (due: DateTime) (until: DateTime) =
        lock gate (fun () -> deferred[id] <- (due, until))

    // Asks the schedule to fire, starts the run with the input from its reply, and reports the start.
    let fire (startedAt: DateTime) (id: string, due: DateTime, workflow: ScheduledWorkflow) =
        async {
            try
                let! reply = send id (cidOf (runIdOf id due)) (ScheduleCommand.Fire due)

                match reply.EventDetails with
                | ScheduleEvent.Firing(accepted, input) ->
                    lock gate (fun () -> apply id reply |> ignore)

                    if not (afterFiring id accepted) then
                        lifetime.Cancel()
                    else
                        let runId = runIdOf id accepted
                        do! workflow.Start runId id accepted input
                        let! started = send id (cidOf $"{runId}/started") (ScheduleCommand.Started accepted)

                        match started.EventDetails with
                        | ScheduleEvent.Fired _ -> lock gate (fun () -> apply id started |> ignore)
                        | other ->
                            logger.LogWarning("Schedule {Schedule} answered the start of run {Run} with {Reply}.", id, runId, other)
                            deferTo id due (startedAt + retryDelay)
                | ScheduleEvent.Refused reason ->
                    lock gate (fun () -> refusals <- refusals + 1)

                    logger.LogDebug("Schedule {Schedule} refused a run for {Due:O}: {Reason}", id, due, reason)
                    // Refused only for being early: try again at the due time. Otherwise the index
                    // was behind, and the change it missed arrives from the projection.
                    if due - reply.CreationDate > Schedule.EarlyTolerance then
                        deferTo id due (max due (startedAt + Schedule.EarlyTolerance))
                    else
                        deferTo id due (startedAt + checkInterval)
                | other ->
                    logger.LogWarning("Schedule {Schedule} answered a fire for {Due:O} with {Reply}.", id, due, other)
                    deferTo id due (startedAt + retryDelay)
            with
            | _ when lifetime.IsCancellationRequested -> ()
            | :? TimeoutException as error ->
                logger.LogWarning(error, "The run of schedule {Schedule} for {Due:O} did not start in time. It is tried again.", id, due)
                deferTo id due (startedAt + retryDelay)
            | error ->
                logger.LogError(error, "The run of schedule {Schedule} for {Due:O} did not start. It is tried again.", id, due)
                deferTo id due (startedAt + retryDelay)
        }

    let loop (projection: FCQRS.Projections.IProjection) : Threading.Tasks.Task =
        task {
            // A run decided before a restart is found once the index has read the whole journal.
            try
                do! projection.CatchUpAsync(lifetime.Token)
            with error ->
                if not lifetime.IsCancellationRequested then
                    logger.LogWarning(error, "The schedule index did not catch up with the journal. The dispatcher starts from what it has read.")

            while not lifetime.IsCancellationRequested do
                let mutable failed = false

                try
                    let at = now ()
                    let due, wakeAt = plan at

                    if due.IsEmpty then
                        let wait = min longestWait (wakeAt - at)
                        let milliseconds = Math.Ceiling(max 0.0 wait.TotalMilliseconds)
                        let! _ = wake.WaitAsync(TimeSpan.FromMilliseconds milliseconds, lifetime.Token)
                        ()
                    else
                        let! _ = Async.StartAsTask(Async.Parallel(due |> List.map (fire at), parallelFires), cancellationToken = lifetime.Token)
                        ()
                with
                | :? OperationCanceledException -> ()
                | error ->
                    logger.LogError(error, "The schedule dispatcher failed. It tries again in a second.")
                    failed <- true

                if failed then
                    try
                        do! Threading.Tasks.Task.Delay(1000, lifetime.Token)
                    with :? OperationCanceledException ->
                        ()
        }

    /// Handles an event the projection read.
    member _.Observe(message: obj) : FCQRS.Model.Data.IMessageWithCID list =
        match message with
        | :? Event<ScheduleEvent> as event ->
            match event.Sender with
            | Some sender ->
                let id = string sender

                lock gate (fun () ->
                    if holding then
                        held.Enqueue(id, event)
                    else
                        observed id event)

                signal ()
            | None -> ()
        | _ -> ()

        []

    /// Starts the loop once the projection exists. It ends when the actor system shuts down.
    member _.Start(projection: FCQRS.Projections.IProjection) =
        let stop () = lifetime.Cancel()

        Akka.Actor.CoordinatedShutdown
            .Get(api.System)
            .AddTask(
                Akka.Actor.CoordinatedShutdown.PhaseBeforeServiceUnbind,
                "fcqrs-schedule-dispatcher",
                fun () ->
                    stop ()
                    Threading.Tasks.Task.FromResult Akka.Done.Instance
            )

        api.System.WhenTerminated.ContinueWith(fun (_: Threading.Tasks.Task) -> stop ()) |> ignore

        projection.Completion.ContinueWith(fun (completion: Threading.Tasks.Task) ->
            if completion.IsFaulted then
                logger.LogCritical(completion.Exception, "The schedule index stopped. Schedules fire from what it had read until the process restarts."))
        |> ignore

        Threading.Tasks.Task.Run(fun () -> loop projection) |> ignore

    /// The index's view of a schedule.
    member _.View(id: string) : ScheduleView option =
        let at = now ()

        lock gate (fun () ->
            match index.TryGetValue id with
            | true, ({ Spec = Some spec } as state) ->
                Some
                    { Id = id
                      Workflow = spec.Workflow
                      Rule = spec.Rule
                      CatchUp = spec.CatchUp
                      Anchor = state.Anchor
                      Paused = state.PausedAt.IsSome
                      Deleted = state.DeletedAt.IsSome
                      LastDue = state.Last |> Option.map _.Due
                      LastStarted = state.Last |> Option.exists _.Started
                      NextDue =
                        try
                            Schedule.allowedDue state at
                        with _ ->
                            None }
            | _ -> None)

    /// Test hook: called after a Firing is stored and before the run starts. Returning false stops
    /// the dispatcher there, as a crash would.
    member _.AfterFiring
        with get () = afterFiring
        and set (value: string -> DateTime -> bool) = afterFiring <- value

    /// Test hook: stops applying the projection's events to the index.
    member _.HoldIndex() = lock gate (fun () -> holding <- true)

    /// Test hook: applies the held events and stops holding.
    member _.ReleaseIndex() =
        lock gate (fun () ->
            holding <- false

            while held.Count > 0 do
                let id, event = held.Dequeue()
                observed id event)

        signal ()

    /// How many fires the schedules refused.
    member _.Refusals = lock gate (fun () -> refusals)

/// The registered schedule dispatcher. Pass it to the functions of `Schedules`.
[<Sealed>]
type ScheduleHandle
    internal
    (
        send: string -> FCQRS.Model.Data.CID -> ScheduleCommand -> Async<Event<ScheduleEvent>>,
        workflows: Collections.Generic.IReadOnlyDictionary<string, ScheduledWorkflow>,
        dispatcher: Dispatcher
    ) =
    member internal _.Send = send
    member internal _.Workflows = workflows
    member internal _.Dispatcher = dispatcher

/// Schedules that start a registered workflow at each due time. A schedule is created, changed,
/// paused, resumed and deleted while the application runs, and is stored in the journal as an
/// aggregate named "fcqrs-schedule". The run for a due time is a workflow instance whose ID is the
/// schedule's ID, "@", and the due time in UTC in ISO 8601 ("O") form. A run starts at least once
/// per accepted due time, and a repeated start with its ID stores nothing.
module Schedules =

    open FCQRS.FSharp

    let private checkIntervalKey = "akka.fcqrs.schedule-check-interval"

    /// The longest the dispatcher sleeps before it reads its index again: one minute unless the
    /// configuration says otherwise.
    let private checkIntervalOf (config: Akka.Configuration.Config) : TimeSpan =
        if config.HasPath checkIntervalKey then
            // A bare number is seconds, as for `akka.fcqrs.command-timeout`.
            match config.GetString checkIntervalKey |> Int32.TryParse with
            | true, seconds -> TimeSpan.FromSeconds(float seconds)
            | _ -> config.GetTimeSpan(checkIntervalKey, Nullable(TimeSpan.FromMinutes 1.0))
        else
            TimeSpan.FromMinutes 1.0

    let private registered =
        Runtime.CompilerServices.ConditionalWeakTable<Akka.Actor.ActorSystem, obj>()

    /// The longest schedule ID. A run ID adds "@" and 28 characters of due time, and the correlation
    /// IDs of a run's commands add a few more; they must fit in 255.
    [<Literal>]
    let MaxIdLength = 200

    let private checkId (id: string) =
        if String.IsNullOrWhiteSpace id then
            invalidArg (nameof id) "A schedule ID must not be blank."

        if id.Contains '~' then
            invalidArg (nameof id) $"Schedule ID '{id}' contains '~', which a workflow ID cannot contain."

        if id.Length > MaxIdLength then
            invalidArg (nameof id) $"A schedule ID must be at most {MaxIdLength} characters."

    /// Registers the schedule aggregate and starts this actor system's dispatcher, which may start
    /// the listed workflows. Register each workflow with `Workflows.register` first. Raises
    /// `InvalidOperationException` when the actor system has a dispatcher already. A schedule whose
    /// workflow is not listed starts no run, and the dispatcher logs an error at each of its due
    /// times, until a later start lists the workflow again.
    let register (api: IActor) (runs: ScheduledWorkflow list) : ScheduleHandle =
        let workflows = Collections.Generic.Dictionary<string, ScheduledWorkflow>()

        for run in runs do
            if not (workflows.TryAdd(run.Name, run)) then
                invalidArg (nameof runs) $"Workflow '{run.Name}' is listed twice."

        lock registered (fun () ->
            match registered.TryGetValue api.System with
            | true, _ -> invalidOp "This actor system has a schedule dispatcher already. Call Schedules.register once."
            | _ -> registered.Add(api.System, obj ()))

        let aggregate =
            Fcqrs.aggregate
                api
                { Name = Schedule.EntityName
                  Initial = Schedule.initial
                  Decide = Schedule.decide
                  Fold = Schedule.fold
                  Snapshots = SnapshotPolicy.Default
                  Passivation = PassivationPolicy.Default }

        let send (id: string) cid command =
            aggregate.Send cid (Fcqrs.aggregateId id) command (fun _ -> true)

        let logger = api.LoggerFactory.CreateLogger "FCQRS.Workflows.Schedules"
        let dispatcher = Dispatcher(api, send, workflows, checkIntervalOf api.System.Settings.Config, logger)

        let projection =
            Fcqrs.projection api (Projection.multi FromStart dispatcher.Observe |> Projection.ofEntities [ Schedule.EntityName ])

        dispatcher.Start projection
        ScheduleHandle(send, workflows, dispatcher)

    let private run (handle: ScheduleHandle) (id: string) (command: ScheduleCommand) =
        async {
            let! reply = handle.Send id (Fcqrs.newCid ()) command

            match reply.EventDetails with
            | ScheduleEvent.Refused reason -> return invalidOp $"Schedule '{id}': {reason}"
            | _ -> return ()
        }

    let private specOf (handle: ScheduleHandle) (workflow: string) (input: 'Input) rule catchUp : ScheduleSpec =
        match handle.Workflows.TryGetValue workflow with
        | true, registered when registered.InputType = typeof<'Input> ->
            { Workflow = workflow
              Input = Payload.ofValue input
              Rule = rule
              CatchUp = catchUp }
        | true, registered ->
            invalidOp $"Workflow '{workflow}' takes a {registered.InputType.Name} as its schedule input, not a {typeof<'Input>.Name}."
        | _ -> invalidOp $"Workflow '{workflow}' is not registered with the schedule dispatcher."

    /// Creates the schedule `id`, which starts the workflow named `workflow` with `input` at each
    /// due time of `rule`. Returns once the schedule is stored. Raises `InvalidOperationException`
    /// when the workflow is not registered with the dispatcher or takes another input, and when the
    /// schedule refuses: a schedule with this ID exists with a different content, or was deleted. A
    /// repeat with the same content is accepted and stores nothing.
    let create (handle: ScheduleHandle) (id: string) (workflow: string) (input: 'Input) (rule: ScheduleRule) (catchUp: CatchUp) : Async<unit> =
        async {
            checkId id
            let spec = specOf handle workflow input rule catchUp
            return! run handle id (ScheduleCommand.Create spec)
        }

    /// Replaces the schedule's workflow, input, rule and catch-up policy. An `Every` rule counts from
    /// the change, and no due time at or before it runs. A run decided before the change keeps the
    /// input it was decided with. Raises `InvalidOperationException` as `create` does, and when the
    /// schedule does not exist or was deleted.
    let change (handle: ScheduleHandle) (id: string) (workflow: string) (input: 'Input) (rule: ScheduleRule) (catchUp: CatchUp) : Async<unit> =
        async {
            checkId id
            let spec = specOf handle workflow input rule catchUp
            return! run handle id (ScheduleCommand.Change spec)
        }

    /// Stops the schedule from starting runs. A run decided before the pause still starts. Raises
    /// `InvalidOperationException` when the schedule does not exist or was deleted.
    let pause (handle: ScheduleHandle) (id: string) : Async<unit> =
        async {
            checkId id
            return! run handle id ScheduleCommand.Pause
        }

    /// Lets a paused schedule start runs again, from the first due time after the resume: due
    /// times inside the pause never run. Raises `InvalidOperationException` when the schedule does
    /// not exist, was deleted, or is not paused.
    let resume (handle: ScheduleHandle) (id: string) : Async<unit> =
        async {
            checkId id
            return! run handle id ScheduleCommand.Resume
        }

    /// Ends the schedule. A run decided before the delete still starts. The ID cannot be used for a
    /// new schedule. Raises `InvalidOperationException` when the schedule does not exist.
    let delete (handle: ScheduleHandle) (id: string) : Async<unit> =
        async {
            checkId id
            return! run handle id ScheduleCommand.Delete
        }

    /// The schedule as the dispatcher's index knows it, or nothing when the index has not read it.
    /// The index lags the schedule by one projection pass, so a change that just returned may not
    /// show yet.
    let state (handle: ScheduleHandle) (id: string) : ScheduleView option = handle.Dispatcher.View id
