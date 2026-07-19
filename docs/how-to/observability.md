---
title: Observe your system
category: How-to
categoryindex: 5
index: 11
---

# Observe your system

FCQRS reports command, event, saga, dispatch, and projection activity through `ILogger` and
`ActivitySource`. Configure both before production so one correlation id can be followed across the
complete workflow.

## Message-flow logs

At `Information` level, the `FCQRS.MessageFlow` category records aggregate decisions, persisted event
versions, saga transitions, and commands issued by sagas. Every line contains the correlation id:

```text
info: FCQRS.MessageFlow
      Command Publish (doc-42, "guides/fcqrs") to aggregate doc-42 yielded PersistEvent (PublicationRequested ...) [cid: ...]
info: FCQRS.MessageFlow
      Aggregate doc-42 persisted event PublicationRequested (...) (v2) [cid: ...]
info: FCQRS.MessageFlow
      Saga doc-42~PublicationSaga~... changed state to ReservingSlug [cid: ...]
info: FCQRS.MessageFlow
      Saga doc-42~PublicationSaga~... sent command Reserve (...) to guides/fcqrs [cid: ...]
```

Disable the process-wide narrative with
`FCQRS.Common.Telemetry.MessageFlowLogging <- false` or
`builder.WithMessageFlowLogging(false)`. Standard logger filtering also applies:

```json
{
  "Logging": {
    "LogLevel": {
      "FCQRS.MessageFlow": "None"
    }
  }
}
```

When the category is disabled, FCQRS skips formatting the message payload.

## Distributed traces

Aggregates, sagas, and projections use three activity sources. A W3C `traceparent` is copied into
command metadata and carried through later events and saga commands. Register all three sources:

```csharp
tracing.AddSource(FCQRS.Common.Telemetry.AllActivitySources); // "FCQRS", "FCQRS.Saga", "FCQRS.Query"
```

`ActivitySource` avoids creating activities when no listener is attached. Restart-detection aborts and
fatal errors set span status to `Error`.

Start an activity at the application boundary before constructing the first command. The resulting
trace should contain the initial command, stored event, saga states, follow-up commands, and projection
handler. The CID remains a domain correlation value; trace context travels beside it in metadata.

### Span names are low-cardinality

Span names contain the case name, such as `Command:Register`, `Event:Registered`,
`Saga:GeneratingCode`, or `Abort:VerificationRequested`. Payload values do not appear in the span name,
so trace backends can group operations without creating one name per entity. On .NET 11, tracing rules
can select a source and operation:

```csharp
builder.Services.AddTracing(tracing =>
{
    tracing.EnableTracing(sourceName: "FCQRS.Saga");
    tracing.DisableTracing(sourceName: "FCQRS", operationName: "Command:HealthPing");
});
```

Payload detail may still appear in tags and logs as described below.

## Keep payloads out of diagnostics

Rendered payloads appear in span tags and message-flow logs by default. Disable them before processing
sensitive values when detailed payload diagnostics are not acceptable:

```fsharp
FCQRS.Common.Telemetry.IncludePayloads <- false   // or builder.WithPayloadDiagnostics(false)
```

<div class="cs-alt"></div>

```csharp
builder.WithPayloadDiagnostics(false);
```

Tags and log lines then contain the case name only. This switch affects diagnostics, not persisted
events. A secret stored in an event remains in the journal regardless of the diagnostics setting.

## Flush telemetry on a fatal exit

FCQRS terminates the process when a fold, aggregate handler, saga handler, effect runner, or projection
handler fails in a way that could leave state processing inconsistent. Fail-fast skips normal finalizer
and process-exit flushing. Register a bounded flush hook for buffered telemetry:

```fsharp
FCQRS.Common.Telemetry.FatalFlush <- System.Action(fun () ->
    tracerProvider.ForceFlush(3000) |> ignore
    loggerProvider.ForceFlush(3000) |> ignore) // or Serilog's Log.CloseAndFlush()
```

<div class="cs-alt"></div>

```csharp
FCQRS.Common.Telemetry.FatalFlush = new Action(() =>
{
    tracerProvider.ForceFlush(3000);
    loggerProvider.ForceFlush(3000); // or Serilog.Log.CloseAndFlush()
});
```

The hook runs on a background thread with a five-second cap.

## Alerts to add

At minimum, alert on:

- process fail-fast and repeated restarts;
- projection handler failure and growing projection lag;
- a saga remaining in one state beyond its domain timeout;
- unreachable cluster members and shard movement that does not settle;
- journal or snapshot storage latency and errors;
- exhausted retries or workflows sent to manual intervention.

FCQRS supplies the message and trace context. Domain timeouts, projection-lag metrics, and
manual-intervention counters belong to the application because only it knows the expected duration and
business impact.

## Akka's own logging

Akka.NET internal logging defaults to `OFF`; FCQRS logs still use the application's `ILoggerFactory`.
Enable Akka.NET internals with `builder.WithAkkaLogging(AkkaLogLevel.Info)`
from the hosting builder, or set `config:akka:loglevel` in configuration. See
[Configuration](../configuration.html) for the config-key details.
