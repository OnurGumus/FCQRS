; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
FCQRS001 | FCQRS | Warning | A workflow body reads the clock, a GUID generator, a random source, or the environment
FCQRS002 | FCQRS | Warning | An aggregate's fold reads the clock, a GUID generator, a random source, or the environment
