namespace FCQRS.ExpectoTickSpec

open System
open System.Collections.Concurrent
open System.IO
open System.Reflection
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
/// A feature whose scenarios are ALL pending (e.g. one `@pending` above
/// `Feature:`) is built from its parsed SOURCE without binding any steps, so
/// a spec written ahead of its implementation joins the suite as pending
/// scenarios instead of failing step binding. Scenario-level `@pending`
/// still requires the feature's other steps to bind. The legacy `_` name
/// prefix also focuses a feature or scenario.
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

    /// Expecto test tree for a BOUND feature: scenarios grouped by `Rule:`,
    /// focus/pending from tags and the `_` prefix.
    let testListFromFeature (feature: Feature) : Test =
        feature.Scenarios
        |> Seq.toList
        |> List.map (fun (scenario: Scenario) ->
            let case =
                caseFor (focusOf scenario.Name scenario.Tags) scenario.Name
                <| fun () ->
                    try
                        scenario.Action.Invoke()
                    with
                    | :? TargetInvocationException as ex when ex.InnerException <> null ->
                        ex.InnerException |> nonNull |> ExceptionDispatchInfo.Capture |> _.Throw()
                    | _ -> reraise ()

            scenario.Rule, case)
        |> treeOf feature.Name

    /// A fully-pending feature built from SOURCE only: scenarios become
    /// pending cases without ever binding steps.
    let private pendingTreeFromSource (source: FeatureSource) : Test =
        source.Scenarios
        |> Array.toList
        |> List.map (fun s -> s.Rule, ptestCase s.Name ignore)
        |> treeOf source.Name

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

        let allPending =
            source.Scenarios.Length > 0
            && source.Scenarios |> Array.forall (fun s -> hasTag "pending" s.Tags)

        if allPending then
            pendingTreeFromSource source
        else
            use reader = new StringReader(String.Join("\n", lines))

            (stepDefinitionsFor assembly).GenerateFeature(resource, reader)
            |> testListFromFeature
