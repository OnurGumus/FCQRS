module ScheduleRuleTests

// The due-time calculator, tested without an actor system.

open System
open Expecto
open FCQRS.Serialization
open FCQRS.Workflows

let private utc y m d h mi = DateTime(y, m, d, h, mi, 0, DateTimeKind.Utc)

let private oslo = "Europe/Oslo"

let tests =
    testList "schedule rule" [
        testCase "schedule rule: daily 09:00 in Oslo after 10:00Z in winter gives 08:00Z the next day" <| fun _ ->
            let rule = ScheduleRule.Daily(TimeOnly(9, 0), oslo)
            let anchor = utc 2026 11 1 0 0
            Expect.equal (ScheduleRule.next rule anchor (utc 2026 11 14 10 0)) (utc 2026 11 15 8 0) "next day, CET"

        testCase "schedule rule: a result is strictly after the given time" <| fun _ ->
            let rule = ScheduleRule.Daily(TimeOnly(9, 0), "UTC")
            let anchor = utc 2026 11 1 0 0
            Expect.equal (ScheduleRule.next rule anchor (utc 2026 11 14 9 0)) (utc 2026 11 15 9 0) "strict"

        testCase "schedule rule: a local time skipped by spring forward runs at the next valid local time" <| fun _ ->
            let rule = ScheduleRule.Daily(TimeOnly(2, 30), oslo)
            let anchor = utc 2027 3 1 0 0
            Expect.equal (ScheduleRule.next rule anchor (utc 2027 3 27 12 0)) (utc 2027 3 28 1 0) "03:00 CEST is 01:00Z"

        testCase "schedule rule: a local time repeated by fall back runs once, at the first occurrence" <| fun _ ->
            let rule = ScheduleRule.Daily(TimeOnly(2, 30), oslo)
            let anchor = utc 2026 10 1 0 0
            let first = ScheduleRule.next rule anchor (utc 2026 10 24 12 0)
            Expect.equal first (utc 2026 10 25 0 30) "02:30 CEST is 00:30Z"
            let second = ScheduleRule.next rule anchor first
            Expect.equal second (utc 2026 10 26 1 30) "next 02:30 is CET, 01:30Z the day after"

        testCase "schedule rule: weekly runs on the listed days" <| fun _ ->
            let rule = ScheduleRule.Weekly([ DayOfWeek.Monday; DayOfWeek.Thursday ], TimeOnly(8, 0), "UTC")
            let anchor = utc 2026 11 1 0 0
            let monday = ScheduleRule.next rule anchor (utc 2026 11 14 12 0)
            Expect.equal monday (utc 2026 11 16 8 0) "Saturday to Monday"
            Expect.equal (ScheduleRule.next rule anchor monday) (utc 2026 11 19 8 0) "Monday to Thursday"

        testCase "schedule rule: every counts from the anchor" <| fun _ ->
            let rule = ScheduleRule.Every(TimeSpan.FromMinutes 90.0)
            let anchor = utc 2026 11 14 10 0
            Expect.equal (ScheduleRule.next rule anchor anchor) (utc 2026 11 14 11 30) "first"
            Expect.equal (ScheduleRule.next rule anchor (utc 2026 11 14 10 30)) (utc 2026 11 14 11 30) "within the first interval"
            Expect.equal (ScheduleRule.next rule anchor (utc 2026 11 14 11 30)) (utc 2026 11 14 13 0) "second"

        testCase "schedule rule: missed follows the catch-up policy" <| fun _ ->
            let rule = ScheduleRule.Daily(TimeOnly(9, 0), "UTC")
            let anchor = utc 2026 11 1 0 0
            let lastDue = utc 2026 11 10 9 0
            let now = utc 2026 11 13 9 30
            let all = [ utc 2026 11 11 9 0; utc 2026 11 12 9 0; utc 2026 11 13 9 0 ]
            Expect.equal (ScheduleRule.missed rule anchor lastDue now CatchUp.All) all "all, oldest first"
            Expect.equal (ScheduleRule.missed rule anchor lastDue now CatchUp.Latest) [ utc 2026 11 13 9 0 ] "latest"
            Expect.equal (ScheduleRule.missed rule anchor lastDue now CatchUp.None) [] "none"
            Expect.equal (ScheduleRule.missed rule anchor lastDue (utc 2026 11 10 12 0) CatchUp.Latest) [] "nothing missed"

        testCase "schedule rule: Latest over a long gap of a short interval returns one due time quickly" <| fun _ ->
            let rule = ScheduleRule.Every(TimeSpan.FromSeconds 1.0)
            let anchor = utc 2026 10 1 0 0
            let now = utc 2026 11 14 0 0
            let sw = Diagnostics.Stopwatch.StartNew()
            let result = ScheduleRule.missed rule anchor (utc 2026 10 15 0 0) now CatchUp.Latest
            Expect.equal result [ now ] "the latest due time"
            Expect.isLessThan sw.ElapsedMilliseconds 1000L "fast"

        testCase "schedule rule: All over a long gap returns the 1000 most recent, oldest first" <| fun _ ->
            let rule = ScheduleRule.Every(TimeSpan.FromSeconds 1.0)
            let anchor = utc 2026 10 1 0 0
            let now = utc 2026 11 14 0 0
            let result = ScheduleRule.missed rule anchor (utc 2026 10 15 0 0) now CatchUp.All
            Expect.hasLength result 1000 "bounded"
            Expect.equal (List.last result) now "ends at the latest"
            Expect.equal result.Head (now.AddSeconds -999.0) "oldest of the window"
            Expect.equal result (List.sort result) "ascending"

        testCase "schedule rule: All over a long gap of a daily rule is bounded too" <| fun _ ->
            let rule = ScheduleRule.Daily(TimeOnly(9, 0), "UTC")
            let result = ScheduleRule.missed rule (utc 2020 1 1 0 0) (utc 2020 1 1 9 0) (utc 2026 11 14 12 0) CatchUp.All
            Expect.hasLength result 1000 "bounded"
            Expect.equal (List.last result) (utc 2026 11 14 9 0) "ends at the latest"

        testCase "schedule rule: validate rejects an unknown zone, no days and a non-positive interval" <| fun _ ->
            Expect.isError (ScheduleRule.validate (ScheduleRule.Daily(TimeOnly(9, 0), "Mars/Base"))) "zone"
            Expect.isError (ScheduleRule.validate (ScheduleRule.Weekly([], TimeOnly(9, 0), "UTC"))) "days"
            Expect.isError (ScheduleRule.validate (ScheduleRule.Every TimeSpan.Zero)) "zero"
            Expect.isError (ScheduleRule.validate (ScheduleRule.Every(TimeSpan.FromMinutes -1.0))) "negative"
            Expect.isError (ScheduleRule.validate (ScheduleRule.Every(TimeSpan.FromMilliseconds 500.0))) "below one second"
            Expect.isOk (ScheduleRule.validate (ScheduleRule.Every(TimeSpan.FromSeconds 1.0))) "one second"
            Expect.isOk (ScheduleRule.validate (ScheduleRule.Daily(TimeOnly(9, 0), oslo))) "valid"

        testCase "schedule rule: validate returns an error, never an exception, for any rule" <| fun _ ->
            let nine = TimeOnly(9, 0)
            // On macOS a zone ID that names a directory of the zone database throws SecurityException.
            for zone in [ "Europe"; "America"; "Etc"; "Europe/"; "America/Argentina"; ""; " "; Unchecked.defaultof<string> ] do
                Expect.isError (ScheduleRule.validate (ScheduleRule.Daily(nine, zone))) $"daily, zone '{zone}'"
                Expect.isError (ScheduleRule.validate (ScheduleRule.Weekly([ DayOfWeek.Monday ], nine, zone))) $"weekly, zone '{zone}'"
            Expect.isError (ScheduleRule.validate (ScheduleRule.Weekly(Unchecked.defaultof<DayOfWeek list>, nine, "UTC"))) "no day list"
            Expect.isError (ScheduleRule.validate (ScheduleRule.Weekly([ enum<DayOfWeek> 9 ], nine, "UTC"))) "a day that does not exist"
            Expect.isError (ScheduleRule.validate (ScheduleRule.Every TimeSpan.MaxValue)) "the largest interval"
            Expect.isError (ScheduleRule.validate (ScheduleRule.Every(TimeSpan.FromDays 366.0 + TimeSpan.FromTicks 1L))) "over 366 days"
            Expect.isOk (ScheduleRule.validate (ScheduleRule.Every(TimeSpan.FromDays 366.0))) "366 days"
            Expect.isError (ScheduleRule.validate Unchecked.defaultof<ScheduleRule>) "no rule"

        testCase "schedule rule: a rule that validates computes due times without an exception" <| fun _ ->
            let anchor = utc 2026 11 14 10 0
            let longest = ScheduleRule.Every(TimeSpan.FromDays 366.0)
            Expect.equal (ScheduleRule.next longest anchor anchor) (anchor.AddDays 366.0) "the longest interval"
            let now = utc 2036 11 14 10 0
            Expect.equal (ScheduleRule.missed longest anchor anchor now CatchUp.All |> List.length) 9 "ten years of the longest interval"

        testCase "schedule rule: rules and policies survive the journal serializer" <| fun _ ->
            let serializer = Payload.ofValue
            for rule in [ ScheduleRule.Daily(TimeOnly(9, 0), oslo)
                          ScheduleRule.Weekly([ DayOfWeek.Monday ], TimeOnly(8, 15), "UTC")
                          ScheduleRule.Every(TimeSpan.FromMinutes 90.0) ] do
                Expect.equal (Payload.toValue<ScheduleRule> (serializer rule)) rule "rule"
            for policy in [ CatchUp.Latest; CatchUp.All; CatchUp.None ] do
                Expect.equal (Payload.toValue<CatchUp> (serializer policy)) policy "policy"
    ]

// The schedule aggregate's decide and fold, tested without an actor system.

open FCQRS.Common
open FCQRS.FSharp
open FCQRS.Model.Data

// What a command produced: a stored event or a reply that is not stored.
type private Reply =
    | Stored of ScheduleEvent
    | Answered of ScheduleEvent

let private messageId () = Guid.CreateVersion7().ToString() |> ValueLens.CreateAsResult |> Result.value

let private command (now: DateTime) details : Command<ScheduleCommand> =
    { CommandDetails = details
      CreationDate = now
      Id = messageId ()
      Sender = Option.None
      CorrelationId = Fcqrs.newCid ()
      Metadata = Map.empty }

let private envelope (now: DateTime) details : Event<ScheduleEvent> =
    { EventDetails = details
      CreationDate = now
      Id = messageId ()
      Sender = Option.None
      CorrelationId = Fcqrs.newCid ()
      Version = Version.Zero
      Metadata = Map.empty }

// Runs a command as the aggregate actor does: a stored or a deferred event is folded.
let private run (now: DateTime) details state =
    match Schedule.decide (command now details) state with
    | PersistEvent event -> Stored event, Schedule.fold (envelope now event) state
    | DeferEvent event -> Answered event, Schedule.fold (envelope now event) state
    | other -> failwithf "Unexpected action %A" other

// Runs commands that must be stored, and returns the state after them.
let private given (steps: (DateTime * ScheduleCommand) list) =
    steps
    |> List.fold
        (fun state (now, details) ->
            match run now details state with
            | Stored _, next -> next
            | reply, _ -> failwithf "Setup command %A at %O was not stored: %A" details now reply)
        Schedule.initial

let private isRefused reply =
    match reply with
    | Answered(ScheduleEvent.Refused _) -> true
    | _ -> false

// Expects a refusal that leaves the state unchanged.
let private refused now details state message =
    let reply, next = run now details state
    Expect.isTrue (isRefused reply) $"{message}: refused, got {reply}"
    Expect.equal next state $"{message}: state unchanged"

let private hello = Payload.ofValue "hello"

let private spec rule policy =
    { Workflow = "remind"
      Input = hello
      Rule = rule
      CatchUp = policy }

let private daily = ScheduleRule.Daily(TimeOnly(9, 0), "UTC")

let private nine day = utc 2026 11 day 9 0

// A daily 09:00 UTC schedule created at 08:00 on 1 November.
let private created policy = [ utc 2026 11 1 8 0, ScheduleCommand.Create(spec daily policy) ]

let aggregateTests =
    testList "schedule aggregate" [
        testCase "schedule aggregate: the entity name is fcqrs-schedule" <| fun _ ->
            Expect.equal Schedule.EntityName "fcqrs-schedule" "a persistent contract"

        testCase "schedule aggregate: create stores the spec, with its time as the anchor" <| fun _ ->
            let s = spec daily CatchUp.Latest
            let at = utc 2026 11 1 8 0
            let reply, state = run at (ScheduleCommand.Create s) Schedule.initial
            Expect.equal reply (Stored(ScheduleEvent.Created(s, at))) "stored"
            Expect.equal state.Spec (Some s) "spec"
            Expect.equal state.Anchor at "anchor"

        testCase "schedule aggregate: a repeated create gets the first verdict and changes nothing" <| fun _ ->
            let s = spec daily CatchUp.Latest
            let state = given (created CatchUp.Latest)
            let reply, next = run (utc 2026 11 1 8 5) (ScheduleCommand.Create s) state
            Expect.equal reply (Answered(ScheduleEvent.Created(s, utc 2026 11 1 8 0))) "first verdict"
            Expect.equal next state "unchanged"

        testCase "schedule aggregate: a create with different content for an existing ID is refused" <| fun _ ->
            let state = given (created CatchUp.Latest)
            refused (utc 2026 11 1 8 5) (ScheduleCommand.Create(spec daily CatchUp.All)) state "different policy"

        testCase "schedule aggregate: a create with an invalid rule is refused" <| fun _ ->
            let bad = spec (ScheduleRule.Daily(TimeOnly(9, 0), "Mars/Base")) CatchUp.Latest
            refused (utc 2026 11 1 8 0) (ScheduleCommand.Create bad) Schedule.initial "unknown zone"

        // The aggregate actor treats an exception from decide as fatal, so a rule from a caller must
        // be refused, never raised.
        testCase "schedule aggregate: a create or change with a rule that cannot produce due times is refused, never raised" <| fun _ ->
            let at = TimeOnly(9, 0)
            let rules =
                [ ScheduleRule.Daily(at, "Europe")
                  ScheduleRule.Daily(at, Unchecked.defaultof<string>)
                  ScheduleRule.Weekly(Unchecked.defaultof<DayOfWeek list>, at, "UTC")
                  ScheduleRule.Weekly([ enum<DayOfWeek> 9 ], at, "UTC")
                  ScheduleRule.Every TimeSpan.MaxValue
                  Unchecked.defaultof<ScheduleRule> ]
            let existing = given (created CatchUp.Latest)
            for rule in rules do
                refused (utc 2026 11 1 8 0) (ScheduleCommand.Create(spec rule CatchUp.Latest)) Schedule.initial $"create with {rule}"
                refused (utc 2026 11 1 8 30) (ScheduleCommand.Change(spec rule CatchUp.Latest)) existing $"change to {rule}"
            let noPolicy = spec daily Unchecked.defaultof<CatchUp>
            refused (utc 2026 11 1 8 0) (ScheduleCommand.Create noPolicy) Schedule.initial "create with no catch-up policy"
            refused (utc 2026 11 1 8 30) (ScheduleCommand.Change noPolicy) existing "change to no catch-up policy"

        testCase "schedule aggregate: a fire whose due time cannot be computed is refused, never raised" <| fun _ ->
            let state = given (created CatchUp.Latest)
            for rule in [ ScheduleRule.Daily(TimeOnly(9, 0), "Europe"); ScheduleRule.Every TimeSpan.MaxValue ] do
                // A stored rule that a later runtime cannot compute, such as a zone a system update removed.
                let broken = { state with Spec = state.Spec |> Option.map (fun current -> { current with Rule = rule }) }
                refused (nine 1) (ScheduleCommand.Fire(nine 1)) broken $"{rule}"

        testCase "schedule aggregate: commands for a schedule that does not exist are refused" <| fun _ ->
            let now = utc 2026 11 1 9 0
            for details in [ ScheduleCommand.Change(spec daily CatchUp.Latest)
                             ScheduleCommand.Pause
                             ScheduleCommand.Resume
                             ScheduleCommand.Delete
                             ScheduleCommand.Fire(nine 1)
                             ScheduleCommand.Started(nine 1) ] do
                refused now details Schedule.initial $"{details}"

        testCase "schedule aggregate: the first fire is the next due time after the creation, with the stored input" <| fun _ ->
            let state = given (created CatchUp.Latest)
            let reply, next = run (utc 2026 11 1 9 0) (ScheduleCommand.Fire(nine 1)) state
            Expect.equal reply (Stored(ScheduleEvent.Firing(nine 1, "remind", hello))) "firing"
            Expect.equal next.Last (Some { Due = nine 1; Workflow = "remind"; Input = hello; Started = false }) "pending"

        testCase "schedule aggregate: a fire for a due time other than the next allowed one is refused" <| fun _ ->
            let state = given (created CatchUp.Latest)
            let now = utc 2026 11 1 9 0
            refused now (ScheduleCommand.Fire(nine 2)) state "the due time after the next"
            refused now (ScheduleCommand.Fire(utc 2026 11 1 9 30)) state "not a due time of the rule"
            refused now (ScheduleCommand.Fire(utc 2026 10 31 9 0)) state "before the creation"
            let state =
                given (created CatchUp.Latest @ [ now, ScheduleCommand.Fire(nine 1); now, ScheduleCommand.Started(nine 1) ])
            refused (nine 2) (ScheduleCommand.Fire(nine 3)) state "skips the next one"

        testCase "schedule aggregate: a fire while paused is refused" <| fun _ ->
            let state = given (created CatchUp.Latest @ [ utc 2026 11 1 8 30, ScheduleCommand.Pause ])
            refused (nine 1) (ScheduleCommand.Fire(nine 1)) state "paused"

        testCase "schedule aggregate: a fire while an earlier firing waits for its fired is refused" <| fun _ ->
            let state = given (created CatchUp.Latest @ [ nine 1, ScheduleCommand.Fire(nine 1) ])
            refused (nine 2) (ScheduleCommand.Fire(nine 2)) state "waiting"
            let reply, state = run (nine 2) (ScheduleCommand.Started(nine 1)) state
            Expect.equal reply (Stored(ScheduleEvent.Fired(nine 1))) "fired"
            Expect.equal state.Last (Some { Due = nine 1; Workflow = "remind"; Input = hello; Started = true }) "started"
            let reply, _ = run (nine 2) (ScheduleCommand.Fire(nine 2)) state
            Expect.equal reply (Stored(ScheduleEvent.Firing(nine 2, "remind", hello))) "the next one"

        testCase "schedule aggregate: a repeated fire gets the first verdict, with the first workflow and input, after a change" <| fun _ ->
            let changed = { spec daily CatchUp.Latest with Workflow = "other"; Input = Payload.ofValue "bye" }
            let state =
                given (created CatchUp.Latest @ [ nine 1, ScheduleCommand.Fire(nine 1)
                                                  utc 2026 11 1 9 10, ScheduleCommand.Change changed ])
            let reply, next = run (utc 2026 11 1 9 20) (ScheduleCommand.Fire(nine 1)) state
            Expect.equal reply (Answered(ScheduleEvent.Firing(nine 1, "remind", hello))) "first verdict while waiting"
            Expect.equal next state "unchanged while waiting"
            let state = given (created CatchUp.Latest @ [ nine 1, ScheduleCommand.Fire(nine 1); nine 1, ScheduleCommand.Started(nine 1) ])
            let reply, next = run (utc 2026 11 1 9 20) (ScheduleCommand.Fire(nine 1)) state
            Expect.equal reply (Answered(ScheduleEvent.Firing(nine 1, "remind", hello))) "first verdict after fired"
            Expect.equal next state "unchanged after fired"

        testCase "schedule aggregate: started is accepted after a pause, a change or a delete" <| fun _ ->
            let changed = { spec daily CatchUp.Latest with Input = Payload.ofValue "bye" }
            for later in [ ScheduleCommand.Pause; ScheduleCommand.Change changed; ScheduleCommand.Delete ] do
                let state = given (created CatchUp.Latest @ [ nine 1, ScheduleCommand.Fire(nine 1); utc 2026 11 1 9 1, later ])
                let reply, next = run (utc 2026 11 1 9 2) (ScheduleCommand.Started(nine 1)) state
                Expect.equal reply (Stored(ScheduleEvent.Fired(nine 1))) $"after {later}"
                Expect.equal next.Last (Some { Due = nine 1; Workflow = "remind"; Input = hello; Started = true }) $"started after {later}"

        testCase "schedule aggregate: a repeated started gets the first verdict; started for a due time never accepted is refused" <| fun _ ->
            let state = given (created CatchUp.Latest @ [ nine 1, ScheduleCommand.Fire(nine 1); nine 1, ScheduleCommand.Started(nine 1) ])
            let reply, next = run (utc 2026 11 1 9 5) (ScheduleCommand.Started(nine 1)) state
            Expect.equal reply (Answered(ScheduleEvent.Fired(nine 1))) "first verdict"
            Expect.equal next state "unchanged"
            refused (nine 2) (ScheduleCommand.Started(nine 2)) state "never fired"

        testCase "schedule aggregate: resume skips due times inside the pause, whatever the catch-up policy" <| fun _ ->
            for policy in [ CatchUp.Latest; CatchUp.All; CatchUp.None ] do
                let state =
                    given (created policy @ [ utc 2026 11 1 8 30, ScheduleCommand.Pause
                                              utc 2026 11 3 12 0, ScheduleCommand.Resume ])
                let now = utc 2026 11 3 12 1
                for day in 1..3 do
                    refused now (ScheduleCommand.Fire(nine day)) state $"{policy}: 9:00 on day {day} fell inside the pause"
                let reply, _ = run (nine 4) (ScheduleCommand.Fire(nine 4)) state
                Expect.equal reply (Stored(ScheduleEvent.Firing(nine 4, "remind", hello))) $"{policy}: the first due time after the resume"

        testCase "schedule aggregate: create after delete is refused, with the same or a different content" <| fun _ ->
            let state = given (created CatchUp.Latest @ [ utc 2026 11 1 8 30, ScheduleCommand.Delete ])
            refused (utc 2026 11 1 8 40) (ScheduleCommand.Create(spec daily CatchUp.Latest)) state "same content"
            refused (utc 2026 11 1 8 40) (ScheduleCommand.Create(spec daily CatchUp.All)) state "different content"
            refused (utc 2026 11 1 8 40) (ScheduleCommand.Change(spec daily CatchUp.All)) state "change"
            refused (nine 1) (ScheduleCommand.Fire(nine 1)) state "fire"

        testCase "schedule aggregate: change stores a new anchor that an every rule counts from" <| fun _ ->
            let hourly = spec (ScheduleRule.Every(TimeSpan.FromHours 1.0)) CatchUp.Latest
            let changed = { hourly with Input = Payload.ofValue "bye" }
            let state = given [ utc 2026 11 1 8 0, ScheduleCommand.Create hourly ]
            let at = utc 2026 11 1 8 30
            let reply, state = run at (ScheduleCommand.Change changed) state
            Expect.equal reply (Stored(ScheduleEvent.Changed(changed, at))) "stored"
            Expect.equal state.Anchor at "anchor"
            Expect.equal state.Spec (Some changed) "spec"
            refused (utc 2026 11 1 9 0) (ScheduleCommand.Fire(utc 2026 11 1 9 0)) state "a due time of the old anchor"
            let reply, _ = run (utc 2026 11 1 9 30) (ScheduleCommand.Fire(utc 2026 11 1 9 30)) state
            Expect.equal reply (Stored(ScheduleEvent.Firing(utc 2026 11 1 9 30, "remind", Payload.ofValue "bye"))) "counted from the change"

        testCase "schedule aggregate: a repeated change gets the first verdict and keeps the anchor" <| fun _ ->
            let changed = spec daily CatchUp.All
            let at = utc 2026 11 1 8 30
            let state = given (created CatchUp.Latest @ [ at, ScheduleCommand.Change changed ])
            let reply, next = run (utc 2026 11 1 8 40) (ScheduleCommand.Change changed) state
            Expect.equal reply (Answered(ScheduleEvent.Changed(changed, at))) "first verdict"
            Expect.equal next state "unchanged"

        testCase "schedule aggregate: a daily due time before a change is never accepted, even to catch up" <| fun _ ->
            let state = given (created CatchUp.Latest @ [ utc 2026 11 2 10 0, ScheduleCommand.Change(spec daily CatchUp.All) ])
            refused (utc 2026 11 2 10 5) (ScheduleCommand.Fire(nine 1)) state "day 1, missed before the change"
            refused (utc 2026 11 2 10 5) (ScheduleCommand.Fire(nine 2)) state "day 2, missed before the change"
            let reply, _ = run (nine 3) (ScheduleCommand.Fire(nine 3)) state
            Expect.equal reply (Stored(ScheduleEvent.Firing(nine 3, "remind", hello))) "the first due time after the change"

        testCase "schedule aggregate: after downtime, Latest accepts only the latest missed due time" <| fun _ ->
            let state = given (created CatchUp.Latest)
            let now = utc 2026 11 4 12 0
            for day in 1..3 do
                refused now (ScheduleCommand.Fire(nine day)) state $"day {day}"
            refused now (ScheduleCommand.Fire(nine 5)) state "the next future due time"
            let reply, _ = run now (ScheduleCommand.Fire(nine 4)) state
            Expect.equal reply (Stored(ScheduleEvent.Firing(nine 4, "remind", hello))) "latest"

        testCase "schedule aggregate: after downtime, All accepts each missed due time in order" <| fun _ ->
            let mutable state = given (created CatchUp.All)
            let now = utc 2026 11 4 12 0
            for day in 1..4 do
                refused now (ScheduleCommand.Fire(nine (day + 1))) state $"out of order before day {day}"
                let reply, next = run now (ScheduleCommand.Fire(nine day)) state
                Expect.equal reply (Stored(ScheduleEvent.Firing(nine day, "remind", hello))) $"day {day}"
                state <- next
                refused now (ScheduleCommand.Fire(nine (day + 1))) state $"waits for day {day}'s fired"
                state <- snd (run now (ScheduleCommand.Started(nine day)) state)
            let reply, _ = run (nine 5) (ScheduleCommand.Fire(nine 5)) state
            Expect.equal reply (Stored(ScheduleEvent.Firing(nine 5, "remind", hello))) "then continues"

        testCase "schedule aggregate: after downtime, None accepts only the next future due time, when it comes" <| fun _ ->
            let state = given (created CatchUp.None)
            let now = utc 2026 11 4 12 0
            for day in 1..4 do
                refused now (ScheduleCommand.Fire(nine day)) state $"day {day}"
            refused now (ScheduleCommand.Fire(nine 5)) state "the next future due time has not come"
            let reply, _ = run (nine 5) (ScheduleCommand.Fire(nine 5)) state
            Expect.equal reply (Stored(ScheduleEvent.Firing(nine 5, "remind", hello))) "the next future due time"

        testCase "schedule aggregate: None accepts a fire that arrives on time" <| fun _ ->
            let state = given (created CatchUp.None)
            let reply, _ = run ((nine 1).AddSeconds 5.0) (ScheduleCommand.Fire(nine 1)) state
            Expect.equal reply (Stored(ScheduleEvent.Firing(nine 1, "remind", hello))) "on time"

        testCase "schedule aggregate: a fire up to a second early is accepted" <| fun _ ->
            let state = given (created CatchUp.Latest)
            for early in [ 50.0; 500.0; 1000.0 ] do
                let reply, _ = run ((nine 1).AddMilliseconds -early) (ScheduleCommand.Fire(nine 1)) state
                Expect.equal reply (Stored(ScheduleEvent.Firing(nine 1, "remind", hello))) $"{early} ms early"

        testCase "schedule aggregate: a fire more than a second early is refused, and the due time runs when it comes" <| fun _ ->
            let state = given (created CatchUp.Latest)
            let reply, next = run ((nine 1).AddMinutes -10.0) (ScheduleCommand.Fire(nine 1)) state
            match reply with
            | Answered(ScheduleEvent.Refused reason) ->
                Expect.stringContains reason "has not come" "the reason says the due time has not come"
            | other -> failtest $"10 minutes early: refused, got {other}"
            Expect.equal next state "10 minutes early: state unchanged"
            refused ((nine 1).AddMilliseconds -1001.0) (ScheduleCommand.Fire(nine 1)) state "just over a second early"
            let reply, _ = run (nine 1) (ScheduleCommand.Fire(nine 1)) next
            Expect.equal reply (Stored(ScheduleEvent.Firing(nine 1, "remind", hello))) "at the due time"

        testCase "schedule aggregate: a firing stores its due time as UTC, whatever kind the fire named" <| fun _ ->
            let state = given (created CatchUp.Latest)
            for kind in [ DateTimeKind.Unspecified; DateTimeKind.Local ] do
                // The same ticks as 09:00 UTC, so the fire names the allowed due time.
                let named = DateTime.SpecifyKind(nine 1, kind)
                let reply, next = run (nine 1) (ScheduleCommand.Fire named) state
                match reply with
                | Stored(ScheduleEvent.Firing(due, _, _)) ->
                    Expect.equal due.Kind DateTimeKind.Utc $"{kind}: the stored due time is UTC"
                    Expect.equal (due.ToString "O") "2026-11-01T09:00:00.0000000Z" $"{kind}: a run ID formats it with Z"
                | other -> failtest $"{kind}: firing stored, got {other}"
                Expect.equal next.Last.Value.Due.Kind DateTimeKind.Utc $"{kind}: the state's due time is UTC"

        testCase "schedule aggregate: repeated pause, resume and delete get the first verdict" <| fun _ ->
            let pausedAt = utc 2026 11 1 8 10
            let state = given (created CatchUp.Latest @ [ pausedAt, ScheduleCommand.Pause ])
            let reply, next = run (utc 2026 11 1 8 11) ScheduleCommand.Pause state
            Expect.equal reply (Answered(ScheduleEvent.Paused pausedAt)) "pause"
            Expect.equal next state "pause unchanged"
            let resumedAt = utc 2026 11 1 8 20
            let state = given (created CatchUp.Latest @ [ pausedAt, ScheduleCommand.Pause; resumedAt, ScheduleCommand.Resume ])
            let reply, next = run (utc 2026 11 1 8 21) ScheduleCommand.Resume state
            Expect.equal reply (Answered(ScheduleEvent.Resumed resumedAt)) "resume"
            Expect.equal next state "resume unchanged"
            let deletedAt = utc 2026 11 1 8 30
            let state = given (created CatchUp.Latest @ [ deletedAt, ScheduleCommand.Delete ])
            let reply, next = run (utc 2026 11 1 8 31) ScheduleCommand.Delete state
            Expect.equal reply (Answered(ScheduleEvent.Deleted deletedAt)) "delete"
            Expect.equal next state "delete unchanged"

        testCase "schedule aggregate: resume of a schedule that was never paused is refused" <| fun _ ->
            refused (utc 2026 11 1 8 30) ScheduleCommand.Resume (given (created CatchUp.Latest)) "never paused"

        testCase "schedule aggregate: pause, resume and delete are refused after a delete" <| fun _ ->
            let state = given (created CatchUp.Latest @ [ utc 2026 11 1 8 10, ScheduleCommand.Pause; utc 2026 11 1 8 30, ScheduleCommand.Delete ])
            refused (utc 2026 11 1 8 40) ScheduleCommand.Resume state "resume"
            refused (utc 2026 11 1 8 40) ScheduleCommand.Pause state "pause"

        testCase "schedule aggregate: events and state survive the journal serializer" <| fun _ ->
            let roundTrip (value: 'T) =
                Text.Json.JsonSerializer.Deserialize<'T>(Text.Json.JsonSerializer.Serialize(value, Serialization.jsonOptions), Serialization.jsonOptions)
            let s = spec (ScheduleRule.Weekly([ DayOfWeek.Monday ], TimeOnly(8, 15), "Europe/Oslo")) CatchUp.All
            let at = utc 2026 11 1 8 0
            for event in [ ScheduleEvent.Created(s, at)
                           ScheduleEvent.Changed(s, at)
                           ScheduleEvent.Paused at
                           ScheduleEvent.Resumed at
                           ScheduleEvent.Deleted at
                           ScheduleEvent.Firing(nine 1, "remind", hello)
                           ScheduleEvent.Fired(nine 1)
                           ScheduleEvent.Refused "no" ] do
                Expect.equal (roundTrip event) event $"{event}"
                let wrapped = envelope at event
                Expect.equal (roundTrip wrapped).EventDetails event $"{event} in its envelope"
            let state =
                given (created CatchUp.Latest @ [ nine 1, ScheduleCommand.Fire(nine 1)
                                                  utc 2026 11 1 9 10, ScheduleCommand.Pause
                                                  utc 2026 11 1 9 20, ScheduleCommand.Resume
                                                  utc 2026 11 1 9 30, ScheduleCommand.Delete ])
            Expect.equal (roundTrip state) state "state"
            Expect.equal (roundTrip Schedule.initial) Schedule.initial "initial state"

        // The journal and the snapshots hold this JSON. A change here breaks stored schedules and
        // runs, so it must come with a migration.
        testCase "schedule aggregate: stored values keep their exact JSON" <| fun _ ->
            let json (value: 'T) = Text.Json.JsonSerializer.Serialize<'T>(value, Serialization.jsonOptions)
            let golden (value: 'T) (expected: string) what =
                Expect.equal (json value) expected what
                Expect.equal (Text.Json.JsonSerializer.Deserialize<'T>(expected, Serialization.jsonOptions)) value $"{what} reads back"
            let at = utc 2026 11 1 8 0
            let weekly = ScheduleRule.Weekly([ DayOfWeek.Monday; DayOfWeek.Thursday ], TimeOnly(8, 15), "Europe/Oslo")
            let s = spec weekly CatchUp.All
            golden (ScheduleRule.Daily(TimeOnly(9, 0), "Europe/Oslo")) """{"Case":"Daily","time":"09:00:00","zone":"Europe/Oslo"}""" "Daily"
            golden weekly """{"Case":"Weekly","days":[1,4],"time":"08:15:00","zone":"Europe/Oslo"}""" "Weekly"
            golden (ScheduleRule.Every(TimeSpan.FromMinutes 90.0)) """{"Case":"Every","interval":"01:30:00"}""" "Every"
            golden CatchUp.Latest """{"Case":"Latest"}""" "Latest"
            golden CatchUp.All """{"Case":"All"}""" "All"
            golden CatchUp.None """{"Case":"None"}""" "None"
            golden (ScheduleEvent.Created(s, at)) """{"Case":"Created","spec":{"Workflow":"remind","Input":{"Type":"System.String","Json":"\u0022hello\u0022"},"Rule":{"Case":"Weekly","days":[1,4],"time":"08:15:00","zone":"Europe/Oslo"},"CatchUp":{"Case":"All"}},"at":"2026-11-01T08:00:00Z"}""" "Created"
            golden (ScheduleEvent.Changed(s, at)) """{"Case":"Changed","spec":{"Workflow":"remind","Input":{"Type":"System.String","Json":"\u0022hello\u0022"},"Rule":{"Case":"Weekly","days":[1,4],"time":"08:15:00","zone":"Europe/Oslo"},"CatchUp":{"Case":"All"}},"at":"2026-11-01T08:00:00Z"}""" "Changed"
            golden (ScheduleEvent.Paused at) """{"Case":"Paused","at":"2026-11-01T08:00:00Z"}""" "Paused"
            golden (ScheduleEvent.Resumed at) """{"Case":"Resumed","at":"2026-11-01T08:00:00Z"}""" "Resumed"
            golden (ScheduleEvent.Deleted at) """{"Case":"Deleted","at":"2026-11-01T08:00:00Z"}""" "Deleted"
            golden (ScheduleEvent.Firing(nine 1, "remind", hello)) """{"Case":"Firing","due":"2026-11-01T09:00:00Z","workflow":"remind","input":{"Type":"System.String","Json":"\u0022hello\u0022"}}""" "Firing"
            golden (ScheduleEvent.Fired(nine 1)) """{"Case":"Fired","due":"2026-11-01T09:00:00Z"}""" "Fired"
            golden (ScheduleEvent.Refused "no") """{"Case":"Refused","reason":"no"}""" "Refused"
            let last = { Due = nine 1; Workflow = "remind"; Input = hello; Started = true }
            golden last """{"Due":"2026-11-01T09:00:00Z","Workflow":"remind","Input":{"Type":"System.String","Json":"\u0022hello\u0022"},"Started":true}""" "AcceptedDue"
            let state =
                { Created = Some(s, at)
                  Spec = Some { s with CatchUp = CatchUp.Latest }
                  Anchor = utc 2026 11 1 8 30
                  PausedAt = Some(utc 2026 11 1 9 10)
                  ResumedAt = Some(utc 2026 11 1 9 5)
                  DeletedAt = Some(utc 2026 11 1 9 30)
                  Last = Some last }
            golden state """{"Created":[{"Workflow":"remind","Input":{"Type":"System.String","Json":"\u0022hello\u0022"},"Rule":{"Case":"Weekly","days":[1,4],"time":"08:15:00","zone":"Europe/Oslo"},"CatchUp":{"Case":"All"}},"2026-11-01T08:00:00Z"],"Spec":{"Workflow":"remind","Input":{"Type":"System.String","Json":"\u0022hello\u0022"},"Rule":{"Case":"Weekly","days":[1,4],"time":"08:15:00","zone":"Europe/Oslo"},"CatchUp":{"Case":"Latest"}},"Anchor":"2026-11-01T08:30:00Z","PausedAt":"2026-11-01T09:10:00Z","ResumedAt":"2026-11-01T09:05:00Z","DeletedAt":"2026-11-01T09:30:00Z","Last":{"Due":"2026-11-01T09:00:00Z","Workflow":"remind","Input":{"Type":"System.String","Json":"\u0022hello\u0022"},"Started":true}}""" "ScheduleState"
            golden Schedule.initial """{"Anchor":"0001-01-01T00:00:00"}""" "the initial ScheduleState"
            golden { Schedule = "ada daily"; Due = nine 1; Input = "drink water" } """{"Schedule":"ada daily","Due":"2026-11-01T09:00:00Z","Input":"drink water"}""" "ScheduledRun"
            Expect.equal (Payload.ofValue { Schedule = "ada daily"; Due = nine 1; Input = "drink water" }).Json
                (json { Schedule = "ada daily"; Due = nine 1; Input = "drink water" }) "a run's stored input is that JSON"
    ]
