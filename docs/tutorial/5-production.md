---
title: 6. Preparing for production
category: Learn FCQRS
categoryindex: 2
index: 8
---

# 6. Preparing for production

DocStore now saves documents, reserves slugs, and recovers interrupted publication. Before deploying
it, decide how to retain those histories, rebuild its query data, and find workflows that need help.

Use the same two owners throughout this review: a document owns its content and publication status;
a slug owns its reservation. The publication saga coordinates their separate decisions.

## 1. Put the journal on durable storage

SQLite is suitable for local development and a single-process deployment. A multi-node cluster needs a
database reachable by every node. Choose a supported provider, provision it as business-critical data,
and test restore procedures. The journal is the source from which aggregate state and read models are
recovered.

Back up the journal and snapshot store according to the database provider's guidance. A snapshot only
shortens replay; losing snapshots does not lose business history. Losing journal events does.

See [Configure the database](../how-to/configure-the-database.html).

## 2. Make every projection restart-safe

DocStore rebuilds its dictionary from offset zero on every startup. A durable document index needs
to retain both its query data and its progress through the journal.

Commit the read-model update and its offset in the same database transaction whenever they share a
store. Separate writes fail in one of two ways after a crash: an offset committed first skips an
event forever, and data committed first applies an event twice.
If a projection writes to a remote index or several databases, use idempotent writes and record
enough information to resume safely. “The handler is called again” must be a supported case.

Monitor projection failures and lag. A command may be safely stored while a query remains stale because
its projection is stopped. Read-your-writes subscriptions coordinate a caller with a running
projection; they do not repair a failed one.

See [Add a projection](../how-to/add-a-projection.html) and
[Read your writes](../how-to/read-your-writes.html).

## 3. Design workflows for partial failure

An external service and the saga journal do not share a transaction. The service may accept a request
just before the process loses its connection or stops. Retrying can therefore repeat the request.

In DocStore, `ReservationUncertain` and `ReportUncertain` retain an unknown outcome after the retry
deadline. Expose these states in an operational view. A person investigating one needs the document
ID, slug, last recorded step, and the other aggregate's observed outcome. Reconcile those facts before
issuing a retry or compensation; a delayed original answer can still arrive.

For every external command, decide:

- the timeout;
- which failures are retryable;
- the backoff policy;
- the idempotency key sent to the service;
- whether compensation is possible;
- what requires human intervention.

Use the FCQRS correlation id as a tracing link. Use a domain-specific idempotency key when the remote
operation needs deduplication.

## 4. Set snapshot policy from recovery measurements

The default snapshot interval is 30 persisted events. A smaller interval writes snapshots more often
and replays fewer events. A larger interval reduces snapshot writes and increases recovery work. Measure
recovery for representative large aggregates before changing it.

Snapshots do not replace events, and they do not make an impure fold safe. The recovered result must be
the same with or without a snapshot.

Passivation decides how often that recovery happens. An aggregate idle for
`akka.cluster.sharding.passivate-idle-entity-after` (`120s` by default) is stopped, and its next
command replays. Aggregate types differ here, so the timeout can be set per type under the entity name in
configuration, or as `Passivation` on the definition when it is a property of the domain. Measure
frequently edited documents separately from slugs that are reserved once and rarely read. [Configuration](../configuration.html) shows each form.

## 5. Configure diagnostics before an incident

Send FCQRS logs through the host's `ILoggerFactory` and register its `ActivitySource` names with
OpenTelemetry. Confirm that one correlation id connects the initial command, persisted events, saga
transitions, follow-up commands, and projection work.

Payload diagnostics are useful in development but may contain personal or secret data. Disable payload
rendering in sensitive environments and never put secrets in events unless the journal is designed to
store them permanently.

See [Observe your system](../how-to/observability.html).

## 6. Move to several nodes only when the single-node system is understood

FCQRS starts a one-node Akka.NET cluster by default. A multi-node deployment requires stable node
addresses, seed-node discovery, shared durable storage, compatible message contracts, and operational
monitoring for Akka.NET cluster and sharding state. The aggregate and saga definitions remain the same.

Test a rolling deployment with old and new nodes active together. Commands, events, and saga state must
serialize across both versions.

## 7. Rehearse failure

Before release, run these exercises in a non-production environment:

- stop the process after an event is persisted and confirm aggregate recovery;
- stop it while a saga is waiting and confirm the workflow resumes safely;
- make a dependency time out and confirm retries, escalation with an unknown outcome, and late-answer handling;
- break a projection and confirm the failure is visible, then rebuild its read model;
- delete snapshots and confirm full replay reaches the same state;
- restore the database backup and verify representative aggregates and projections;
- send concurrent commands to one aggregate and verify the domain outcome;
- start sagas from many aggregate instances at once, at your expected peak, and confirm every workflow
  completes; concurrent starts consume threads, so this differs from loading one aggregate (see
  [Configuration](../configuration.html));
- restart one cluster node and confirm entity routing continues.

## Completion checklist

You are ready to use FCQRS effectively when you can answer these questions for your application:

- What does each aggregate own, and which rules cross aggregate boundaries?
- Which replies are persisted facts and which are deferred answers?
- Can every fold replay deterministically?
- Is every retryable command idempotent at the business level?
- Where is each projection's offset stored, and how is that read model rebuilt?
- Which projection must a caller wait for before querying?
- What happens when every external dependency times out?
- Which event shapes must remain compatible during deployment?
- How are the journal, snapshots, and read models backed up and restored?
- Which logs, traces, and alerts reveal a stuck saga or failed projection?

## Choose the next implementation task

Use the supporting references for the task your application needs next:

- Use [Understand](../concepts/index.html) when you need to reason more deeply about a guarantee,
  recovery boundary, or design choice.
- Use [Apply](../how-to/index.html) when implementing a specific task in your own application.
- Use the [configuration reference](../configuration.html) when choosing exact runtime settings.

For a first real deployment, begin with [Configure the database](../how-to/configure-the-database.html)
and [Observe your system](../how-to/observability.html), then return to this completion checklist before
release.
