---
title: Check bodies and folds for nondeterminism
category: Apply
categoryindex: 5
index: 10
---

# Check bodies and folds for nondeterminism

This workflow body reads the clock directly:

```fsharp isolated
open System
open FCQRS.Workflows

let reminder =
    Workflow.define "reminder" (fun (name: string) -> workflow {
        let sentAt = DateTime.UtcNow
        return $"{name} reminded at {sentAt}"
    })
```

<div class="cs-alt"></div>

```csharp
public sealed class Reminder() : Workflow<string, string>("reminder")
{
    public override async WorkflowTask<string> Run(
        WorkflowContext ctx, string name)
    {
        var sentAt = DateTime.UtcNow;
        return $"{name} reminded at {sentAt}";
    }
}
```

The body runs again every time the instance recovers, and each run reads a different time. A body
that branches on that value can take another path on replay, and the instance is
[parked](../concepts/workflows.html#Change-a-workflow-while-instances-are-running) or does the wrong
thing. An aggregate's fold has the same problem: recovery folds every stored event again, so a fold
that reads the clock gives a different state each time. FCQRS does not detect either at run time.

The FCQRS.Analyzers package reports them before the code runs:

| Code | Where | Reports |
|---|---|---|
| `FCQRS001` | the function given to `Workflow.define`, a `workflow { }` or `workflowResult { }` expression, or a C# method that returns `WorkflowTask` | a read of the clock, a GUID generator, a random source, or an environment variable |
| `FCQRS002` | the `Fold` field of an F# `Aggregate`, or a C# override of `ApplyEvent` | the same reads |

For the body above it reports:

```text
Warning FCQRS001 : A workflow body reads DateTime.UtcNow. The body runs again on every
recovery and must take the same path: read the time with `now`, make IDs with `newGuid` and
random numbers with `random`, or move the call into an activity.
```

The C# message names `ctx.Now()`, `ctx.NewGuid()`, and `ctx.Random()` instead. Read the time with
`now` (C#: `ctx.Now()`), which stores the value so that replay returns it:

```fsharp isolated
open System
open FCQRS.Workflows

let reminder =
    Workflow.define "reminder" (fun (name: string) -> workflow {
        let! sentAt = now
        return $"{name} reminded at {sentAt}"
    })
```

<div class="cs-alt"></div>

```csharp
public sealed class Reminder() : Workflow<string, string>("reminder")
{
    public override async WorkflowTask<string> Run(
        WorkflowContext ctx, string name)
    {
        var sentAt = await ctx.Now();
        return $"{name} reminded at {sentAt}";
    }
}
```

## Run the analyzers

In C#, add the package. The compiler runs the analyzer in every build:

```xml
<PackageReference Include="FCQRS.Analyzers" Version="1.0.0" PrivateAssets="all" />
```

The F# compiler does not run analyzers during a build. Add the package, build, and run the
`fsharp-analyzers` tool, or open the project in Ionide, which runs them in the editor:

```bash
dotnet tool install fsharp-analyzers
dotnet build
dotnet fsharp-analyzers --project App.fsproj \
  --analyzers-path ~/.nuget/packages/fcqrs.analyzers/1.0.0/analyzers/dotnet/fs \
  --treat-as-error FCQRS001 FCQRS002
```

Build first: the tool checks the code against the built references, and before a build it finds
less. `--treat-as-error` makes the tool exit with an error, so a CI step fails on a warning.

## What is checked, and what is not

- An activity's call, a lambda over `AttemptContext`, is not checked. Its result is stored, so it
  can read the clock and do I/O.
- A decision (`Decide`, `HandleCommand`) is not checked. The event it stores holds the value it
  read, and the fold reads it from there.
- A call to a function defined in the same F# file, or to a method in the same C# project, is
  followed. The warning points at the call and names the read.
- The analyzers know the reads listed above. They do not detect I/O in a body, a value read once
  into a module-level binding, a read through a library they do not know, or, in F#, a call to a
  function in another file.

[The body must be deterministic](../concepts/workflows.html#The-body-must-be-deterministic)
explains the rule the analyzers check.
