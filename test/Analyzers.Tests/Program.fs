/// Runs the determinism analyzers on small F# and C# programs and checks what they report. Each
/// language gets the same cases, so the two analyzers stay equivalent.
module Program

open System
open System.IO
open Expecto
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.Diagnostics

/// The FCQRS assemblies the programs reference, next to this test.
let private fcqrs =
    [ "FCQRS.dll"; "FCQRS.Workflows.dll"; "FCQRS.Model.dll"; "FSharp.Core.dll"; "Akka.dll"; "Akkling.dll" ]
    |> List.map (fun name -> Path.Combine(AppContext.BaseDirectory, name))

/// The runtime's own assemblies.
let private runtime =
    (AppContext.GetData "TRUSTED_PLATFORM_ASSEMBLIES" :?> string).Split(Path.PathSeparator)
    |> Array.filter (fun path -> Path.GetFileName(path).StartsWith "System." || Path.GetFileName path = "netstandard.dll" || Path.GetFileName path = "mscorlib.dll")
    |> List.ofArray

let private checker = FSharpChecker.Create(keepAssemblyContents = true)

/// The codes and the source text each F# message points at.
let private fsharp (source: string) : (string * string) list =
    let file = Path.Combine(Path.GetTempPath(), $"analyzer_{Guid.NewGuid():N}.fs")
    let text = SourceText.ofString source
    let options =
        let projectOptions, _ =
            checker.GetProjectOptionsFromScript(file, text, assumeDotNetFramework = false, useSdkRefs = true)
            |> Async.RunSynchronously
        { projectOptions with
            SourceFiles = [| file |]
            OtherOptions =
                Array.append
                    (projectOptions.OtherOptions |> Array.filter (fun option -> not (option.StartsWith "-r:" && option.Contains "FSharp.Core")))
                    [| for path in fcqrs -> $"-r:{path}" |] }
    let _, answer = checker.ParseAndCheckFileInProject(file, 0, text, options) |> Async.RunSynchronously
    match answer with
    | FSharpCheckFileAnswer.Succeeded results ->
        let errors = results.Diagnostics |> Array.filter (fun d -> d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)
        if errors.Length > 0 then failtestf "the F# program does not compile: %A" errors
        match results.ImplementationFile with
        | Some tree ->
            let lines = source.Split '\n'
            [ for message in FCQRS.Analyzers.Determinism.analyze tree ->
                  let line = lines[message.Range.StartLine - 1]
                  let finish = if message.Range.EndLine = message.Range.StartLine then message.Range.EndColumn else line.Length
                  message.Code, line.Substring(message.Range.StartColumn, finish - message.Range.StartColumn).Trim() ]
        | None -> failtest "no typed tree"
    | FSharpCheckFileAnswer.Aborted -> failtest "the F# check was aborted"

/// The codes and the source text each C# diagnostic points at.
let private csharp (source: string) : (string * string) list =
    let tree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions(LanguageVersion.Latest))
    let compilation =
        CSharpCompilation.Create(
            "Program",
            [ tree ],
            [ for path in runtime @ fcqrs -> MetadataReference.CreateFromFile path :> MetadataReference ],
            CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        )
    let errors = compilation.GetDiagnostics() |> Seq.filter (fun d -> d.Severity = DiagnosticSeverity.Error) |> List.ofSeq
    if not errors.IsEmpty then failtestf "the C# program does not compile: %A" errors
    let analyzers = compilation.WithAnalyzers(System.Collections.Immutable.ImmutableArray.Create<DiagnosticAnalyzer>(FCQRS.Analyzers.CSharp.DeterminismAnalyzer()))
    [ for diagnostic in analyzers.GetAnalyzerDiagnosticsAsync().Result |> Seq.sortBy _.Location.SourceSpan.Start ->
          diagnostic.Id, source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length).Trim() ]

let private fsharpHeader =
    """module Sample
open System
open System.Threading.Tasks
open FCQRS.Common
open FCQRS.FSharp
open FCQRS.Workflows
"""

let private csharpHeader =
    """using System;
using System.Threading.Tasks;
using FCQRS.Workflows;
using FCQRS.Workflows.CSharp;
using static FCQRS.Common;
using static FCQRS.CSharp;
"""

let private fsharpCases =
    testList "F# analyzer" [
        testCase "a body that reads the clock" <| fun _ ->
            let found = fsharp (fsharpHeader + """
let reminder =
    Workflow.define "reminder" (fun (name: string) -> workflow {
        let at = DateTime.UtcNow
        return $"{name} {at}"
    })
            """)
            Expect.equal found [ "FCQRS001", "DateTime.UtcNow" ] "the read is reported"

        testCase "a body that calls a helper that makes a GUID" <| fun _ ->
            let found = fsharp (fsharpHeader + """
let newKey () = Guid.NewGuid().ToString()
let keyed =
    Workflow.define "keyed" (fun (name: string) -> workflow {
        let key = newKey ()
        return key
    })
            """)
            Expect.equal found [ "FCQRS001", "newKey ()" ] "the call is reported where the body makes it"

        testCase "a body that uses now, and an activity that reads the clock" <| fun _ ->
            let found = fsharp (fsharpHeader + """
let stamped =
    Workflow.define "stamped" (fun (name: string) -> workflow {
        let! at = now
        let! later = activity "read" (Retry.once (TimeSpan.FromSeconds 5.0)) (fun _ -> Task.FromResult DateTime.UtcNow)
        return at, later
    })
            """)
            Expect.isEmpty found "now and activities are allowed"

        testCase "a fold that reads the clock, inline and as a named function" <| fun _ ->
            let found = fsharp (fsharpHeader + """
let fold (event: Event<int>) (state: DateTime) = DateTime.Now
let inlineFold : Aggregate<DateTime, int, int> =
    { Name = "Inline"; Initial = DateTime.MinValue
      Decide = fun _ _ -> PersistEvent 1
      Fold = fun _ _ -> DateTime.UtcNow
      Snapshots = NoSnapshots; Passivation = PassivationPolicy.Default }
let namedFold : Aggregate<DateTime, int, int> =
    { Name = "Named"; Initial = DateTime.MinValue
      Decide = fun _ _ -> PersistEvent 1
      Fold = fold
      Snapshots = NoSnapshots; Passivation = PassivationPolicy.Default }
            """)
            Expect.equal found [ "FCQRS002", "DateTime.UtcNow"; "FCQRS002", "fold" ] "both folds are reported"

        testCase "a decision that reads the clock" <| fun _ ->
            let found = fsharp (fsharpHeader + """
let decided : Aggregate<int, int, DateTime> =
    { Name = "Decided"; Initial = 0
      Decide = fun _ _ -> PersistEvent DateTime.UtcNow
      Fold = fun _ state -> state + 1
      Snapshots = NoSnapshots; Passivation = PassivationPolicy.Default }
            """)
            Expect.isEmpty found "a decision may read the clock: its event stores the value"
    ]

let private csharpCases =
    testList "C# analyzer" [
        testCase "a body that reads the clock" <| fun _ ->
            let found = csharp (csharpHeader + """
public sealed class Reminder() : Workflow<string, string>("reminder")
{
    public override async WorkflowTask<string> Run(WorkflowContext ctx, string name)
    {
        var at = DateTime.UtcNow;
        return $"{name} {at}";
    }
}
            """)
            Expect.equal found [ "FCQRS001", "DateTime.UtcNow" ] "the read is reported"

        testCase "a body that calls a helper that makes a GUID" <| fun _ ->
            let found = csharp (csharpHeader + """
public sealed class Keyed() : Workflow<string, string>("keyed")
{
    static string NewKey() => Guid.NewGuid().ToString();
    public override async WorkflowTask<string> Run(WorkflowContext ctx, string name)
    {
        var key = NewKey();
        return key;
    }
}
            """)
            Expect.equal found [ "FCQRS001", "NewKey()" ] "the call is reported where the body makes it"

        testCase "a body that uses Now, and an activity that reads the clock" <| fun _ ->
            let found = csharp (csharpHeader + """
public sealed class Stamped() : Workflow<string, string>("stamped")
{
    public override async WorkflowTask<string> Run(WorkflowContext ctx, string name)
    {
        var at = await ctx.Now();
        var later = await ctx.Activity("read", Retry.once(TimeSpan.FromSeconds(5)), attempt => Task.FromResult(DateTime.UtcNow));
        return $"{at} {later}";
    }
}
            """)
            Expect.isEmpty found "Now and activities are allowed"

        testCase "a fold that reads the clock, inline and through a helper" <| fun _ ->
            let found = csharp (csharpHeader + """
public sealed class Inline : Aggregate<DateTime, int, int>
{
    public override string EntityName => "Inline";
    public override DateTime InitialState => DateTime.MinValue;
    public override EventAction<int> HandleCommand(Command<int> command, DateTime state) => EventAction<int>.NewPersistEvent(1);
    public override DateTime ApplyEvent(Event<int> @event, DateTime state) => DateTime.UtcNow;
}
public sealed class Named : Aggregate<DateTime, int, int>
{
    static DateTime Fold() => DateTime.Now;
    public override string EntityName => "Named";
    public override DateTime InitialState => DateTime.MinValue;
    public override EventAction<int> HandleCommand(Command<int> command, DateTime state) => EventAction<int>.NewPersistEvent(1);
    public override DateTime ApplyEvent(Event<int> @event, DateTime state) => Fold();
}
            """)
            Expect.equal found [ "FCQRS002", "DateTime.UtcNow"; "FCQRS002", "Fold()" ] "both folds are reported"

        testCase "a decision that reads the clock" <| fun _ ->
            let found = csharp (csharpHeader + """
public sealed class Decided : Aggregate<int, int, DateTime>
{
    public override string EntityName => "Decided";
    public override int InitialState => 0;
    public override EventAction<DateTime> HandleCommand(Command<int> command, int state) => EventAction<DateTime>.NewPersistEvent(DateTime.UtcNow);
    public override int ApplyEvent(Event<DateTime> @event, int state) => state + 1;
}
            """)
            Expect.isEmpty found "a decision may read the clock: its event stores the value"
    ]

[<EntryPoint>]
let main args = runTestsWithCLIArgs [] args (testList "analyzers" [ fsharpCases; csharpCases ])
