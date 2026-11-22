# FCQRS.Analyzers

Warnings for code that FCQRS runs again on recovery and that must therefore give the same result
every time:

| Code | Where | Reports |
|---|---|---|
| `FCQRS001` | A workflow body: the function given to `Workflow.define`, a `workflow { }` or `workflowResult { }` expression, or a C# method that returns `WorkflowTask` | a read of the clock, a GUID generator, a random source, or an environment variable |
| `FCQRS002` | An aggregate's fold: the `Fold` field of an F# `Aggregate`, or a C# override of `ApplyEvent` | the same reads |

An activity's call (a lambda over `AttemptContext`) is not checked: its result is stored. A
decision (`Decide`, `HandleCommand`) is not checked either: the event it stores holds the value.
A call to a function or method defined in the same F# file, or in the same C# project, is followed,
and the warning points at the call.

## C#

Add the package. The compiler runs the analyzer in every build:

```xml
<PackageReference Include="FCQRS.Analyzers" Version="1.0.0" PrivateAssets="all" />
```

## F#

The F# compiler does not run analyzers during a build. Run them with the `fsharp-analyzers` tool
after a build, or in Ionide:

```bash
dotnet tool install fsharp-analyzers
dotnet build
dotnet fsharp-analyzers --project App.fsproj \
  --analyzers-path ~/.nuget/packages/fcqrs.analyzers/1.0.0/analyzers/dotnet/fs
```

The tool needs the project and its references built: before a build it checks the code against
missing assemblies and reports less.
