namespace FCQRS.ExpectoTickSpec

open System.Collections.Concurrent
open System.Reflection
open TickSpec
open Expecto
open System.Runtime.ExceptionServices

/// TickSpec -> Expecto bridge. Feature files load from embedded resources in
/// the CALLER's assembly, scenarios group by their Gherkin `Rule:` into nested
/// test lists, and a `_` prefix on a feature or scenario name focuses it
/// (ftestList/ftestCase).
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

    /// Parse one feature from an embedded resource, binding its steps to
    /// definitions found in the same assembly.
    let featureFromEmbeddedResource (assembly: Assembly) (fullResourceName: string) : Feature =
        match assembly.GetManifestResourceStream fullResourceName with
        | null -> failwithf "Feature file %s not found as embedded resource." fullResourceName
        | stream ->
            use s = stream
            (stepDefinitionsFor assembly).GenerateFeature(fullResourceName, s)

    /// Expecto test tree for a parsed feature: scenarios grouped by `Rule:`,
    /// `_`-prefixed names focused.
    let testListFromFeature (feature: Feature) : Test =
        let createTestCase (scenario: Scenario) =
            let testCaseFunc =
                if scenario.Name.TrimStart().StartsWith "_" then
                    ftestCase
                else
                    testCase

            testCaseFunc scenario.Name
            <| fun () ->
                try
                    scenario.Action.Invoke()
                with
                | :? TargetInvocationException as ex when ex.InnerException <> null ->
                    ex.InnerException |> nonNull |> ExceptionDispatchInfo.Capture |> _.Throw()
                | _ -> reraise ()

        let scenariosByRule =
            feature.Scenarios |> Seq.groupBy (fun s -> s.Rule) |> Seq.toList

        let tests =
            scenariosByRule
            |> List.collect (fun (rule, scenarios) ->
                let testCases = scenarios |> Seq.map createTestCase |> Seq.toList

                match rule with
                | Some ruleName -> [ testList ruleName testCases ]
                | None -> testCases)

        let featureTestListFunc =
            if feature.Name.TrimStart().StartsWith "_" then
                ftestList
            else
                testList

        featureTestListFunc feature.Name tests

    /// Test tree for `{resourcePrefix}.{baseFeatureName}.feature` embedded in
    /// `assembly` — steps are discovered in the same assembly:
    ///   createTest (Assembly.GetExecutingAssembly()) "MyApp" "login"
    let createTest (assembly: Assembly) (resourcePrefix: string) (baseFeatureName: string) : Test =
        $"{resourcePrefix}.{baseFeatureName}.feature"
        |> featureFromEmbeddedResource assembly
        |> testListFromFeature
