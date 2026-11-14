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
            let rule = Daily(TimeOnly(9, 0), oslo)
            let anchor = utc 2026 11 1 0 0
            Expect.equal (ScheduleRule.next rule anchor (utc 2026 11 14 10 0)) (utc 2026 11 15 8 0) "next day, CET"

        testCase "schedule rule: a result is strictly after the given time" <| fun _ ->
            let rule = Daily(TimeOnly(9, 0), "UTC")
            let anchor = utc 2026 11 1 0 0
            Expect.equal (ScheduleRule.next rule anchor (utc 2026 11 14 9 0)) (utc 2026 11 15 9 0) "strict"

        testCase "schedule rule: a local time skipped by spring forward runs at the next valid local time" <| fun _ ->
            let rule = Daily(TimeOnly(2, 30), oslo)
            let anchor = utc 2027 3 1 0 0
            Expect.equal (ScheduleRule.next rule anchor (utc 2027 3 27 12 0)) (utc 2027 3 28 1 0) "03:00 CEST is 01:00Z"

        testCase "schedule rule: a local time repeated by fall back runs once, at the first occurrence" <| fun _ ->
            let rule = Daily(TimeOnly(2, 30), oslo)
            let anchor = utc 2026 10 1 0 0
            let first = ScheduleRule.next rule anchor (utc 2026 10 24 12 0)
            Expect.equal first (utc 2026 10 25 0 30) "02:30 CEST is 00:30Z"
            let second = ScheduleRule.next rule anchor first
            Expect.equal second (utc 2026 10 26 1 30) "next 02:30 is CET, 01:30Z the day after"

        testCase "schedule rule: weekly runs on the listed days" <| fun _ ->
            let rule = Weekly([ DayOfWeek.Monday; DayOfWeek.Thursday ], TimeOnly(8, 0), "UTC")
            let anchor = utc 2026 11 1 0 0
            let monday = ScheduleRule.next rule anchor (utc 2026 11 14 12 0)
            Expect.equal monday (utc 2026 11 16 8 0) "Saturday to Monday"
            Expect.equal (ScheduleRule.next rule anchor monday) (utc 2026 11 19 8 0) "Monday to Thursday"

        testCase "schedule rule: every counts from the anchor" <| fun _ ->
            let rule = Every(TimeSpan.FromMinutes 90.0)
            let anchor = utc 2026 11 14 10 0
            Expect.equal (ScheduleRule.next rule anchor anchor) (utc 2026 11 14 11 30) "first"
            Expect.equal (ScheduleRule.next rule anchor (utc 2026 11 14 10 30)) (utc 2026 11 14 11 30) "within the first interval"
            Expect.equal (ScheduleRule.next rule anchor (utc 2026 11 14 11 30)) (utc 2026 11 14 13 0) "second"

        testCase "schedule rule: missed follows the catch-up policy" <| fun _ ->
            let rule = Daily(TimeOnly(9, 0), "UTC")
            let anchor = utc 2026 11 1 0 0
            let lastDue = utc 2026 11 10 9 0
            let now = utc 2026 11 13 9 30
            let all = [ utc 2026 11 11 9 0; utc 2026 11 12 9 0; utc 2026 11 13 9 0 ]
            Expect.equal (ScheduleRule.missed rule anchor lastDue now CatchUp.All) all "all, oldest first"
            Expect.equal (ScheduleRule.missed rule anchor lastDue now CatchUp.Latest) [ utc 2026 11 13 9 0 ] "latest"
            Expect.equal (ScheduleRule.missed rule anchor lastDue now CatchUp.None) [] "none"
            Expect.equal (ScheduleRule.missed rule anchor lastDue (utc 2026 11 10 12 0) CatchUp.Latest) [] "nothing missed"

        testCase "schedule rule: validate rejects an unknown zone, no days and a non-positive interval" <| fun _ ->
            Expect.isError (ScheduleRule.validate (Daily(TimeOnly(9, 0), "Mars/Base"))) "zone"
            Expect.isError (ScheduleRule.validate (Weekly([], TimeOnly(9, 0), "UTC"))) "days"
            Expect.isError (ScheduleRule.validate (Every TimeSpan.Zero)) "zero"
            Expect.isError (ScheduleRule.validate (Every(TimeSpan.FromMinutes -1.0))) "negative"
            Expect.isOk (ScheduleRule.validate (Daily(TimeOnly(9, 0), oslo))) "valid"

        testCase "schedule rule: rules and policies survive the journal serializer" <| fun _ ->
            let serializer = Payload.ofValue
            for rule in [ Daily(TimeOnly(9, 0), oslo)
                          Weekly([ DayOfWeek.Monday ], TimeOnly(8, 15), "UTC")
                          Every(TimeSpan.FromMinutes 90.0) ] do
                Expect.equal (Payload.toValue<ScheduleRule> (serializer rule)) rule "rule"
            for policy in [ CatchUp.Latest; CatchUp.All; CatchUp.None ] do
                Expect.equal (Payload.toValue<CatchUp> (serializer policy)) policy "policy"
    ]
