# Tutorial step 5 as a workflow: design

## Intent

Sagas solve cross-aggregate coordination, but they are hard to write and hard to reason about: a
start handshake inside the aggregate actor, state machines spread over `handleEvent` and
`applySideEffects`, `StayExpecting`, `ExpectationExhausted`. A workflow started by the application
does the same job with less: the start is one stored record, the aggregate only answers commands,
and the process reads top to bottom.

So the CQRS tutorial's transfer step teaches a workflow, and sagas become the older mechanism:
fully supported for existing applications and their stored journals, no longer recommended for new
code.

## Goals

- Step 5 of the CQRS tutorial ("Transfer money") moves money with a workflow that the application
  starts, in F# and C#, semantically equivalent.
- Step 6 ("Add a memo") continues from step 5's database with the workflow version.
- The saga material stays correct and runnable, presented as the older mechanism.
- A reader who needs an aggregate event to start a workflow finds how.

## Non-goals

- No change to FCQRS's code or public API. Sagas are not deprecated with `[<Obsolete>]`.
- No release. The docs and samples change; packages do not.
- No saga that starts a workflow. A saga's side effects are aggregate commands only; starting a
  workflow from a saga would be a new feature.
- No rewrite of the workflow tutorial. Step 5 introduces only what the transfer needs and links
  there for the rest.

## Anti-goals

- Step 5 must not become a second workflow tutorial.
- No page may claim a workflow gives exactly-once effects or that sagas are removed.
- The account must not reject a repeated `SendTransfer`: with a workflow that loses money (taken,
  never delivered, because the workflow reads the rejection as a refusal).

## Constraints

- `samples/accounts/` targets .NET 11 with the published packages. Step 5 and 6 move from FCQRS
  6.14.0 to the current core 7.x and FCQRS.Workflows 7.13.0.
- Step 5 and step 6 both build an assembly named `Accounts`; step 6 reads step 5's database.
- The CLAUDE.md documentation rules: example first, guarantee at its boundary, failure mode, next
  link; F# and C# equivalent; no em dashes as structure.
- `scripts/check-accounts.py` checks the samples' output; `scripts/build-docs.py` checks excerpts
  and links.

## Approaches

- **A. Replace step 5's saga with a workflow (chosen).** The tutorial teaches the recommended way;
  the saga moves to a sample of its own that the saga pages quote.
- B. Keep step 5 as a saga and add a workflow step 7. Less churn, but readers learn sagas first,
  which is what this change is meant to stop.
- C. Drop transfers from the CQRS tutorial and point to the workflow tutorial. Breaks step 6, which
  continues from step 5's database, and leaves the CQRS track with nothing that crosses aggregates.

## Design

### Step 5 sample (`samples/accounts/5-transfer-money/{fsharp,csharp}`)

- `Account`: commands and events unchanged (`SendTransfer`, `ReceiveTransfer`, `RefundTransfer`,
  `TransferSent`, ...). One rule changes: a repeated `SendTransfer` answers with its first verdict,
  `DeferEvent(TransferSent ...)`, and that check comes before the balance check, because the first
  send lowered the balance. This mirrors `ReceiveTransfer` and `RefundTransfer`, so the "safe to
  repeat" lesson now covers all three commands with one rule.
- `Transfer`: a workflow named `Transfer` with input `{ Source; Target; Amount }` and the workflow
  ID as the transfer ID. Body:
  1. `send` `SendTransfer(id, target, amount)` to the source. A `Rejected` reply ends the transfer
     with `Refused reason`.
  2. `send` `ReceiveTransfer(id, source, amount)` to the target. `TransferReceived` ends it with
     `Completed`.
  3. Only on an explicit `Rejected` reply from the target, `send` `RefundTransfer(id, target,
     amount)` to the source and end with `Refunded`.

  The result is a union `Completed | Refunded | Refused of reason`.

  An unknown outcome is never treated as a rejection (my call). When `send` gives up without a reply
  (`OutcomeUnknown`, after `Retry.command`'s five minutes), the step fails and the workflow faults;
  it does not refund, because the target may have taken the money. The page shows `Status` reporting
  the fault and `Resume` re-running that step with the same command ID, which the account answers
  with its first verdict. This replaces the saga's endless resend with a visible stop that an
  operator or a scheduled job answers. The same holds for steps 1 and 3.
- `Program`: registers the account, the statement projection, and the workflow. `transfer id target
  amount` calls `Start id input`, then `Result id timeout`, then `statement.CatchUpAsync()` before the
  statement is read. It runs t1 (alice to bob, completes) and t2 (alice to carol, who has no
  account, refunded), repeats `ReceiveTransfer t1` by hand as now, and prints both statements.
- Packages: FCQRS and FCQRS.Workflows at current versions.

### Package versions across the tutorial (my call)

Every step, 1 to 6, and `serve-over-http` move to the current core package together, so a reader
who builds one bank never upgrades mid-tutorial. Steps 5 and 6 add FCQRS.Workflows. Each page's
`#r "nuget: ..."` line and `check-accounts.py` follow. Steps 1 to 4 change only where core 7's API
requires it.

### Step 5 page (`docs/tutorial/transfer-money.fsx`)

Sections, in order:

1. The CRUD version of the feature (kept).
2. Add the transfer commands and events (kept).
3. Make each transfer command safe to repeat: now all three, with the reason being that a workflow
   sends a command again after a restart.
4. Write the transfer as a workflow: the body, `send`, and what a stored step result means for
   recovery. Defines only workflow, step, and workflow ID.
5. Register the workflow and start a transfer: the application starts it; the ID makes a repeated
   start store nothing; `Result` waits for the end; the statement catches up separately.
6. Deliver a transfer twice (kept, reworded).
7. Run it, run it again.
8. What remains the application's job: stable transfer IDs per request, idempotent command
   handling, no exactly-once external effects.
9. A short note: earlier FCQRS versions did this with a saga, linking the saga page.
10. Next: step 6, and the workflow tutorial for timeouts, signals, and parallel steps.

### Step 6 (`samples/accounts/6-add-a-memo/`, `docs/tutorial/add-a-memo.fsx`)

The memo becomes an optional field of the workflow input and of `TransferSent`. Old events read it
as missing, as the page shows today. The page makes no claim about old workflow inputs, which
step 6 never reads back (my call: drop the claim rather than add output to prove it).

### The saga, kept (`samples/accounts/transfer-with-a-saga/{fsharp,csharp}`)

Today's step 5 saga moves here, with package versions and README updated. Its `Account` is step
5's file, linked rather than copied: the new repeat rule (a repeated `SendTransfer` answers
`TransferSent` without storing it) is also correct for the saga, because a deferred event starts no
saga. Its saga module is renamed `TransferSaga` (the `module` line is outside every excerpt), so
the test project can compile it beside step 5's `Transfer`.

`docs/how-to/write-a-saga.md`, `docs/concepts/sagas.md`, and the other pages that quote step 5's
saga excerpts point here.

### Kill tests (my call)

Both mechanisms stay under a kill test, on the tutorial's own code:

- `BankInvariantTests.fs` compiles step 5's `Account.fs` and the saga folder's `TransferSaga`.
- `WorkflowBankInvariantTests.fs` drops its `Vault` aggregate and compiles step 5's `Account.fs`
  and workflow `Transfer.fs`, so the code that carries the money-loss risk is the code under test.

### Saga pages

`concepts/sagas.md` and `how-to/write-a-saga.md` open with one paragraph: sagas are the older
mechanism, supported for existing applications and stored journals; new code uses a workflow that
the application starts, linking step 5. `overview.fsx`, `concepts/index.md`, `how-to/index.md`,
and the README stop presenting sagas as the default.

### New how-to: start a workflow from an aggregate event

For the case no single request causes, such as starting collections when an account goes
overdrawn. One option (my call): a projection whose handler calls `Start`, then optionally
`Signal`, as the reservations sample does. It starts only for stored events, retries after a
restart, and costs one poll interval of delay. The workflow ID comes from the event: the
persistence ID and sequence number, or a business key.

The page says why it does not use `RunAsync`: the runner's follow-up command and the workflow's
first command reach the aggregate by different paths with no order between them, and a rejected
follow-up lets a later event reuse the same derived ID. Both lose starts without any crash.

### Checks

- `check-accounts.py` updated for the new output and the new saga folder.
- Docs build passes; `git diff --check` clean.
- Facade suite passes, including `BankInvariantTests` on the moved saga sample.
- CLAUDE.md's learning-path and architecture map updated.

## Left to the builder

- Exact wording and excerpt boundaries.
- Whether `Program` prints the workflow's result per transfer.
- Retry policies: `send`'s default (`Retry.command`) unless the page needs a shorter one for the
  demo.
