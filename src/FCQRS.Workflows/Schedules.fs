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
    /// Start one run for every missed due time, oldest first.
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

    /// The due times in `(lastDue, now]` that the policy runs, oldest first. All times are UTC.
    let missed (rule: ScheduleRule) (anchor: DateTime) (lastDue: DateTime) (now: DateTime) (policy: CatchUp) : DateTime list =
        match policy with
        | CatchUp.None -> []
        | _ ->
            let all =
                lastDue
                |> List.unfold (fun after ->
                    let due = next rule anchor after
                    if due <= now then Some(due, due) else None)
            match policy with
            | CatchUp.All -> all
            | _ -> all |> List.tryLast |> Option.toList

    /// Checks that a rule can produce due times: a known time zone, at least one day, and a positive
    /// interval.
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
        | ScheduleRule.Every interval when interval <= TimeSpan.Zero -> Error "The interval must be positive."
        | ScheduleRule.Every _ -> Ok()
