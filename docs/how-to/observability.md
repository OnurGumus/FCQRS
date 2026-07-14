---
title: Observe your system
category: How-to
categoryindex: 5
index: 8
---

# Observe your system

FCQRS gives you the command → event → saga story two ways out of the box: a plain-text **message-flow
log** that needs no setup, and **distributed traces** that plug into OpenTelemetry. Both are on the
standard .NET abstractions (`ILogger`, `ActivitySource`), so they flow through whatever you already
use. This guide covers what you get, how to tune it, and how to keep sensitive data out.

## The message-flow log (on by default)

FCQRS narrates every message at `Information` level — no tracing pipeline required. Each aggregate
command logs the effect it yielded, each persisted event logs its new version, and sagas log every
event they pick up, every state transition, and every command they send (with target and delay).
Every line carries the correlation ID, so grepping one CID replays a whole workflow:

```text
info: FCQRS.MessageFlow
      Command Register ("alice", "pw") to aggregate alice (v0) yielded PersistEvent (VerificationRequested ...) [cid: 00-...]
info: FCQRS.MessageFlow
      Aggregate alice persisted event VerificationRequested ("alice", "pw") (v1) [cid: 00-...]
info: FCQRS.MessageFlow
      Saga alice~Saga~00-... received VerificationRequested ("alice", "pw"), decided StateChangedEvent (UserDefined GeneratingCode) [cid: 00-...]
info: FCQRS.MessageFlow
      Saga alice~Saga~00-... changed state to GeneratingCode [cid: 00-...]
info: FCQRS.MessageFlow
      Saga alice~Saga~00-... sent command SetVerificationCode "327470" to alice [cid: 00-...]
```

These are your application's messages, not FCQRS internals, so the switch lives in FCQRS — turn it
off process-wide with `FCQRS.Common.Telemetry.MessageFlowLogging <- false`, or from the C# hosting
builder with `builder.WithMessageFlowLogging(false)`. Every line goes to the dedicated
**`FCQRS.MessageFlow`** logger category, so standard log filtering also works — e.g.
`"Logging:LogLevel:FCQRS.MessageFlow": "None"` in `appsettings.json`. When the category is filtered
out (or the switch is off) the lines are never even formatted, so it costs nothing.

## Distributed traces

Aggregates, sagas, and the projection each emit through an `ActivitySource`, and spans parent onto
whatever trace was current when the command was created (the W3C `traceparent` rides the command's
metadata through the whole flow). Register all three sources with your OpenTelemetry pipeline in one
call:

```csharp
tracing.AddSource(FCQRS.Common.Telemetry.AllActivitySources); // "FCQRS", "FCQRS.Saga", "FCQRS.Query"
```

Spans cost nothing until a listener is attached, so leaving this unwired is free. Restart-detection
aborts and fatal errors light up as `Error`-status spans, so failed flows are findable without tag
filters.

### Span names are low-cardinality

Span names are the **case name only** — `Command:Register`, `Event:Registered`,
`Saga:GeneratingCode`, `Abort:VerificationRequested` — never the payload values. That is deliberate:
it lets trace viewers group and compute latency by operation, and on .NET 11 it lets the rule-based
`AddTracing` API enable or disable a specific FCQRS operation from configuration, no redeploy:

```csharp
builder.Services.AddTracing(tracing =>
{
    tracing.EnableTracing(sourceName: "FCQRS.Saga");
    tracing.DisableTracing(sourceName: "FCQRS", operationName: "Command:HealthPing");
});
```

It also means no payload value is ever written into an indexed span name.

## Keep payloads out of diagnostics

The full rendered payload rides in the span **tags** (`command.type` / `event.type`) and in the
message-flow log lines, and is **on by default** — the detail that makes the flow log useful in
development. For sensitive domains, turn it off:

```fsharp
FCQRS.Common.Telemetry.IncludePayloads <- false   // or builder.WithPayloadDiagnostics(false)
```

Tags and log lines then carry the case name only, matching the span name. Span **names** are
low-cardinality regardless of this switch, so tracing rules and grouping are unaffected either way.
(Best practice still applies: don't put secrets in command/event fields — an event journal keeps them
forever. This switch protects the diagnostics surface, not the journal.)

## Flush telemetry on a fatal exit

On an unrecoverable inconsistency (a fold or handler that throws) FCQRS fail-fasts the process to
avoid silent state divergence — which skips finalizers, so anything still buffered in a batch span
exporter or log sink is lost, including the fatal flow's own span. Give it a flush hook to drain your
pipeline first:

```fsharp
FCQRS.Common.Telemetry.FatalFlush <- System.Action(fun () ->
    tracerProvider.ForceFlush(3000) |> ignore
    loggerProvider.ForceFlush(3000) |> ignore) // or Serilog's Log.CloseAndFlush()
```

It runs on a background thread with a 5-second cap, so a hung exporter can't block the kill.

## Akka's own logging

Akka.NET's internal logging ships **off** (it is chatty); FCQRS's own logs go through your
`ILoggerFactory` regardless. To see Akka internals, use `builder.WithAkkaLogging(AkkaLogLevel.Info)`
from the hosting builder, or set `config:akka:loglevel` in configuration. See
[Configuration](../configuration.html) for the config-key details.
