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
    /// Start no run for a missed due time and wait for the next one in the future. A due time
    /// whose run is asked for within one minute of it (`Schedule.OnTimeWindow`) is on time, not
    /// missed, so a rule whose due times are less than a minute apart runs its latest one after a
    /// shorter gap, as `Latest` does.
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

    /// The longest `Every` interval: 366 days, which keeps every due time far from the largest
    /// `DateTime`.
    let MaxInterval = TimeSpan.FromDays 366.0

    /// Checks that a rule can produce due times: a time zone this system can read, at least one day
    /// that exists, and an interval from one second to `MaxInterval` (366 days). Returns an error,
    /// never an exception, whatever the rule holds.
    let validate (rule: ScheduleRule) : Result<unit, string> =
        let checkZone (zone: string) =
            if String.IsNullOrWhiteSpace zone then
                Error "A schedule needs a time zone."
            else
                // Not only TimeZoneNotFoundException: on macOS an ID that names a directory of the
                // zone database, such as "Europe", raises SecurityException.
                try
                    zoneOf zone |> ignore
                    Ok()
                with _ ->
                    Error $"Unknown time zone '{zone}'."

        let checkDays (days: DayOfWeek list) =
            if isNull (box days) || days.IsEmpty then
                Error "A weekly schedule needs at least one day."
            else
                match days |> List.tryFind (fun day -> not (Enum.IsDefined day)) with
                | Some day -> Error $"{int day} is not a day of the week."
                | Option.None -> Ok()

        if isNull (box rule) then
            Error "A schedule needs a rule."
        else
            match rule with
            | ScheduleRule.Daily(_, zone) -> checkZone zone
            | ScheduleRule.Weekly(days, _, zone) -> checkDays days |> Result.bind (fun () -> checkZone zone)
            | ScheduleRule.Every interval when interval < TimeSpan.FromSeconds 1.0 -> Error "The interval must be at least 1 second."
            | ScheduleRule.Every interval when interval > MaxInterval -> Error "The interval must be at most 366 days."
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
    /// A run for `due` was decided, with the workflow it starts and the input it gets. A later
    /// change does not alter either.
    | Firing of due: DateTime * workflow: string * input: Payload
    /// The run for `due` has been started.
    | Fired of due: DateTime
    /// A command was refused. Never stored.
    | Refused of reason: string

/// The last due time a schedule accepted.
type AcceptedDue =
    { /// The due time, in UTC.
      Due: DateTime
      /// The workflow its run starts.
      Workflow: string
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
    /// start a run more than a second early. The same due time is accepted when its time comes.
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

    // Total, like ScheduleRule.validate: the actor treats an exception from decide as fatal.
    let private validate (spec: ScheduleSpec) =
        if String.IsNullOrWhiteSpace spec.Workflow then
            Error "A schedule needs a workflow name."
        elif isNull (box spec.CatchUp) then
            Error "A schedule needs a catch-up policy."
        elif isNull (box spec.Input) then
            Error "A schedule needs an input."
        else
            ScheduleRule.validate spec.Rule

    /// Chooses what to do with a command. The command's `CreationDate` is the current time. A
    /// refusal is a deferred `Refused`. A repeat of an accepted command is answered with a deferred
    /// copy of the first verdict, checked before anything the first command changed. A spec that
    /// fails validation, or a due time that cannot be computed, is refused: decide raises nothing.
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
            DeferEvent(ScheduleEvent.Firing(due, state.Last.Value.Workflow, state.Last.Value.Input))
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
            // A stored rule can stop computing, for example when a system update removes its zone.
            let allowed =
                try
                    Ok(allowedDue state now)
                with error ->
                    Error error.Message

            match allowed with
            | Error reason -> refuse $"The due time could not be computed: {reason}"
            | Ok(Some allowed) when allowed = due && due - now > EarlyTolerance ->
                refuse $"The due time {due:O} has not come; it is {now:O}."
            // The allowed value is stored, not the one named: a run ID formats the due time, and a
            // due time named with another DateTimeKind would format differently.
            | Ok(Some allowed) when allowed = due -> PersistEvent(ScheduleEvent.Firing(DateTime.SpecifyKind(allowed, DateTimeKind.Utc), spec.Workflow, spec.Input))
            | Ok(Some allowed) -> refuse $"{due:O} is not the due time to run; {allowed:O} is."
            | Ok None -> refuse $"{due:O} is not the due time to run."

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
        | ScheduleEvent.Firing(due, _, _) when state.Last |> Option.exists (fun last -> last.Due = due) -> state
        | ScheduleEvent.Firing(due, workflow, input) ->
            { state with
                Last =
                    Some
                        { Due = due
                          Workflow = workflow
                          Input = input
                          Started = false } }
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
      /// Whether the schedule was deleted. A deleted schedule is in the index only while a run decided
      /// before the delete waits to start.
      Deleted: bool
      /// The last due time the schedule accepted.
      LastDue: DateTime option
      /// Whether the run for `LastDue` is known to have started.
      LastStarted: bool
      /// The due time the schedule accepts next, or nothing while it is paused or deleted.
      NextDue: DateTime option }

/// The dispatcher's stored index: a table in the journal's database with one row per schedule, kept
/// by a projection that writes each row in the transaction that stores its read position. The
/// table's name and columns, the row text, and the projection's name are persistent contracts.
module internal ScheduleIndex =

    open System.Globalization
    open System.Text.Json.Nodes

    /// The table, in the journal's schema.
    [<Literal>]
    let Table = "fcqrs_schedule_index"

    /// The table's columns: the schedule's ID, and its entry as `encode` writes it.
    [<Literal>]
    let Columns = "schedule_id TEXT NOT NULL PRIMARY KEY, entry TEXT NOT NULL"

    /// The projection that keeps the table. Its read positions are stored under this name.
    [<Literal>]
    let ProjectionName = "fcqrs-schedule-index"

    /// The version of the row text `encode` writes. `decode` refuses another.
    [<Literal>]
    let Format = 1

    let private blank: Payload = { Type = ""; Json = "" }

    /// The entry without inputs: a run's input comes from the schedule's reply to Fire.
    let strip (state: ScheduleState) =
        { state with
            Created = state.Created |> Option.map (fun (spec, at) -> { spec with Input = blank }, at)
            Spec = state.Spec |> Option.map (fun spec -> { spec with Input = blank })
            Last = state.Last |> Option.map (fun last -> { last with Input = blank }) }

    /// The entry after one event, or nothing when the schedule leaves the index. The dispatcher folds
    /// the replies to its own Fire and Started before the projection delivers them, and accepted due
    /// times only grow, so a Firing or a Fired older than the entry's last due time changes nothing.
    /// A deleted schedule leaves the index once no run waits to be started, so deleted schedules take
    /// no memory and no row; an entry without a spec can only come from events that arrived after
    /// that.
    let advance (state: ScheduleState) (event: Event<ScheduleEvent>) : ScheduleState option =
        let next =
            match event.EventDetails with
            | ScheduleEvent.Firing(due, _, _) when state.Last |> Option.exists (fun last -> last.Due >= due) -> state
            | ScheduleEvent.Fired due when state.Last |> Option.exists (fun last -> last.Due > due) -> state
            | ScheduleEvent.Refused _ -> state
            | _ -> Schedule.fold event state |> strip

        let finished = next.DeletedAt.IsSome && next.Last |> Option.forall _.Started
        if finished || next.Spec.IsNone then None else Some next

    let private text (value: string) : JsonNode =
        match JsonValue.Create value with
        | null -> raise (ArgumentNullException(nameof value, "An index entry holds no null text."))
        | node -> node

    let private time (value: DateTime) = text (DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture))
    let private optional (write: 'T -> JsonNode) (value: 'T option) : JsonNode | null = value |> Option.map write |> Option.toObj

    let private objectOf (fields: (string * (JsonNode | null)) list) : JsonNode =
        let node = JsonObject()
        for name, value in fields do
            node[name] <- value
        node

    let private rule (value: ScheduleRule) =
        let timeOf (at: TimeOnly) = text (at.ToString("O", CultureInfo.InvariantCulture))
        match value with
        | ScheduleRule.Daily(at, zone) -> objectOf [ "daily", objectOf [ "time", timeOf at; "zone", text zone ] ]
        | ScheduleRule.Weekly(days, at, zone) ->
            let names = JsonArray([| for day in days -> text (string day) |])
            objectOf [ "weekly", objectOf [ "days", names; "time", timeOf at; "zone", text zone ] ]
        | ScheduleRule.Every interval -> objectOf [ "every", text (interval.ToString("c", CultureInfo.InvariantCulture)) ]

    let private catchUp (value: CatchUp) =
        text (
            match value with
            | CatchUp.Latest -> "Latest"
            | CatchUp.All -> "All"
            | CatchUp.None -> "None"
        )

    let private spec (value: ScheduleSpec) =
        objectOf [ "workflow", text value.Workflow; "rule", rule value.Rule; "catchUp", catchUp value.CatchUp ]

    /// The row text of an index entry. Inputs are not stored.
    let encode (state: ScheduleState) : string =
        objectOf
            [ "format", JsonValue.Create Format
              "created", state.Created |> optional (fun (created, at) -> objectOf [ "spec", spec created; "at", time at ])
              "spec", state.Spec |> optional spec
              "anchor", time state.Anchor
              "pausedAt", state.PausedAt |> optional time
              "resumedAt", state.ResumedAt |> optional time
              "deletedAt", state.DeletedAt |> optional time
              "last",
              state.Last
              |> optional (fun last -> objectOf [ "due", time last.Due; "workflow", text last.Workflow; "started", JsonValue.Create last.Started ]) ]
        |> _.ToJsonString()

    let private field (node: JsonNode) (name: string) : JsonNode =
        match node[name] with
        | null -> raise (FormatException $"The index row has no '{name}'.")
        | value -> value

    let private optionalField (node: JsonNode) (name: string) : JsonNode option =
        let mutable value: JsonNode | null = null

        if node.AsObject().TryGetPropertyValue(name, &value) then
            Option.ofObj value
        else
            raise (FormatException $"The index row has no '{name}'.")

    let private textOf (node: JsonNode) = node.GetValue<string>()
    let private timeOf (node: JsonNode) = DateTime.ParseExact(textOf node, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
    let private timeOnlyOf (node: JsonNode) = TimeOnly.ParseExact(textOf node, "O", CultureInfo.InvariantCulture)

    let private ruleOf (node: JsonNode) =
        let cases = node.AsObject()
        if cases.Count <> 1 then
            raise (FormatException "An index row's rule has one case.")

        let case = Seq.head cases
        let value = case.Value |> Option.ofObj |> Option.defaultWith (fun () -> raise (FormatException "An index row's rule is empty."))

        match case.Key with
        | "daily" -> ScheduleRule.Daily(timeOnlyOf (field value "time"), textOf (field value "zone"))
        | "weekly" ->
            let days =
                [ for day in (field value "days").AsArray() ->
                      match day with
                      | null -> raise (FormatException "An index row's day is empty.")
                      | day -> Enum.Parse<DayOfWeek>(textOf day) ]

            ScheduleRule.Weekly(days, timeOnlyOf (field value "time"), textOf (field value "zone"))
        | "every" -> ScheduleRule.Every(TimeSpan.ParseExact(textOf value, "c", CultureInfo.InvariantCulture))
        | other -> raise (FormatException $"Unknown rule '{other}' in an index row.")

    let private catchUpOf (node: JsonNode) =
        match textOf node with
        | "Latest" -> CatchUp.Latest
        | "All" -> CatchUp.All
        | "None" -> CatchUp.None
        | other -> raise (FormatException $"Unknown catch-up policy '{other}' in an index row.")

    let private specOf (node: JsonNode) : ScheduleSpec =
        { Workflow = textOf (field node "workflow")
          Input = blank
          Rule = ruleOf (field node "rule")
          CatchUp = catchUpOf (field node "catchUp") }

    /// The index entry a row's text holds. Raises `FormatException` for text `encode` did not write.
    let decode (row: string) : ScheduleState =
        let node =
            match JsonNode.Parse row with
            | null -> raise (FormatException "The index row is empty.")
            | node -> node

        match (field node "format").GetValue<int>() with
        | Format -> ()
        | other -> raise (FormatException $"The index row has format {other}; this version reads format {Format}.")

        { Created = optionalField node "created" |> Option.map (fun created -> specOf (field created "spec"), timeOf (field created "at"))
          Spec = optionalField node "spec" |> Option.map specOf
          Anchor = timeOf (field node "anchor")
          PausedAt = optionalField node "pausedAt" |> Option.map timeOf
          ResumedAt = optionalField node "resumedAt" |> Option.map timeOf
          DeletedAt = optionalField node "deletedAt" |> Option.map timeOf
          Last =
            optionalField node "last"
            |> Option.map (fun last ->
                { Due = timeOf (field last "due")
                  Workflow = textOf (field last "workflow")
                  Input = blank
                  Started = (field last "started").GetValue<bool>() }) }

/// The one schedule dispatcher of an actor system. It keeps an index of every schedule from a
/// projection over the schedule aggregate's events, sleeps until the earliest due time, and starts
/// runs. The index is a hint: every run is decided by the schedule aggregate. The projection stores
/// the index in the journal's database (see `ScheduleIndex`), so a start reads one row per schedule
/// and the events stored since, not the schedules' whole history.
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
    // The schedules whose fire is running. A schedule has at most one, and the loop does not wait
    // for it, so a start that hangs delays only its own schedule.
    let inFlight = Collections.Generic.HashSet<string>()
    // Schedules to look at before the next pass over the index: those a pass found due, whose entry
    // changed, or whose fire ended. Each is looked at again when it gets a slot, so a burst of due
    // schedules costs work in proportion to the burst, not a pass over the index per fire.
    let ready = Collections.Generic.Queue<string>()
    let queued = Collections.Generic.HashSet<string>()
    // When the next pass over the index is due: the earliest time a schedule outside `ready` and
    // `inFlight` needs looking at, and at most a check interval (or a day) from now. A clock that
    // steps back would leave it further away than that, so `plan` brings it back first.
    let mutable wakeAt = DateTime.MinValue
    let held = Collections.Generic.Queue<string * Event<ScheduleEvent>>()
    let mutable holding = false
    let mutable refusals = 0
    let mutable computations = 0L
    let mutable afterFiring: string -> DateTime -> bool = fun _ _ -> true
    let wake = new Threading.SemaphoreSlim(0, 1)
    let lifetime = new Threading.CancellationTokenSource()
    let caughtUp = Threading.Tasks.TaskCompletionSource(Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
    let mutable indexProjection: FCQRS.Projections.IProjection option = None

    // How many fires run at once when several schedules are due.
    let parallelFires = 16
    // How long a failed fire waits, from its failure, before it is tried again.
    let retryDelay = min checkInterval (TimeSpan.FromSeconds 5.0)
    // The longest single wait; a semaphore wait takes at most Int32.MaxValue milliseconds.
    let longestWait = TimeSpan.FromDays 1.0

    // The journal's database, which holds the stored index and its read positions. Built here, so
    // a journal the index cannot use raises from Schedules.register.
    let store = FCQRS.Projections.journalStore api
    let table = store.OwnedTable ScheduleIndex.Table

    // Upcasters are fixed when the dispatcher is registered, as when its projection started there.
    do FCQRS.EventUpcasting.Internal.freeze api.System

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

    // Folds one event into the index in memory, as `ScheduleIndex.advance` says; true when the
    // entry changed.
    let apply (id: string) (event: Event<ScheduleEvent>) =
        let state =
            match index.TryGetValue id with
            | true, state -> state
            | _ -> Schedule.initial

        match ScheduleIndex.advance state event with
        | None ->
            upcoming.Remove id |> ignore
            deferred.Remove id |> ignore
            reported.Remove id |> ignore
            index.Remove id
        | Some next when next <> state ->
            index[id] <- next
            upcoming.Remove id |> ignore
            true
        | Some _ -> false

    // Puts a schedule in `ready`, unless it is there already or its fire is running; the end of
    // that fire puts it there.
    let enqueue (id: string) =
        if not (inFlight.Contains id) && queued.Add id then
            ready.Enqueue id

    // An event the projection read. A change clears a deferred due time, which the change may
    // have made right or wrong, and the schedule is looked at again.
    let observed (id: string) (event: Event<ScheduleEvent>) =
        if apply id event then
            deferred.Remove id |> ignore
            enqueue id

    // Logs once per due time of the rule that passes while `workflow` is not registered. `due` is
    // the due time the schedule would accept, which does not move until a fire is accepted, so the
    // log is keyed on the latest due time the rule has reached instead.
    let report (id: string) (state: ScheduleState) (spec: ScheduleSpec) (workflow: string) (due: DateTime) (now: DateTime) =
        let reached =
            ScheduleRule.missed spec.Rule state.Anchor due now CatchUp.Latest
            |> List.tryLast
            |> Option.defaultValue due

        match reported.TryGetValue id with
        | true, seen when seen = reached -> ()
        | _ ->
            reported[id] <- reached
            logger.LogError(
                "Schedule {Schedule} is due at {Due:O}, but its workflow {Workflow} is not registered with the schedule dispatcher. No run starts until it is.",
                id,
                reached,
                workflow
            )

    // Moves the next pass forward to `at`.
    let later (at: DateTime) = if at < wakeAt then wakeAt <- at

    // One schedule at `now`: the due time to fire, when a run should be asked for now. Otherwise
    // nothing, and the next pass is moved forward to the time the schedule needs looking at again.
    // Called under the gate, for a schedule whose fire is not running.
    let evaluate (now: DateTime) (id: string) : DateTime option =
        match index.TryGetValue id with
        | true, ({ Spec = Some spec } as state) ->
            try
                // A Firing without its Fired: the run may not have started. Its start is repeated
                // whatever the schedule's state is now, on the workflow the Firing names.
                let pending = state.Last |> Option.filter (fun last -> not last.Started)

                let workflow =
                    match pending with
                    | Some last -> last.Workflow
                    | None -> spec.Workflow

                let next =
                    match pending with
                    | Some last -> Some last.Due
                    | None ->
                        match upcoming.TryGetValue id with
                        | true, cached when cached > now -> Some cached
                        | _ ->
                            computations <- computations + 1L
                            let allowed = Schedule.allowedDue state now

                            match allowed with
                            | Some allowed when allowed > now -> upcoming[id] <- allowed
                            | _ -> upcoming.Remove id |> ignore

                            allowed

                match next with
                | None -> None
                | Some next ->
                    match deferred.TryGetValue id with
                    | true, (refused, until) when refused = next && until > now ->
                        later until
                        None
                    | _ when next > now ->
                        later next
                        None
                    | _ when workflows.ContainsKey workflow -> Some next
                    | _ ->
                        report id state spec workflow next now
                        // Report again at the next due time.
                        later (ScheduleRule.next spec.Rule state.Anchor now)
                        None
            with error ->
                logger.LogError(error, "The schedule dispatcher could not compute the next due time of schedule {Schedule}.", id)
                None
        | _ -> None

    // What to fire at `now`, and how long to wait before looking again. A pass over the index runs
    // when `wakeAt` has come and a slot is free; it only finds the schedules that are due. Slots go
    // to schedules from `ready`, each looked at again at that moment, so a burst of due schedules
    // costs a few due-time computations per schedule. While every slot is taken nothing is looked
    // at: the end of a fire wakes the loop. The schedules returned are marked as firing.
    let plan (now: DateTime) =
        lock gate (fun () ->
            let due = ResizeArray<string * DateTime>()

            // After the clock steps back, the next pass would wait as long as the step.
            if wakeAt > now + min checkInterval longestWait then
                wakeAt <- now

            if parallelFires > inFlight.Count && now >= wakeAt then
                // The loop waits at most a day at a time, whatever the check interval.
                wakeAt <- now + min checkInterval longestWait

                for KeyValue(id, _) in index do
                    if not (inFlight.Contains id) && not (queued.Contains id) then
                        match evaluate now id with
                        | Some _ -> enqueue id
                        | None -> ()

            while inFlight.Count < parallelFires && ready.Count > 0 do
                let id = ready.Dequeue()
                queued.Remove id |> ignore

                match evaluate now id with
                | Some next ->
                    inFlight.Add id |> ignore
                    due.Add((id, next))
                | None -> ()

            let wait =
                if inFlight.Count >= parallelFires then longestWait
                else min longestWait (wakeAt - now)

            List.ofSeq due, wait)

    let deferTo (id: string) (due: DateTime) (until: DateTime) =
        lock gate (fun () -> deferred[id] <- (due, until))

    // Asks the schedule to fire, starts the run on the workflow and with the input from its reply,
    // and reports the start.
    // A failure or a refusal defers the due time from the moment it happens.
    let fire (id: string, due: DateTime) =
        async {
            try
                let! reply = send id (cidOf (runIdOf id due)) (ScheduleCommand.Fire due)

                match reply.EventDetails with
                | ScheduleEvent.Firing(accepted, name, input) ->
                    lock gate (fun () -> apply id reply |> ignore)

                    match workflows.TryGetValue name with
                    | _ when not (afterFiring id accepted) -> lifetime.Cancel()
                    // The index named another workflow. The Firing is folded, so the next pass
                    // reports the one it names.
                    | false, _ -> ()
                    | true, workflow ->
                        let runId = runIdOf id accepted
                        do! workflow.Start runId id accepted input
                        let! started = send id (cidOf $"{runId}/started") (ScheduleCommand.Started accepted)

                        match started.EventDetails with
                        | ScheduleEvent.Fired _ -> lock gate (fun () -> apply id started |> ignore)
                        | other ->
                            logger.LogWarning("Schedule {Schedule} answered the start of run {Run} with {Reply}.", id, runId, other)
                            deferTo id due (now () + retryDelay)
                | ScheduleEvent.Refused reason ->
                    lock gate (fun () -> refusals <- refusals + 1)

                    logger.LogDebug("Schedule {Schedule} refused a run for {Due:O}: {Reason}", id, due, reason)
                    // Refused only for being early: try again at the due time. Otherwise the index
                    // was behind, and the change it missed arrives from the projection.
                    if due - reply.CreationDate > Schedule.EarlyTolerance then
                        deferTo id due (max due (now () + Schedule.EarlyTolerance))
                    else
                        deferTo id due (now () + checkInterval)
                | other ->
                    logger.LogWarning("Schedule {Schedule} answered a fire for {Due:O} with {Reply}.", id, due, other)
                    deferTo id due (now () + retryDelay)
            with
            | _ when lifetime.IsCancellationRequested -> ()
            | :? TimeoutException as error ->
                logger.LogWarning(error, "The run of schedule {Schedule} for {Due:O} did not start in time. It is tried again.", id, due)
                deferTo id due (now () + retryDelay)
            | error ->
                logger.LogError(error, "The run of schedule {Schedule} for {Due:O} did not start. It is tried again.", id, due)
                deferTo id due (now () + retryDelay)
        }

    // Starts a fire without waiting for it. Its end frees the schedule, puts it in `ready` to find
    // its next due time, and wakes the loop.
    let launch (id: string, due: DateTime) =
        Async
            .StartAsTask(fire (id, due), cancellationToken = lifetime.Token)
            .ContinueWith(fun (_: Threading.Tasks.Task<unit>) ->
                lock gate (fun () ->
                    inFlight.Remove id |> ignore
                    enqueue id)

                signal ())
        |> ignore

    // An aggregate that stores an event before the actor system's saga starters are wired waits
    // for them, and terminates the process when they do not come within saga-start-timeout. The
    // dispatcher fires nothing until they are wired.
    let starters () =
        task {
            let rules = (FCQRS.Common.SagaStarter.Internal.startRulesOf api.System).Task

            if not rules.IsCompleted then
                // A delay takes at most about 49 days; a longer check interval would raise.
                let! first = Threading.Tasks.Task.WhenAny(rules :> Threading.Tasks.Task, Threading.Tasks.Task.Delay(min checkInterval longestWait, lifetime.Token))

                if not (obj.ReferenceEquals(first, rules)) && not lifetime.IsCancellationRequested then
                    logger.LogWarning(
                        "The schedule dispatcher fires nothing until the actor system's saga starters are wired. Call Fcqrs.wireSagaStarters, or ActorWiring.InitSagaStarterEmpty in C#, after registering every aggregate and saga."
                    )

                    let! _ = rules.WaitAsync(lifetime.Token)
                    ()
        }

    // An event the projection read, from the projection's handler or a test.
    let observe (id: string) (event: Event<ScheduleEvent>) =
        lock gate (fun () ->
            if holding then
                held.Enqueue(id, event)
            else
                observed id event)

        signal ()

    let parameter (command: Data.Common.DbCommand) (name: string) (value: string) =
        let parameter = command.CreateParameter()
        parameter.ParameterName <- name
        parameter.DbType <- Data.DbType.String
        parameter.Value <- value
        command.Parameters.Add parameter |> ignore

    let rowCommand (connection: Data.Common.DbConnection) (transaction: Data.Common.DbTransaction) (sql: string) (id: string) =
        let command = connection.CreateCommand()
        command.Transaction <- transaction
        command.CommandText <- sql
        parameter command "@id" id
        command

    let decodeRow (id: string) (entry: string) =
        try
            ScheduleIndex.decode entry
        with error ->
            raise (FormatException($"The row of schedule '{id}' in {ScheduleIndex.Table} cannot be read: {error.Message}", error))

    // The projection's handler. It folds the event into the schedule's stored row, writes or deletes
    // the row in the transaction that stores the read position, and folds the event into the index
    // in memory. When the transaction then fails, the projection hands the event over again, and
    // folding an event a second time changes nothing. The projection outlives the loop, which a test
    // stops as a crash would, so its database work does not take the loop's cancellation.
    let indexEvent (connection: Data.Common.DbConnection) (transaction: Data.Common.DbTransaction) (envelope: Akka.Persistence.Query.EventEnvelope) : Threading.Tasks.Task =
        task {
            match envelope.Event with
            | :? Event<ScheduleEvent> as event ->
                match event.Sender with
                | Some sender ->
                    let id = string sender
                    let token = Threading.CancellationToken.None

                    let! stored =
                        task {
                            use select = rowCommand connection transaction $"SELECT entry FROM {table} WHERE schedule_id = @id" id

                            match! select.ExecuteScalarAsync token with
                            | :? string as entry -> return Some(decodeRow id entry)
                            | _ -> return None
                        }

                    match ScheduleIndex.advance (defaultArg stored Schedule.initial) event, stored with
                    | Some next, Some state when next = state -> ()
                    | Some next, _ ->
                        use upsert =
                            rowCommand
                                connection
                                transaction
                                $"INSERT INTO {table} (schedule_id, entry) VALUES (@id, @entry) ON CONFLICT (schedule_id) DO UPDATE SET entry = excluded.entry"
                                id

                        parameter upsert "@entry" (ScheduleIndex.encode next)
                        let! _ = upsert.ExecuteNonQueryAsync token
                        ()
                    | None, Some _ ->
                        use delete = rowCommand connection transaction $"DELETE FROM {table} WHERE schedule_id = @id" id
                        let! _ = delete.ExecuteNonQueryAsync token
                        ()
                    | None, None -> ()

                    observe id event
                | None -> ()
            | _ -> ()
        }

    // Creates the table on first use, binds the projection's read positions to this journal, and
    // reads every row into the index in memory.
    let load (token: Threading.CancellationToken) =
        task {
            do! store.InitializeAsync token
            do! store.CreateOwnedTableAsync(ScheduleIndex.Table, ScheduleIndex.Columns, token)
            // The table and the read positions live in the journal's database and move with it, so a
            // changed connection string still names the journal they were read from.
            do! store.RebindProjectionAsync(ScheduleIndex.ProjectionName, token)
            use! connection = store.OpenProjectionAsync token
            use select = connection.CreateCommand()
            select.CommandText <- $"SELECT schedule_id, entry FROM {table}"
            use! reader = select.ExecuteReaderAsync token
            let rows = ResizeArray<string * ScheduleState>()
            let mutable reading = true

            while reading do
                let! available = reader.ReadAsync token

                if available then
                    let id = reader.GetString 0
                    rows.Add((id, decodeRow id (reader.GetString 1)))
                else
                    reading <- false

            lock gate (fun () ->
                for id, state in rows do
                    index[id] <- state)
        }

    // Waits a second, unless the dispatcher stops first.
    let pause () =
        task {
            try
                do! Threading.Tasks.Task.Delay(1000, lifetime.Token)
            with :? OperationCanceledException ->
                ()
        }

    let loop () : Threading.Tasks.Task =
        task {
            let mutable loading = true

            while loading && not lifetime.IsCancellationRequested do
                try
                    do! load lifetime.Token
                    loading <- false
                with
                | _ when lifetime.IsCancellationRequested -> ()
                | error ->
                    logger.LogError(error, "The schedule dispatcher could not read its index from {Table}. No schedule fires until it has. It tries again in a second.", ScheduleIndex.Table)
                    do! pause ()

            if not lifetime.IsCancellationRequested then
                // Started after the rows are in memory: the projection folds each event into the
                // index from the read position stored with its row.
                let options = FCQRS.Projections.TransactionalProjectionOptions(ScheduleIndex.ProjectionName, store)
                options.Entities <- [ Schedule.EntityName ]
                let projection = FCQRS.Projections.startOwned api options indexEvent
                indexProjection <- Some projection

                projection.Completion.ContinueWith(fun (completion: Threading.Tasks.Task) ->
                    if completion.IsFaulted then
                        logger.LogCritical(completion.Exception, "The schedule index stopped. Schedules fire from what it had read until the process restarts."))
                |> ignore

                // A run decided before a restart is found once the index has read the events stored
                // since its rows were written. No schedule fires before that.
                let mutable behind = true

                while behind && not lifetime.IsCancellationRequested do
                    try
                        do! projection.CatchUpAsync(lifetime.Token)
                        behind <- false
                    with
                    | _ when lifetime.IsCancellationRequested -> ()
                    | _ when projection.Completion.IsCompleted -> behind <- false
                    | error ->
                        logger.LogWarning(error, "The schedule index has not caught up with the journal. No schedule fires until it has.")
                        do! pause ()

            caughtUp.TrySetResult() |> ignore

            let mutable waiting = true

            while waiting && not lifetime.IsCancellationRequested do
                let mutable failed = false

                try
                    do! starters ()
                    waiting <- false
                with
                | :? OperationCanceledException -> waiting <- false
                | error ->
                    logger.LogError(error, "The schedule dispatcher could not wait for the saga starters. It tries again in a second.")
                    failed <- true

                if failed then
                    try
                        do! Threading.Tasks.Task.Delay(1000, lifetime.Token)
                    with :? OperationCanceledException ->
                        ()

            while not lifetime.IsCancellationRequested do
                let mutable failed = false

                try
                    let due, wait = plan (now ())

                    for fire in due do
                        launch fire

                    let milliseconds = Math.Ceiling(max 0.0 wait.TotalMilliseconds)
                    let! _ = wake.WaitAsync(TimeSpan.FromMilliseconds milliseconds, lifetime.Token)
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

    /// Folds an event into the index in memory only, as the projection does after writing its row.
    member _.Observe(message: obj) : unit =
        match message with
        | :? Event<ScheduleEvent> as event ->
            match event.Sender with
            | Some sender -> observe (string sender) event
            | None -> ()
        | _ -> ()

    /// Starts the loop, which reads the stored index, starts its projection, and waits for it to
    /// catch up before it fires anything. It ends when the actor system shuts down.
    member _.Start() =
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

        // The loop logs and survives its own errors. Anything that still ends it is logged here, so
        // the dispatcher never stops without a word.
        Threading.Tasks.Task
            .Run(fun () -> loop ())
            .ContinueWith(fun (ended: Threading.Tasks.Task) ->
                if ended.IsFaulted then
                    logger.LogCritical(ended.Exception, "The schedule dispatcher stopped. No schedule fires until the process restarts."))
        |> ignore

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

    /// Test hook: how many due times the dispatcher has computed with `Schedule.allowedDue`.
    member _.Computations = lock gate (fun () -> computations)

    /// Test hook: completes once the index has caught up with the journal, before the first plan.
    member _.Ready: Threading.Tasks.Task = caughtUp.Task

    /// Test hook: the projection that keeps the index, once it runs.
    member _.Index: FCQRS.Projections.IProjection option = indexProjection

/// The registered schedule dispatcher. Pass it to the functions of `Schedules`.
[<Sealed>]
type ScheduleHandle
    internal
    (
        send: string -> FCQRS.Model.Data.CID -> ScheduleCommand -> Async<Event<ScheduleEvent>>,
        workflows: Collections.Generic.IReadOnlyDictionary<string, ScheduledWorkflow>,
        dispatcher: Dispatcher,
        startersWired: unit -> bool
    ) =
    member internal _.Send = send
    member internal _.Workflows = workflows
    member internal _.Dispatcher = dispatcher
    member internal _.StartersWired = startersWired

/// Schedules that start a registered workflow at each due time. A schedule is created, changed,
/// paused, resumed and deleted while the application runs, and is stored in the journal as an
/// aggregate named "fcqrs-schedule". The run for a due time is a workflow instance whose ID is the
/// schedule's ID, "@", and the due time in UTC in ISO 8601 ("O") form. A run starts at least once
/// per accepted due time, and a repeated start with its ID stores nothing.
module Schedules =

    open FCQRS.FSharp

    let private checkIntervalKey = "akka.fcqrs.schedule-check-interval"

    /// The longest the dispatcher sleeps before it reads its index again while fewer than 16 starts
    /// are running: one minute unless the configuration says otherwise.
    let private checkIntervalOf (config: Akka.Configuration.Config) : TimeSpan =
        if config.HasPath checkIntervalKey then
            // A bare number is seconds, as for `akka.fcqrs.command-timeout`.
            let interval =
                match config.GetString checkIntervalKey |> Int32.TryParse with
                | true, seconds -> TimeSpan.FromSeconds(float seconds)
                | _ -> config.GetTimeSpan(checkIntervalKey, Nullable(TimeSpan.FromMinutes 1.0))

            if interval <= TimeSpan.Zero then
                raise (
                    Akka.Configuration.ConfigurationException(
                        $"{checkIntervalKey} must be positive; it is '{config.GetString checkIntervalKey}'."
                    )
                )

            interval
        else
            TimeSpan.FromMinutes 1.0

    let private registered =
        Runtime.CompilerServices.ConditionalWeakTable<Akka.Actor.ActorSystem, obj>()

    /// The longest schedule ID, in characters: a run ID adds "@" and 28 characters of due time, the
    /// correlation IDs of a run's commands add up to 8 more, and a correlation ID holds at most 255.
    /// The persistence IDs must fit too (see `MaxPersistenceIdLength`), and Akka escapes their
    /// parts as `Uri.EscapeDataString` does, so `create` and `change` also require the escaped
    /// workflow name and the escaped schedule ID to be at most 205 characters together. A
    /// non-ASCII character escapes to 6 to 12 characters.
    [<Literal>]
    let MaxIdLength = 200

    /// The longest persistence ID: 255 characters, the size of the journal's column on PostgreSQL.
    /// A run journals as "<escaped workflow name>/default-shard/<escaped run ID>", which is 50
    /// characters besides the escaped workflow name and schedule ID, and a schedule as
    /// "fcqrs-schedule/default-shard/<escaped schedule ID>".
    [<Literal>]
    let MaxPersistenceIdLength = 255

    // A persistence ID's parts as Akka escapes them: the sharding region's name and the entity's ID.
    let private persistenceIdLength (entity: string) (id: string) =
        Uri.EscapeDataString(entity).Length + 1 + DEFAULT_SHARD.Length + 1 + Uri.EscapeDataString(id).Length

    // Every run ID of a schedule has this length, because the due time has a fixed format.
    let private sampleRunId (id: string) =
        $"""{id}@{DateTime.SpecifyKind(DateTime.UnixEpoch, DateTimeKind.Utc).ToString "O"}"""

    let private checkId (id: string) =
        if String.IsNullOrWhiteSpace id then
            invalidArg (nameof id) "A schedule ID must not be blank."

        if id.Contains '~' then
            invalidArg (nameof id) $"Schedule ID '{id}' contains '~', which a workflow ID cannot contain."

        if id.Length > MaxIdLength then
            invalidArg (nameof id) $"A schedule ID must be at most {MaxIdLength} characters."

        let length =
            try
                persistenceIdLength Schedule.EntityName id
            with :? UriFormatException as error ->
                invalidArg (nameof id) $"Schedule ID '{id}' cannot be escaped: {error.Message}"

        if length > MaxPersistenceIdLength then
            invalidArg (nameof id) $"Schedule ID '{id}' escapes to a persistence ID of {length} characters; the most is {MaxPersistenceIdLength}."

    // A run's persistence ID holds the escaped workflow name and the escaped run ID.
    let private checkRunId (id: string) (workflow: string) =
        let length =
            try
                persistenceIdLength workflow (sampleRunId id)
            with :? UriFormatException as error ->
                invalidArg (nameof workflow) $"Workflow '{workflow}' cannot be escaped: {error.Message}"

        if length > MaxPersistenceIdLength then
            invalidArg
                (nameof id)
                $"Schedule ID '{id}' with workflow '{workflow}' gives its runs a persistence ID of {length} characters; the most is {MaxPersistenceIdLength}. The escaped workflow name and schedule ID must be at most {MaxPersistenceIdLength - 50} characters together."

    /// Registers the schedule aggregate and starts this actor system's dispatcher, which may start
    /// the listed workflows. Register each workflow with `Workflows.register` first. Raises
    /// `InvalidOperationException` when the actor system has a dispatcher already, and
    /// `Akka.Configuration.ConfigurationException` when `akka.fcqrs.schedule-check-interval` is not
    /// positive. A schedule whose workflow is not listed starts no run, and the dispatcher logs an
    /// error at each of its due times, until a later start lists the workflow again.
    ///
    /// The schedule aggregate, like every aggregate, waits for the actor system's saga starters
    /// before it stores an event, and terminates the process when they are not wired within
    /// `akka.fcqrs.saga-start-timeout`. Wire them with `Fcqrs.wireSagaStarters` after registering
    /// every aggregate and saga, this one included. Until they are wired the dispatcher fires
    /// nothing, and every function of this module that sends a command raises
    /// `InvalidOperationException`.
    ///
    /// The dispatcher keeps its index in the table `fcqrs_schedule_index`, which it creates on first
    /// use in the journal's database and schema, with one row per schedule. A start reads the rows
    /// and the schedule events stored since, so it does not read the schedules' whole history again.
    /// The first start on a journal without the table reads that history once and fills the table.
    let register (api: IActor) (runs: ScheduledWorkflow list) : ScheduleHandle =
        let checkInterval = checkIntervalOf api.System.Settings.Config
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
        let dispatcher = Dispatcher(api, send, workflows, checkInterval, logger)
        dispatcher.Start()
        let rules = SagaStarter.Internal.startRulesOf api.System
        ScheduleHandle(send, workflows, dispatcher, fun () -> rules.Task.IsCompleted)

    // Sent only once the saga starters are wired: before that, the aggregate would wait for them
    // and terminate the process when they never come.
    let private run (handle: ScheduleHandle) (id: string) (command: ScheduleCommand) =
        async {
            if not (handle.StartersWired()) then
                invalidOp
                    $"Schedule '{id}': the actor system's saga starters are not wired, and an aggregate stores no event until they are. Call Fcqrs.wireSagaStarters (ActorWiring.InitSagaStarterEmpty in C#; AddFcqrs wires them when the host starts) after registering every aggregate and saga."

            let! reply = handle.Send id (Fcqrs.newCid ()) command

            match reply.EventDetails with
            | ScheduleEvent.Refused reason -> return invalidOp $"Schedule '{id}': {reason}"
            | _ -> return ()
        }

    // A weekly rule's days in order and without repeats, so the same days are the same content.
    let private normalize (rule: ScheduleRule) =
        match rule with
        | ScheduleRule.Weekly(days, time, zone) -> ScheduleRule.Weekly(days |> List.distinct |> List.sort, time, zone)
        | rule -> rule

    // Checks the arguments before anything is sent, so a caller's mistake raises ArgumentException
    // here instead of reaching the aggregate.
    let private specOf (handle: ScheduleHandle) (id: string) (workflow: string) (input: 'Input) (rule: ScheduleRule) (catchUp: CatchUp) : ScheduleSpec =
        checkId id

        if isNull (box rule) then
            nullArg (nameof rule)

        if isNull (box catchUp) then
            nullArg (nameof catchUp)

        match ScheduleRule.validate rule with
        | Error reason -> invalidArg (nameof rule) reason
        | Ok() -> ()

        match handle.Workflows.TryGetValue workflow with
        | true, registered when registered.InputType = typeof<'Input> ->
            checkRunId id workflow

            { Workflow = workflow
              Input = Payload.ofValue input
              Rule = normalize rule
              CatchUp = catchUp }
        | true, registered ->
            invalidOp $"Workflow '{workflow}' takes a {registered.InputType.Name} as its schedule input, not a {typeof<'Input>.Name}."
        | _ -> invalidOp $"Workflow '{workflow}' is not registered with the schedule dispatcher."

    /// Creates the schedule `id`, which starts the workflow named `workflow` with `input` at each
    /// due time of `rule`. Returns once the schedule is stored. Raises `ArgumentException` for a rule
    /// that `ScheduleRule.validate` rejects, a missing rule or policy, or an ID that is blank,
    /// contains '~', or is too long (see `MaxIdLength`). Raises `InvalidOperationException` when
    /// the saga starters are not wired, when the workflow is not registered with the dispatcher or
    /// takes another input, and when the schedule refuses: a schedule with this ID exists with a
    /// different content, or was deleted. A repeat with the same content is accepted and stores
    /// nothing; a weekly rule's days are sorted and their repeats removed before the comparison.
    let create (handle: ScheduleHandle) (id: string) (workflow: string) (input: 'Input) (rule: ScheduleRule) (catchUp: CatchUp) : Async<unit> =
        async {
            let spec = specOf handle id workflow input rule catchUp
            return! run handle id (ScheduleCommand.Create spec)
        }

    /// Replaces the schedule's workflow, input, rule and catch-up policy. An `Every` rule counts from
    /// the change, and no due time at or before it runs. A run decided before the change starts the
    /// workflow, with the input, it was decided with. Raises `ArgumentException` and
    /// `InvalidOperationException` as `create` does, and `InvalidOperationException` when the
    /// schedule does not exist or was deleted.
    let change (handle: ScheduleHandle) (id: string) (workflow: string) (input: 'Input) (rule: ScheduleRule) (catchUp: CatchUp) : Async<unit> =
        async {
            let spec = specOf handle id workflow input rule catchUp
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

    /// The schedule as the dispatcher's index knows it, or nothing when the index has not read it. A
    /// deleted schedule leaves the index once no run decided before the delete waits to start.
    /// The index lags the schedule by one projection pass, so a change that just returned may not
    /// show yet.
    let state (handle: ScheduleHandle) (id: string) : ScheduleView option = handle.Dispatcher.View id
