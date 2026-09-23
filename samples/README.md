# Samples

Start with the [accounts tutorial](accounts/). Each step is a complete program in F# and C# that uses
the published FCQRS package on .NET 11. The C# programs use C# 15.

The task guides quote the same programs. [accounts/serve-over-http](accounts/serve-over-http/) puts
step 4's account and statement behind ASP.NET Core endpoints.

The registration samples, [F#](registration-fsharp/) and [C#](registration-csharp/), are older .NET 10
programs that CI still runs. The [C# interop page](../docs/concepts/csharp-interop.md) links the C# one as a .NET 10
example.

The older `getting-started-*` document-store projects and fixtures remain for persisted-compatibility
checks. They are not part of the beginner guide.
