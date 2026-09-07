---
title: Learning path
category: Learn FCQRS
categoryindex: 2
index: 1
---

# Learn FCQRS by building a document store

Save a document, try to create it twice, restart it, edit it, and publish it under a unique URL slug.
Each result introduces the next part of FCQRS through something you can run and inspect.

You need Git, basic F# or C#, and the .NET 10 SDK selected by `global.json`. Both language paths use
stable .NET 10 throughout. No previous experience with CQRS, event sourcing, actors, or sagas is needed.

**[Start here: save your first document](../get-started.html).** Choose one language and keep the same
project, document ID, and SQLite database as you move through the course. The later commands are
already included in the runnable sample; each chapter activates and explains one more part.

| Stage | Experiment | What the result explains |
|---|---|---|
| [0. Save your first document](../get-started.html) | Save a document and read it back. | Command, event, projection, and query. |
| [1. Make one document decision](1-the-aggregate.html) | Create the same document with different content. | State, rules, and replies that do not add events. |
| [2. Restart, project, and query](2-running-it.html) | Reuse its ID in a new process. | Recovery and waiting for query data. |
| [3. Edit your document](3-edit-your-document.html) | Edit, repeat, and restart again. | New event cases and unchanged-content replies. |
| [4. Publish under a unique URL](3-adding-a-saga.html) | Publish two documents under the same slug. | Independent owners and a durable saga. |
| [5. Test changes and recovery](4-testing-and-evolution.html) | Pause a workflow and recover it. | Retry safety, serialized contracts, and old histories. |
| [6. Preparing for production](5-production.html) | Prepare for failures and deployment. | Storage, diagnostics, backups, and recovery rehearsals. |

Predict a result before running a command, compare it with the output, then follow the explanation.
Python 3 is optional for running the complete repository smoke test in stage 5.

## Look up a topic when you need it

- [Understand](../concepts/index.html) explains a model or guarantee in more depth.
- [Apply](../how-to/index.html) gives instructions for a specific implementation task.
- [Configuration](../configuration.html) and the API reference describe settings and calls.

These are supporting references. Begin with [your first document](../get-started.html) and follow the
next-page link at the end of each stage.
