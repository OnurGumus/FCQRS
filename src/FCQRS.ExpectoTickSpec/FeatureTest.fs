namespace FCQRS.ExpectoTickSpec

open System.Reflection
open TickSpec
open Expecto
open System.Runtime.ExceptionServices

module FeatureTest =
    let assembly = Assembly.GetExecutingAssembly()
    let stepDefinitions = StepDefinitions assembly

    let featureFromEmbeddedResource fullResourceName =
        match assembly.GetManifestResourceStream fullResourceName with
        | null -> failwithf "Feature file %s not found as embedded resource." fullResourceName
        | stream ->
            use s = stream
            stepDefinitions.GenerateFeature(fullResourceName, s)
            
    let testListFromFeature feature =
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
                    ex.InnerException 
                    |> nonNull
                    |> ExceptionDispatchInfo.Capture 
                    |> _.Throw()
                | _ -> reraise ()

        let scenariosByRule =
            feature.Scenarios
            |> Seq.groupBy (fun s -> s.Rule)
            |> Seq.toList

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

    let createTest assembly baseFeatureName =
        $"{assembly}.{baseFeatureName}.feature"
        |> featureFromEmbeddedResource
        |> testListFromFeature
