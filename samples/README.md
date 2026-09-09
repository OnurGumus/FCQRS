# Samples

Start with [registration in F#](registration-fsharp/) or [registration in C#](registration-csharp/).
Both use stable .NET 10 and the published FCQRS package. Run twice to see recovery and duplicate handling.

Then try the optional HTTP API in [F#](registration-http-fsharp/) or [C#](registration-http-csharp/).
It reuses the account rule and exposes registration and query endpoints.

The older `getting-started-*` document-store projects and fixtures remain for persisted-compatibility
checks. They are not part of the beginner guide.
