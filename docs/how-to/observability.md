---
title: Observe your system
category: Apply
categoryindex: 4
index: 13
---

# Observe your system

FCQRS reports command, event, saga, dispatch, and projection activity through `ILogger` and
`ActivitySource`. Configure both before production so one correlation id can be followed across the
complete workflow.

## Message-flow logs

At `Information` level, the `FCQRS.MessageFlow` category records aggregate decisions, persisted event
versions, saga transitions, and commands issued by sagas. Every line contains the correlation id.
These lines are the first transfer from the tutorial's [transfer money](../tutorial/transfer-money.html)
step, one correlation id from Alice's command to Bob's stored event:

```text
info: FCQRS.MessageFlow[0]
      Command SendTransfer ("t1", "bob", 30M) to aggregate alice (v2) yielded PersistEvent (TransferSent ("t1", "bob", 30M)) [cid: 01a0cf1b-8485-72e5-9e41-6011435b56bf]
info: FCQRS.MessageFlow[0]
      Aggregate alice persisted event TransferSent ("t1", "bob", 30M) (v3) [cid: 01a0cf1b-8485-72e5-9e41-6011435b56bf]
info: FCQRS.MessageFlow[0]
      Saga alice~Saga~01a0cf1b-8485-72e5-9e41-6011435b56bf changed state to Delivering [cid: 01a0cf1b-8485-72e5-9e41-6011435b56bf]
info: FCQRS.MessageFlow[0]
      Saga alice~Saga~01a0cf1b-8485-72e5-9e41-6011435b56bf sent command ReceiveTransfer ("t1", "alice", 30M) to bob [cid: 01a0cf1b-8485-72e5-9e41-6011435b56bf]
info: FCQRS.MessageFlow[0]
      Aggregate bob persisted event TransferReceived ("t1", "alice", 30M) (v2) [cid: 01a0cf1b-8485-72e5-9e41-6011435b56bf]
info: FCQRS.MessageFlow[0]
      Saga alice~Saga~01a0cf1b-8485-72e5-9e41-6011435b56bf changed state to Completed [cid: 01a0cf1b-8485-72e5-9e41-6011435b56bf]
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
// "FCQRS", "FCQRS.Saga", and "FCQRS.Query"
tracing.AddSource(FCQRS.Common.Telemetry.AllActivitySources);
```

`ActivitySource` avoids creating activities when no listener is attached. Restart-detection aborts and
fatal errors set span status to `Error`.

Start an activity at the application boundary before constructing the first command. The resulting
trace should contain the initial command, stored event, saga states, follow-up commands, and projection
handler. The CID remains a domain correlation value; trace context travels beside it in metadata.

### Span names are low-cardinality

Span names contain the case name, such as `Command:SendTransfer`, `Event:TransferSent`,
`Saga:Delivering`, or `Abort:TransferSent`. Payload values do not appear in the span name,
so trace backends can group operations without creating one name per entity. On .NET 11, tracing rules
can select a source and operation:

```csharp
using Microsoft.Extensions.Diagnostics.Tracing;

builder.Services.AddTracing(tracing =>
{
    tracing.EnableTracing(sourceName: "FCQRS.Saga");
    tracing.DisableTracing(
        sourceName: "FCQRS", operationName: "Command:HealthPing");
});
```

Payload detail may still appear in tags and logs as described below.

## Keep payloads out of diagnostics

Rendered payloads appear in span tags and message-flow logs by default. Disable them before processing
sensitive values when detailed payload diagnostics are not acceptable:

```fsharp
FCQRS.Common.Telemetry.IncludePayloads <- false
```

<div class="cs-alt"></div>

```csharp
builder.WithPayloadDiagnostics(false);
```

Tags and log lines then contain the case name only. This switch affects diagnostics, not persisted
events. A secret stored in an event remains in the journal regardless of the diagnostics setting.

## Flush telemetry on a fatal exit

FCQRS terminates the process when a fold, aggregate handler, saga handler, effect runner, or projection
handler fails in a way that could leave state processing inconsistent. It also terminates when a
payload cannot be serialized or the journal rejects an event.
[When FCQRS stops the process](../concepts/process-termination.html) lists every case and explains why.
Fail-fast skips normal finalizer and process-exit flushing. Register a bounded flush hook for buffered
telemetry:

```fsharp
FCQRS.Common.Telemetry.FatalFlush <- System.Action(fun () ->
    tracerProvider.ForceFlush(3000) |> ignore
    // With Serilog, call Log.CloseAndFlush() instead.
    loggerProvider.ForceFlush(3000) |> ignore)
```

<div class="cs-alt"></div>

```csharp
FCQRS.Common.Telemetry.FatalFlush = new Action(() =>
{
    tracerProvider.ForceFlush(3000);
    // With Serilog, call Serilog.Log.CloseAndFlush() instead.
    loggerProvider.ForceFlush(3000);
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
