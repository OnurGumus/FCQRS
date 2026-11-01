namespace FCQRS.ExpectoTickSpec

open System
open System.Collections.Concurrent
open System.IO
open System.Reflection
open System.Text.RegularExpressions
open TickSpec
open Expecto
open System.Runtime.ExceptionServices

/// TickSpec -> Expecto bridge. Feature files load from embedded resources in
/// the CALLER's assembly and scenarios group by their Gherkin `Rule:` into
/// nested test lists.
///
/// Focus and pending are driven by Gherkin tags — TickSpec merges feature-
/// and rule-level tags into every scenario's Tags, so the tag works at any
/// level:
///   @focus    -> Expecto Focused (only focused tests run; the rest report
///                as skipped)
///   @pending  -> Expecto Pending (never runs, reported as pending)
/// Pending scenarios are cut from the feature text before TickSpec binds it,
/// so their steps do not need to exist: a spec written ahead of its
/// implementation joins the suite as pending scenarios instead of failing
/// step binding. `discover` also takes a `Pending.Entry` list, which skips
/// scenarios the same way and reports why each one waits. The legacy `_`
/// name prefix also focuses a feature or scenario.
///
/// Every entry point takes the assembly holding the steps and feature
/// resources explicitly. Resolving it here (GetExecutingAssembly) would bind
/// to THIS library once it ships as a compiled package, silently scanning an
/// assembly with no steps — pass your test assembly instead, typically
/// `Assembly.GetExecutingAssembly()` at the call site.
module FeatureTest =

    /// StepDefinitions reflect over the whole assembly; build once per
    /// assembly, not once per feature.
    let private stepDefinitionsCache =
        ConcurrentDictionary<Assembly, StepDefinitions>()

    let private stepDefinitionsFor (assembly: Assembly) =
        stepDefinitionsCache.GetOrAdd(assembly, fun a -> StepDefinitions a)

    /// TickSpec stores tags without the '@'.
    let private hasTag (tag: string) (tags: string[]) =
        tags
        |> Array.exists (fun t -> String.Equals(t, tag, StringComparison.OrdinalIgnoreCase))

    /// FocusState from tags plus the legacy '_' name prefix; @pending wins
    /// over @focus (a parked scenario stays parked even while focusing).
    let private focusOf (name: string) (tags: string[]) =
        if hasTag "pending" tags then Pending
        elif hasTag "focus" tags || name.TrimStart().StartsWith "_" then Focused
        else Normal

    let private caseFor =
        function
        | Normal -> testCase
        | Focused -> ftestCase
        | Pending -> ptestCase

    let private readLines (assembly: Assembly) (fullResourceName: string) : string[] =
        match assembly.GetManifestResourceStream fullResourceName with
        | null -> failwithf "Feature file %s not found as embedded resource." fullResourceName
        | stream ->
            use reader = new StreamReader(stream)

            [|
                let mutable line = reader.ReadLine()

                while not (isNull line) do
                    yield nonNull line
                    line <- reader.ReadLine()
            |]

    /// TickSpec names each outline row after its header line plus " (n)".
    let private outlineRow = Regex(@" \(\d+\)$")

    /// The header line a TickSpec scenario name came from, e.g.
    /// "Scenario Outline: Adds <n> (2)" -> "Scenario Outline: Adds <n>".
    let private headerOf (name: string) : string =
        if name.StartsWith "Scenario Outline" then outlineRow.Replace(name, "") else name

    /// A name without its keyword: "Scenario: Pays at the door" -> "Pays at the door".
    let private titleOf (name: string) : string =
        match name.IndexOf ':' with
        | -1 -> name
        | colon -> name.Substring(colon + 1).Trim()

    /// Which part of a feature file a line belongs to.
    type private Part =
        /// The feature's header, background and shared examples: always kept.
        | Always
        | RulePart of rule: int
        | ScenarioPart of rule: int option * header: string

    /// The feature's lines with every scenario whose header line fails `keep`
    /// blanked out, with the tags above it. A rule left without scenarios is
    /// blanked whole, background included, since TickSpec refuses an empty
    /// rule. Lines are blanked, not removed, so TickSpec's line numbers still
    /// point into the real file.
    let private cut (keep: string -> bool) (lines: string[]) : string[] =
        let trimmed = lines |> Array.map _.Trim()
        let parts = Array.create lines.Length Always
        let mutable inDocString = false
        let mutable rule = None
        let mutable rules = 0

        for i in 0 .. lines.Length - 1 do
            let line = trimmed[i]

            if line.StartsWith "\"\"\"" then
                inDocString <- not inDocString
            elif not inDocString then
                let opened =
                    if line.StartsWith "Rule:" then
                        let index = rules
                        rules <- rules + 1
                        rule <- Some index
                        Some(RulePart index)
                    elif line.StartsWith "Scenario" then
                        Some(ScenarioPart(rule, line))
                    elif line.StartsWith "Shared Examples" then
                        Some Always
                    else
                        None

                match opened with
                | Some part ->
                    // Tags and blank lines just above a keyword belong to it.
                    let mutable start = i

                    while start > 0 && (trimmed[start - 1] = "" || trimmed[start - 1].StartsWith "@") do
                        start <- start - 1

                    parts[start..] <- Array.create (lines.Length - start) part
                | None -> ()

        let kept =
            function
            | Always -> true
            | ScenarioPart(_, header) -> keep header
            | RulePart rule ->
                let scenarios =
                    parts
                    |> Array.choose (function
                        | ScenarioPart(Some r, header) when r = rule -> Some header
                        | _ -> None)

                scenarios.Length = 0 || Array.exists keep scenarios

        Array.map2 (fun line part -> if kept part then line else "") lines parts

    /// Rule-grouped Expecto tree from (rule, case) pairs; a `_`-prefixed
    /// feature name focuses the whole list.
    let private treeOf (featureName: string) (scenarios: (string option * Test) list) : Test =
        let tests =
            scenarios
            |> List.groupBy fst
            |> List.collect (fun (rule, cases) ->
                let cases = cases |> List.map snd

                match rule with
                | Some ruleName -> [ testList ruleName cases ]
                | None -> cases)

        let featureTestListFunc =
            if featureName.TrimStart().StartsWith "_" then
                ftestList
            else
                testList

        featureTestListFunc featureName tests

    /// Runs a bound scenario, rethrowing the step's own exception rather than
    /// TickSpec's reflection wrapper.
    let private run (scenario: Scenario) () =
        try
            scenario.Action.Invoke()
        with
        | :? TargetInvocationException as ex when ex.InnerException <> null ->
            ex.InnerException |> nonNull |> ExceptionDispatchInfo.Capture |> _.Throw()
        | _ -> reraise ()

    /// Expecto test tree for a BOUND feature: scenarios grouped by `Rule:`,
    /// focus/pending from tags and the `_` prefix.
    let testListFromFeature (feature: Feature) : Test =
        feature.Scenarios
        |> Seq.toList
        |> List.map (fun (scenario: Scenario) ->
            scenario.Rule, caseFor (focusOf scenario.Name scenario.Tags) scenario.Name (run scenario))
        |> treeOf feature.Name

    /// What becomes of one scenario.
    type private Fate =
        | Runs
        /// `@pending`: reported as Expecto Pending.
        | Tagged
        /// A `Pending.Entry`: skipped with its reason.
        | Waits of reason: string

    /// The tree for one parsed feature. Only scenarios that run are bound, so
    /// the steps of the others need not exist.
    let private build
        (assembly: Assembly)
        (resource: string)
        (listName: string)
        (nameOf: string -> string)
        (fateOf: ScenarioSource -> Fate)
        (lines: string[])
        (source: FeatureSource)
        : Test =
        let fates = source.Scenarios |> Array.map (fun scenario -> scenario, fateOf scenario)
        let running = fates |> Array.filter (fun (_, fate) -> fate = Runs) |> Array.map fst
        let headers = running |> Array.map (fun scenario -> headerOf scenario.Name) |> Set.ofArray

        let bound =
            if running.Length = 0 then
                [||]
            else
                (stepDefinitionsFor assembly).GenerateFeature(resource, cut headers.Contains lines).Scenarios
                |> Seq.toArray

        // TickSpec binds the kept scenarios in source order.
        if bound.Length <> running.Length then
            failwithf
                "%s: %d scenarios should run but TickSpec bound %d. Two scenarios may share a title."
                resource
                running.Length
                bound.Length

        let next = Collections.Generic.Queue<Scenario>(bound)

        fates
        |> Array.toList
        |> List.map (fun (scenario, fate) ->
            let name = nameOf scenario.Name

            let case =
                match fate with
                | Runs ->
                    let binding = next.Dequeue()
                    caseFor (focusOf binding.Name binding.Tags) name (run binding)
                | Tagged -> ptestCase name ignore
                | Waits reason -> testCase name (fun () -> skiptest reason)

            scenario.Rule, case)
        |> treeOf listName

    /// Parse one feature from an embedded resource, binding its steps to
    /// definitions found in the same assembly.
    let featureFromEmbeddedResource (assembly: Assembly) (fullResourceName: string) : Feature =
        use reader =
            new StringReader(String.Join("\n", readLines assembly fullResourceName))

        (stepDefinitionsFor assembly).GenerateFeature(fullResourceName, reader)

    /// Test tree for `{resourcePrefix}.{baseFeatureName}.feature` embedded in
    /// `assembly` — steps are discovered in the same assembly:
    ///   createTest (Assembly.GetExecutingAssembly()) "MyApp" "login"
    let createTest (assembly: Assembly) (resourcePrefix: string) (baseFeatureName: string) : Test =
        let resource = $"{resourcePrefix}.{baseFeatureName}.feature"
        let lines = readLines assembly resource
        let source = FeatureParser.parseFeature lines
        let fateOf (scenario: ScenarioSource) = if hasTag "pending" scenario.Tags then Tagged else Runs
        build assembly resource source.Name id fateOf lines source

    /// Every `{resourcePrefix}.*.feature` embedded in `assembly`, one test
    /// list per file, named after the file. Tests are named by scenario title,
    /// without the `Scenario:` keyword. A scenario `pending` names is skipped
    /// with its reason, before any `@pending` tag. When `pending` is not
    /// empty, a first test fails if it names a feature or scenario that does
    /// not exist, so the list cannot go stale:
    ///   discover (Assembly.GetExecutingAssembly()) "MyApp" [ Pending.feature "billing" "needs invoices" ]
    let discover (assembly: Assembly) (resourcePrefix: string) (pending: Pending.Entry list) : Test list =
        let prefix = resourcePrefix + "."

        let features =
            [ for resource in assembly.GetManifestResourceNames() |> Array.sort do
                  if resource.StartsWith prefix && resource.EndsWith ".feature" then
                      let name = resource.Substring(prefix.Length, resource.Length - prefix.Length - ".feature".Length)
                      let lines = readLines assembly resource
                      yield name, resource, lines, FeatureParser.parseFeature lines ]

        let titlesOf (source: FeatureSource) =
            source.Scenarios
            |> Array.map (fun scenario -> titleOf (headerOf scenario.Name))
            |> Array.distinct
            |> List.ofArray

        let staleCheck =
            testCase "The pending list names only real scenarios" (fun () ->
                match Pending.stale pending [ for name, _, _, source in features -> name, titlesOf source ] with
                | [] -> ()
                | problems -> failtest (String.concat "\n" problems))

        let tree (name, resource, lines, source) =
            let fateOf (scenario: ScenarioSource) =
                match Pending.lookup pending name (titleOf (headerOf scenario.Name)) with
                | Some entry -> Waits entry.Reason
                | None when hasTag "pending" scenario.Tags -> Tagged
                | None -> Runs

            build assembly resource name titleOf fateOf lines source

        [ if not pending.IsEmpty then
              yield staleCheck
          for feature in features -> tree feature ]
