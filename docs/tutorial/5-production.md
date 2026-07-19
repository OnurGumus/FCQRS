---
title: 5. Preparing for production
category: Tutorial
categoryindex: 3
index: 6
---

# 5. Preparing for production

The domain model does not change when the application moves from a laptop to production. The operating
environment does. Production readiness means deciding what happens when storage is slow, a projection
fails, an external service accepts a request twice, or a node stops during a workflow.

> **Learn alongside this chapter:** use [Consistency and recovery](../concepts/consistency-and-recovery.html)
> as the failure model, then keep [Observe your system](../how-to/observability.html) and the
> [configuration reference](../configuration.html) open while turning the checklist into deployment
> settings and alerts.

## 1. Put the journal on durable storage

SQLite is suitable for local development and a single-process deployment. A multi-node cluster needs a
database reachable by every node. Choose a supported provider, provision it as business-critical data,
and test restore procedures. The journal is the source from which aggregate state and read models are
recovered.

Back up the journal and snapshot store according to the database provider's guidance. A snapshot only
shortens replay; losing snapshots does not lose business history. Losing journal events does.

See [Configure the database](../how-to/configure-the-database.html).

## 2. Make every projection restart-safe

Commit the read-model update and its offset in the same database transaction whenever they share a
store. If a projection writes to a remote index or several databases, use idempotent writes and record
enough information to resume safely. “The handler is called again” must be a supported case.

Monitor projection failures and lag. A command may be safely stored while a query remains stale because
its projection is stopped. Read-your-writes subscriptions coordinate a caller with a running
projection; they do not repair a failed one.

See [Add a projection](../how-to/add-a-projection.html) and
[Read your writes](../how-to/read-your-writes.html).

## 3. Design workflows for partial failure

An external service and the saga journal do not share a transaction. The service may accept a request
just before the process loses its connection or stops. Retrying can therefore repeat the request.

> **Motivation:** Durable state solves only the part of a failure that happened before the last commit.
> Production design must make the uncertain work after that boundary observable, repeatable, or safe to
> resolve manually.

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
- make an external dependency time out and confirm retry and terminal-failure behaviour;
- break a projection and confirm the failure is visible, then rebuild its read model;
- delete snapshots and confirm full replay reaches the same state;
- restore the database backup and verify representative aggregates and projections;
- send concurrent commands to one aggregate and verify the domain outcome;
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

## Continue learning while operating

- **Understand:** return to [The read side](../concepts/read-models.html),
  [Sagas](../concepts/sagas.html), and
  [Consistency and recovery](../concepts/consistency-and-recovery.html) when a production symptom crosses
  more than one durability boundary.
- **Apply:** the [how-to guides](../how-to/index.html) are the working reference after this course.
  Start with [Configure the database](../how-to/configure-the-database.html),
  [Observe your system](../how-to/observability.html), and
  [Rebuild a read model](../how-to/rebuild-a-read-model.html). The
  [configuration reference](../configuration.html) lists the FCQRS and Akka.NET settings used by the
  runtime.
