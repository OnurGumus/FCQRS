module FCQRS.DynamicConfig 
open System
open Microsoft.Extensions.Configuration
open System.Runtime.CompilerServices
open System.Dynamic
open System.Collections.Generic

[<AutoOpen>]
module Internal = 
    let rec internal replaceWithArray (parent: ExpandoObject | null) (key: string | null) (input: ExpandoObject option) =
        match input with
        | None -> ()
        | Some input ->
            let dict = input :> IDictionary<_, _>
            let keys = dict.Keys |> List.ofSeq

            if keys |> Seq.forall (Int32.TryParse >> fst) then
                let arr = keys.Length |> Array.zeroCreate

                for kvp in dict do
                    arr.[kvp.Key |> Int32.Parse] <- kvp.Value

                let parentDict = parent :> IDictionary<string|null, _>
                parentDict.Remove key |> ignore
                parentDict.Add(key, arr)
            else
                for childKey in keys do
                    let newInput =
                        match dict.[childKey] with
                        | :? ExpandoObject as e -> Some e
                        | _ -> None

                    replaceWithArray input childKey newInput

let getSection (configs: KeyValuePair<string, _> seq) : obj =
    let result = ExpandoObject()

    for kvp in configs do
        let mutable parent = result :> IDictionary<_, _>
        let path = kvp.Key.Split(':')
        let mutable i = 0

        while i < path.Length - 1 do
            if parent.ContainsKey(path.[i]) |> not then
                parent.Add(path.[i], ExpandoObject())

            parent <- downcast parent.[path.[i]]
            i <- i + 1

        if kvp.Value |> isNull |> not then
            parent.Add(path.[i], kvp.Value)

    replaceWithArray null null (Some result)
    upcast result

[<Extension>]
type ConfigExtension() =
    /// <summary>
    /// An extension method that returns given string as an dynamic Expando object
    /// </summary>
    /// <exception cref="System.ArgumentNullException">Thrown configuration or section is null</exception>
    [<Extension>]

    static member GetSectionAsDynamic(configuration: IConfiguration, section: string) : obj =

        let configs =
            configuration.GetSection(section).AsEnumerable()
            |> Seq.filter (fun k -> k.Key.StartsWith(sprintf "%s:" section))

        let res = getSection configs

        let paths = section.Split(":", StringSplitOptions.None) |> List.ofArray

        let rec loop paths (res: obj) =
            match paths, res with
            | head :: (_ :: _ as tail), (:? IDictionary<string, obj> as d) ->
                let v = d.[head]
                loop tail v
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
