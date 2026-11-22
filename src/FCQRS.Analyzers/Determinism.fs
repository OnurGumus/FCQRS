/// Finds code that must be deterministic but reads the clock, a GUID generator, a random source,
/// or the environment: a workflow body, which runs again on every recovery, and an aggregate's fold,
/// which runs again for every stored event on every recovery.
module FCQRS.Analyzers.Determinism

open System.Collections.Generic
open FSharp.Analyzers.SDK
open FSharp.Compiler.Symbols
open FSharp.Compiler.Symbols.FSharpExprPatterns
open FSharp.Compiler.Text

/// Where an expression runs.
type private Scope =
    | Outside
    /// A workflow body: the function given to `Workflow.define` and the code in `workflow { }`.
    | Body
    /// The fold of an `Aggregate` record.
    | Fold

[<Literal>]
let BodyCode = "FCQRS001"

[<Literal>]
let FoldCode = "FCQRS002"

/// Reads whose value changes between runs, by declaring type and compiled member name, with the
/// name a message shows.
let private reads =
    dict
        [ ("System.DateTime", "get_Now"), "DateTime.Now"
          ("System.DateTime", "get_UtcNow"), "DateTime.UtcNow"
          ("System.DateTime", "get_Today"), "DateTime.Today"
          ("System.DateTimeOffset", "get_Now"), "DateTimeOffset.Now"
          ("System.DateTimeOffset", "get_UtcNow"), "DateTimeOffset.UtcNow"
          ("System.TimeProvider", "GetUtcNow"), "TimeProvider.GetUtcNow"
          ("System.TimeProvider", "GetLocalNow"), "TimeProvider.GetLocalNow"
          ("System.TimeProvider", "GetTimestamp"), "TimeProvider.GetTimestamp"
          ("System.Diagnostics.Stopwatch", "StartNew"), "Stopwatch.StartNew"
          ("System.Diagnostics.Stopwatch", "GetTimestamp"), "Stopwatch.GetTimestamp"
          ("System.Environment", "get_TickCount"), "Environment.TickCount"
          ("System.Environment", "get_TickCount64"), "Environment.TickCount64"
          ("System.Environment", "GetEnvironmentVariable"), "Environment.GetEnvironmentVariable"
          ("System.Environment", "GetEnvironmentVariables"), "Environment.GetEnvironmentVariables"
          ("System.Guid", "NewGuid"), "Guid.NewGuid"
          ("System.Guid", "CreateVersion7"), "Guid.CreateVersion7"
          ("System.Random", ".ctor"), "new Random"
          ("System.Random", "get_Shared"), "Random.Shared" ]

let private fullName (entity: FSharpEntity) =
    try entity.FullName with _ -> entity.DisplayName

let private readOf (mfv: FSharpMemberOrFunctionOrValue) : string option =
    match mfv.DeclaringEntity with
    | Some entity ->
        match reads.TryGetValue((fullName entity, mfv.CompiledName)) with
        | true, name -> Some name
        | _ -> None
    | None -> None

let private isType (name: string) (t: FSharpType) =
    let t = if t.IsAbbreviation then t.AbbreviatedType else t
    t.HasTypeDefinition && fullName t.TypeDefinition = name

/// A lambda over `AttemptContext` is an activity's call, where reading the clock and doing I/O
/// belong: its result is stored.
let private isActivity (parameter: FSharpMemberOrFunctionOrValue) =
    isType "FCQRS.Workflows.AttemptContext" parameter.FullType

/// The builders whose computation expressions are workflow bodies.
let private isWorkflowBuilder (mfv: FSharpMemberOrFunctionOrValue) =
    match mfv.ApparentEnclosingEntity with
    | Some entity ->
        let name = fullName entity
        name = "FCQRS.Workflows.WorkflowBuilder" || name = "FCQRS.Workflows.WorkflowResultBuilder"
    | None -> false

let private isDefine (mfv: FSharpMemberOrFunctionOrValue) =
    mfv.CompiledName = "define"
    && (match mfv.DeclaringEntity with
        | Some entity -> fullName entity = "FCQRS.Workflows.Workflow"
        | None -> false)

let private isAggregateRecord (t: FSharpType) = isType "FCQRS.FSharp.Aggregate`3" t || isType "FCQRS.FSharp.Aggregate" t

/// The function a field holds: `fold`, or `fun e s -> fold e s`.
let rec private functionOf (expr: FSharpExpr) : FSharpMemberOrFunctionOrValue option =
    match expr with
    | Value v -> Some v
    | Lambda(_, body) -> functionOf body
    | Call(None, v, _, _, _) -> Some v
    | Application(Value v, _, _) -> Some v
    | _ -> None

/// The messages for one checked file.
let analyze (tree: FSharpImplementationFileContents) : Message list =
    // The functions this file defines, with their bodies, so a call to one can be followed.
    let bodies = List<FSharpMemberOrFunctionOrValue * FSharpExpr>()

    let rec collect (declarations: FSharpImplementationFileDeclaration list) =
        for declaration in declarations do
            match declaration with
            | FSharpImplementationFileDeclaration.Entity(_, inner) -> collect inner
            | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(v, _, body) -> bodies.Add((v, body))
            | FSharpImplementationFileDeclaration.InitAction _ -> ()

    collect tree.Declarations

    let bodyOf (v: FSharpMemberOrFunctionOrValue) =
        bodies |> Seq.tryFind (fun (defined, _) -> defined.IsEffectivelySameAs v) |> Option.map snd

    // The first read a function's body makes, outside its activities, following the calls it
    // makes to this file's functions. None while it is being looked at, so recursion ends.
    let readsOf = Dictionary<FSharpMemberOrFunctionOrValue, string option>(HashIdentity.Reference)

    let rec firstRead (expr: FSharpExpr) : string option =
        match expr with
        | Lambda(parameter, _) when isActivity parameter -> None
        | Call(_, mfv, _, _, _) | NewObject(mfv, _, _) when (readOf mfv).IsSome -> readOf mfv
        | Call(_, mfv, _, _, args) ->
            match readOfFunction mfv with
            | Some read -> Some read
            | None -> args |> List.tryPick firstRead
        | Application(Value v, _, args) ->
            match readOfFunction v with
            | Some read -> Some read
            | None -> args |> List.tryPick firstRead
        | _ -> expr.ImmediateSubExpressions |> List.tryPick firstRead

    and readOfFunction (v: FSharpMemberOrFunctionOrValue) : string option =
        match readsOf.TryGetValue v with
        | true, known -> known
        | _ ->
            match bodyOf v with
            | None -> None
            | Some body ->
                readsOf[v] <- None
                let found = firstRead body
                readsOf[v] <- found
                found

    let messages = List<Message>()
    let reported = HashSet<range>()

    let report (scope: Scope) (range: range) (what: string) =
        if reported.Add range then
            let code, text =
                match scope with
                | Body ->
                    BodyCode,
                    $"A workflow body {what}. The body runs again on every recovery and must take the same path: read the time with `now`, make IDs with `newGuid` and random numbers with `random`, or move the call into an activity."
                | _ ->
                    FoldCode,
                    $"An aggregate's fold {what}. The fold runs again for every stored event on recovery and must give the same state: read the value in Decide and store it in the event."

            messages.Add(
                { Type = "FCQRS determinism"
                  Message = text
                  Code = code
                  Severity = Severity.Warning
                  Range = range
                  Fixes = [] }
            )

    let rec visit (scope: Scope) (expr: FSharpExpr) =
        match expr with
        | Lambda(parameter, body) when isActivity parameter -> visit Outside body
        | Call(target, mfv, _, _, args) ->
            match scope, readOf mfv with
            | (Body | Fold), Some read -> report scope expr.Range $"reads {read}"
            | (Body | Fold), None ->
                readOfFunction mfv |> Option.iter (fun read -> report scope expr.Range $"calls `{mfv.DisplayName}`, which reads {read}")
            | _ -> ()

            let inner = if isWorkflowBuilder mfv || isDefine mfv then Body else scope
            target |> Option.iter (visit inner)
            args |> List.iter (visit inner)
        | NewObject(mfv, _, args) ->
            match scope, readOf mfv with
            | (Body | Fold), Some read -> report scope expr.Range $"reads {read}"
            | _ -> ()

            args |> List.iter (visit scope)
        | Application(Value v, _, args) ->
            match scope with
            | Body | Fold ->
                readOfFunction v |> Option.iter (fun read -> report scope expr.Range $"calls `{v.DisplayName}`, which reads {read}")
            | Outside -> ()

            args |> List.iter (visit scope)
        | NewRecord(recordType, fields) when isAggregateRecord recordType ->
            let names = recordType.TypeDefinition.FSharpFields |> Seq.map _.Name |> List.ofSeq

            for name, field in List.zip names fields do
                if name = "Fold" then
                    match field with
                    | Lambda _ when (functionOf field |> Option.bind bodyOf).IsNone -> visit Fold field
                    | _ ->
                        match functionOf field |> Option.bind readOfFunction with
                        | Some read ->
                            let name = (functionOf field |> Option.get).DisplayName
                            report Fold field.Range $"is `{name}`, which reads {read}"
                        | None -> visit Fold field
                else
                    visit scope field
        | _ -> expr.ImmediateSubExpressions |> List.iter (visit scope)

    for _, body in bodies do
        visit Outside body

    let rec visitInit (declarations: FSharpImplementationFileDeclaration list) =
        for declaration in declarations do
            match declaration with
            | FSharpImplementationFileDeclaration.Entity(_, inner) -> visitInit inner
            | FSharpImplementationFileDeclaration.InitAction action -> visit Outside action
            | _ -> ()

    visitInit tree.Declarations
    List.ofSeq messages

let private run (tree: FSharpImplementationFileContents option) : Async<Message list> =
    async {
        match tree with
        | Some tree -> return analyze tree
        | None -> return []
    }

[<Literal>]
let private Description =
    "Warns when a workflow body or an aggregate's fold reads the clock, a GUID generator, a random source, or the environment."

[<Literal>]
let private Help = "https://onurgumus.github.io/FCQRS/concepts/workflows.html#The-body-must-be-deterministic"

[<CliAnalyzer("FCQRS.Determinism", Description, Help)>]
let cliAnalyzer: Analyzer<CliContext> = fun context -> run context.TypedTree

[<EditorAnalyzer("FCQRS.Determinism", Description, Help)>]
let editorAnalyzer: Analyzer<EditorContext> = fun context -> run context.TypedTree
