---
title: Rebuild a read model
category: Apply
categoryindex: 4
index: 10
---

# Rebuild a read model

Rebuild a read model when a projection bug produced incorrect rows, a new query needs a different
shape, or an index must be recreated. The journal remains unchanged throughout the operation. The
examples use the statement from the tutorial's [show a statement](../tutorial/show-a-statement.html)
step, a transactional projection named `Statement` that writes the `statement` table.

If the rebuild needs [event upcasters](evolve-events.html), register the complete conversion chain
before initializing the projection. Both FCQRS projection styles apply the registered chain to
journal events; rows already processed before that registration are not converted in place.

## Before rebuilding

Identify these four values:

- the projection handler and the event types it accepts;
- the read-model tables, index, or collection it owns;
- the progress stored for that projection: its rows in `fcqrs_projection_progress` for a transactional
  projection, or its offset row for an [offset-based projection](add-a-projection.html);
- the application queries that read the model.

Do not reuse one projection name or offset for independently deployed projections. Each consumer needs
progress that describes its own work.

## Rebuild in place

Use this sequence when queries can be unavailable during the rebuild:

1. stop the projection and any writers to its read-model tables;
2. clear or replace only the data owned by that projection;
3. reset that projection's progress to the beginning;
4. start the projection and let it replay the journal;
5. monitor errors and lag until it reaches the current journal position;
6. verify representative records and counts before restoring query traffic.

For the statement, steps 2 and 3 are two statements:

```sql
DELETE FROM statement;
DELETE FROM fcqrs_projection_progress
WHERE projection_name = 'Statement';
```

For an offset-based projection, set its offset row back to `0` instead.

The progress update must remain in the same transaction as each read-model update. A transactional
projection does this itself. A crash during the rebuild then retries the uncommitted event.

## Rebuild beside the live model

For a large model or one that must remain available:

1. create new tables or an index with a versioned name, such as `statement_v2`;
2. run a second projection with its own name or offset into the new destination, **in a separate
   process or deployment**: one FCQRS host supports one projection, and a second `AddProjection` or
   `AddTransactionalProjection` call throws `InvalidOperationException` at registration;
3. let it replay and catch up while queries continue using the old model;
4. compare the old and new results;
5. switch queries to the new destination;
6. keep the old model until rollback is no longer required.

Events written during the rebuild are consumed as the new projection catches up. The cutover should
occur only after it reaches the live stream. For a transactional projection, `CatchUpAsync` confirms
that it has handled every event committed before the call.

The tutorial's [add a memo](../tutorial/add-a-memo.html) step uses the first half of this sequence:
the projection `StatementV2` fills `statement_v2` from the first event, and the old `statement` table
stays as it is. That step replaces the old projection in the same host rather than running both.

## When replay fails

Stop at the first failing event. Record its offset, type, correlation id, and exception. Then determine
whether the problem is:

- a projection bug;
- an event shape the current code cannot deserialize;
- an invariant that older history legitimately does not satisfy;
- unavailable read-model storage.

Do not skip an event merely to advance the offset. A skipped event makes every later result suspect.
Correct the handler or compatibility code, reset to a known good offset, and resume.

See [Add a projection](add-a-projection.html) for the handler and transaction pattern, and
[Evolve persisted events](evolve-events.html) for old event shapes.
