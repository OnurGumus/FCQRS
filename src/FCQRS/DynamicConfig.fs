module FCQRS.DynamicConfig

open System
open Microsoft.Extensions.Configuration
open System.Runtime.CompilerServices
open System.Dynamic
open System.Collections.Generic

[<AutoOpen>]
module Internal =
    /// Recursively converts ExpandoObject nodes whose keys form a dense 0..n-1
    /// integer run into arrays (HOCON's array shape). Sparse or duplicate
    //  ("0" vs "00") numeric keys are left as objects: converting them would
    //  crash or silently corrupt. Recurses into array elements so arrays nested
    //  inside array elements (a:0:b:0 = x) convert too.
    let rec internal convertNumericRuns (node: ExpandoObject) : obj =
        let dict = node :> IDictionary<string, obj>
        let keys = dict.Keys |> List.ofSeq

        let parsed =
            keys
            |> List.choose (fun k ->
                match Int32.TryParse k with
                | true, v -> Some v
                | _ -> None)

        let isDenseIntRun =
            keys.Length > 0
            && parsed.Length = keys.Length
            && (parsed |> List.sort = [ 0 .. keys.Length - 1 ])

        if isDenseIntRun then
            let arr: obj array = keys.Length |> Array.zeroCreate

            for kvp in dict do
                arr.[kvp.Key |> Int32.Parse] <-
                    match kvp.Value with
                    | :? ExpandoObject as e -> convertNumericRuns e
                    | v -> v

            box arr
        else
            for k in keys do
                match dict.[k] with
                | :? ExpandoObject as e -> dict.[k] <- convertNumericRuns e
                | _ -> ()

            box node

let internal getSection (configs: KeyValuePair<string, _> seq) : obj =
    let result = ExpandoObject()

    for kvp in configs do
        let mutable parent = result :> IDictionary<_, _>
        let path = kvp.Key.Split(':')
        let mutable i = 0

        // Descend, creating intermediate nodes. A key may legally have both a
        // scalar value and children (env-var style config); overwriting on
        // collision (last write wins) beats crashing on Add/downcast.
        while i < path.Length - 1 do
            match parent.TryGetValue path.[i] with
            | true, (:? ExpandoObject as child) -> parent <- child :> IDictionary<_, _>
            | _ ->
                let child = ExpandoObject()
                parent.[path.[i]] <- child
                parent <- child :> IDictionary<_, _>

            i <- i + 1

        if kvp.Value |> isNull |> not then
            parent.[path.[i]] <- kvp.Value

    convertNumericRuns result |> ignore
    upcast result

[<Extension>]
type ConfigExtension() =
    /// <summary>
    /// An extension method that returns the given configuration section as a dynamic Expando object
    /// </summary>
    /// <returns>An expando object representing the requested section. A section with no keys
    /// yields an empty expando object (mirroring IConfiguration.GetSection, which never returns
    /// null), so callers can layer their own defaults underneath, e.g. Akka's reference.conf.</returns>
    /// <exception cref="System.ArgumentNullException">Thrown when configuration or section is null</exception>
    [<Extension>]

    static member GetSectionAsDynamic(configuration: IConfiguration, section: string) : obj =
        if isNull (box configuration) then nullArg "configuration"
        if isNull (box section) then nullArg "section"

        let configs =
            configuration.GetSection(section).AsEnumerable()
            |> Seq.filter (fun k -> k.Key.StartsWith(sprintf "%s:" section))

        let res = getSection configs

        let paths = section.Split(":", StringSplitOptions.None) |> List.ofArray

        // Descend the full path so the returned node is the requested section itself.
        // A missing segment (section absent from the configuration) yields an empty
        // object instead of a KeyNotFoundException.
        let rec loop paths (res: obj) =
            match paths, res with
            | head :: tail, (:? IDictionary<string, obj> as d) ->
                match d.TryGetValue head with
                | true, v -> loop tail v
                | _ -> box (ExpandoObject())
            | _ -> res

        loop paths res

    /// <summary>
    /// An extension method that returns given string as an dynamic Expando object
    /// </summary>
    /// <returns>An expando object represents given section</returns>
    /// <exception cref="System.ArgumentNullException">Thrown configuration is null</exception>
    [<Extension>]
    static member GetRootAsDynamic(configuration: IConfiguration) : obj =
        let configs = configuration.AsEnumerable()
        getSection configs
