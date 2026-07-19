---
title: Rebuild a read model
category: How-to
categoryindex: 5
index: 7
---

# Rebuild a read model

Rebuild a read model when a projection bug produced incorrect rows, a new query needs a different
shape, or an index must be recreated. The journal remains unchanged throughout the operation.

## Before rebuilding

Identify these four values:

- the projection handler and the event types it accepts;
- the read-model tables, index, or collection it owns;
- the stored offset used by that projection;
- the application queries that read the model.

Do not reuse one offset for independently deployed projections. Each consumer needs an offset that
describes its own progress.

## Rebuild in place

Use this sequence when queries can be unavailable during the rebuild:

1. stop the projection and any writers to its read-model tables;
2. clear or replace only the data owned by that projection;
3. reset that projection's offset to the beginning;
4. start the projection and let it replay the journal;
5. monitor errors and lag until it reaches the current journal position;
6. verify representative records and counts before restoring query traffic.

The offset update must remain in the same transaction as each read-model update. A crash during the
rebuild then retries the uncommitted event.

## Rebuild beside the live model

For a large model or one that must remain available:

1. create new tables or an index with a versioned name;
2. run a second projection with its own offset into the new destination;
3. let it replay and catch up while queries continue using the old model;
4. compare the old and new results;
5. switch queries to the new destination;
6. keep the old model until rollback is no longer required.

Events written during the rebuild are consumed as the new projection catches up. The cutover should
occur only after its offset reaches the live stream.

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
