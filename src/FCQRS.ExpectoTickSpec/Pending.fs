namespace FCQRS.ExpectoTickSpec

/// Scenarios that wait, listed in code with the reason each one waits.
///
/// A Gherkin tag cannot carry a sentence, so `@pending` says only THAT a
/// scenario waits. An entry here also says WHY, and the reason is reported
/// with the skipped test. Entries name a feature by its file name without
/// `.feature` and a scenario by its title without the `Scenario:` keyword:
///   Pending.scenario "booking" "Five people race for the last mat" "needs the waitlist"
module Pending =

    /// What an entry covers: a whole feature, or one scenario in it.
    type Covers =
        | WholeFeature
        | OneScenario of title: string

    /// One waiting feature or scenario, and why it waits.
    type Entry =
        { Feature: string
          Covers: Covers
          Reason: string }

    /// An entry for every scenario in a feature.
    let feature (name: string) (reason: string) : Entry =
        { Feature = name
          Covers = WholeFeature
          Reason = reason }

    /// An entry for one scenario. It wins over its feature's entry.
    let scenario (name: string) (title: string) (reason: string) : Entry =
        { Feature = name
          Covers = OneScenario title
          Reason = reason }

    /// The entry a scenario waits on: its own first, else its feature's.
    let lookup (entries: Entry list) (feature: string) (title: string) : Entry option =
        let ofFeature = entries |> List.filter (fun entry -> entry.Feature = feature)
        let covering covers = ofFeature |> List.tryFind (fun entry -> entry.Covers = covers)
        covering (OneScenario title) |> Option.orElse (covering WholeFeature)

    /// One line per entry that names no feature, or no scenario in its feature.
    /// `features` pairs each feature's name with its scenario titles.
    let stale (entries: Entry list) (features: (string * string list) list) : string list =
        let known = Map.ofList features

        let problem entry =
            match Map.tryFind entry.Feature known, entry.Covers with
            | None, _ -> Some $"\"{entry.Feature}\" is not a feature file"
            | Some titles, OneScenario title when not (List.contains title titles) ->
                Some $"\"{entry.Feature}\" has no scenario \"{title}\""
            | Some _, _ -> None

        entries |> List.choose problem
