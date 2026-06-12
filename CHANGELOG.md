# Changelog

## 6.0.0-preview23
- Notification buffer config hardening: the queue takes
  config:akka:fcqrs:notification-buffer verbatim (any positive size),
  while the BroadcastHub - which requires a power-of-two buffer - gets
  the value rounded down to a power of two, clamped to [8, 4096].
  Previously a non-power-of-two setting crashed stream materialization.

## 6.0.0-preview22
- **Complete delayed/self side-effect helpers**: the saga's scheduled-command
  concept (ExecuteCommand.DelayInMs) is now reachable for every target on both
  surfaces — F#: `toSelf`, `toSelfAfter`, `toAggregateAfter`, `toActorAfter`
  (joining `toOriginatorAfter`); C#: `SagaCommands.ToSelf`, `ToSelfDelayed`,
  `ToAggregateDelayed`, `ToActorDelayed` (joining `ToOriginatorDelayed`).
  `toSelfAfter` is the idiomatic saga timeout: enter a state, schedule a
  reminder to yourself, and HandleEvent decides whether it still matters.

## 6.0.0-preview21

### Snapshots
- **`SnapshotPolicy`** (`Default` / `NoSnapshots` / `Every n`): per-aggregate and
  per-saga snapshot cadence, set on the F# definition record (`Snapshots = ...`)
  or by overriding the virtual `SnapshotPolicy` property on the C# base classes.
- **`WithDefaultSnapshotPolicy(...)`** on the C# host builder: what `Default`
  resolves to for every entity it registers. Resolution: entity override →
  builder default → `config:akka:persistence:snapshot-version-count` → 30.
- **`PersistAndSnapshot`**: persist an event and save a manual snapshot
  checkpoint once it is durable, independent of cadence.

### Events
- **`PersistAllEvents` / `EventActions.PersistAll` / `persistAll`**: several
  events from one command persisted as a single journal `AtomicWrite` —
  all-or-nothing, sequential versions, nothing published until the whole batch
  is durable (preview20).

### Logging & telemetry
- **`WithAkkaLogging(AkkaLogLevel...)`**: enable Akka's internal logging
  (shipped OFF) from the fluent builder, no HOCON editing.
- **Telemetry rebuilt**: trace context now rides `Metadata["traceparent"]`
  (stamped automatically from `Activity.Current` at command creation) and flows
  command → events → sagas → the saga's commands → projections. New
  `FCQRS.Query` ActivitySource closes traces end-to-end; all span sites are
  gated on `HasListeners()` (zero overhead when off); CIDs stay plain GUIDs.
  Register with `tracing.AddSource(Telemetry.AllActivitySources)`.

### Journal manifests
- **Stable logical type names**: register payload types once
  (`Fcqrs.journalTypes [ journalType<Document.Event> "doc.event" ]` /
  `.WithJournalTypes(m => m.Type<DocumentEvent>("doc.event"))`) and journal
  manifests become `fcqrs:ev(doc.event)` instead of CLR
  AssemblyQualifiedNames — CLR types can then be renamed or moved freely;
  only the mapping changes and old rows keep deserializing
  (`JournalTypes.Remap` for deliberate re-pointing, aliases supported).
  Pre-existing journals and unregistered types keep using AQN manifests via a
  read-side fallback: no migration needed, ever.

### Reliability
- Read-your-writes notification queue: overflow now drops the oldest
  unconsumed notification (`DropHead`) instead of faulting the stream;
  buffer size via `config:akka:fcqrs:notification-buffer` (default 1024).

## 6.0.0-preview19
- **Saga snapshots carry the starting event**: a saga recovered through a
  snapshot used to wake with no starting event and silently skip its recovery
  re-drive (pending commands never re-issued). Snapshots now persist it;
  old-shape snapshots still load and re-drive with degraded metadata.

## 6.0.0-preview18
- **Read side self-heals**: a journal-read error used to silently COMPLETE the
  projection stream (frozen read models in a healthy-looking process); now
  `RestartSource` with backoff, resuming from the last processed offset.
- **One crash policy**: aggregates' `HandleCommand`/`ApplyEvent` and the
  serializer (both directions) now FailFast on error like sagas already did,
  instead of quiet actor restarts / silently stopped entities.

## 6.0.0-preview17
- **Saga-start handshake deadlock closed**: a saga resurrected mid-handshake
  (transient persist failure + remember-entities) never re-signaled the
  SagaStarter while the originator stayed parked in an unbounded ask — a
  permanent process-local deadlock. Recovery now re-signals Continue, and the
  handshake ask is bounded (default 30s, `config:akka:fcqrs:saga-start-timeout`)
  with FailFast on expiry.
