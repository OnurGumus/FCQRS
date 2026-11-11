# FCQRS.Workflows Part 3 (Children, Cancellation, Versions) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A workflow starts other workflows and can wait for them, can be cancelled, and can change its code while instances are running.

**Architecture:** Starting and awaiting a child are attempts under a retry policy, like a command: the parent asks the child's shard region, and start-once makes a repeated start safe. Cancellation stores the request together with a `Cancelled` failure for every pending step, in one write, so replay needs no special case. A version step is stored under a key beside the path sequence, so adding one does not shift the paths of running instances.

**Tech Stack:** As part 1.

**Spec:** `design/specs/2026-11-10-fcqrs-workflows-design.md`

## Global Constraints

As parts 1 and 2, and:

- A child's ID is `<parent id>:<step path>`.
- A cancel request and the failures it causes are one journal write.
- A version step consumes no path number.

## Deviations from the spec

- `awaitChild` asks the child for its result and asks again after a timeout or a restart. The child does not notify its parent.
- Every child joins its parent's trace. A detached child does not start a trace of its own.
- A child that was started and not yet awaited when its parent is cancelled keeps running.
- A body that catches the cancellation and returns a value ends as `Finished`. It ends as `Cancelled` when the cancellation leaves the body unhandled.

## Review Focus

1. A parent that crashes between starting a child and storing the child's ID: the repeated start must reach the same child.
2. A cancel request for a workflow whose steps are all in parallel branches: every branch's pending step must fail, and cleanup must still run.
3. A `version` step added before steps that a running instance has passed: that instance must get version 1 and must not park.
4. A `version` step inside a loop: each iteration must store its own value.
5. Cancelling a parked instance: it must end, since it cannot run cleanup.

---

## Task 1: Version step

**Produces:** `val version: changeId: string -> latest: int -> Workflow<int>`

- Key: `<next path>@<changeId>`. A stored value is returned. With none stored, an instance whose journal already holds a step at or under the next path gets `1`; otherwise `latest` is stored and returned.

- [ ] Machine tests: a new instance stores and gets `latest`; an instance whose journal was written by the old code gets `1`, stores nothing, and finishes on the old branch; an instance that had not reached the point gets `latest`; a loop stores one value per iteration.
- [ ] Fail, implement, pass, commit.

## Task 2: Cancellation

**Produces:** `WorkflowHandle.Cancel: string -> Async<unit>`; `ErrorKind.Cancelled` failures; `WorkflowRecord.Cancelled`.

- [ ] Tests: a cancelled workflow blocked in an activity ends as `Cancelled`, and its activity's token is cancelled; a body that catches the failure runs a cleanup activity and then ends as `Cancelled` when it rethrows; pending steps in two parallel branches both fail; a cancelled workflow stays cancelled after a restart; cancelling a finished workflow changes nothing; cancelling an unknown ID raises `InvalidOperationException`.
- [ ] Fail, implement, pass, commit.

## Task 3: Child workflows

**Produces:**

```fsharp
type ChildHandle<'Result> = { Workflow: string; Id: string }
val startChild: WorkflowDefinition<'I, 'R> -> 'I -> Workflow<ChildHandle<'R>>
val awaitChild: ChildHandle<'R> -> Workflow<'R>
val child: WorkflowDefinition<'I, 'R> -> 'I -> Workflow<'R>
```

- [ ] Tests: a parent awaits a child's result; a child's fault raises `ChildFaulted` in the parent; a detached child finishes after its parent; a parent restarted while its child runs gets the child's result and the child ran once; cancelling the parent cancels the awaited child; a child whose workflow is not registered fails the step with a message that says so.
- [ ] Fail, implement, pass, commit.

## Task 4: Parking

- [ ] Test: an instance whose step was renamed reports `Parked` after a restart, runs nothing, resumes and finishes when the original code is registered again, and can be cancelled while parked.
- [ ] Pass, run the Release suite, commit.
