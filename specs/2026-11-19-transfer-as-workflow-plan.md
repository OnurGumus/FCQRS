# Tutorial step 5 as a workflow: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this
> plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The CQRS tutorial's transfer becomes a workflow that the application starts; sagas become
the older mechanism, still runnable and tested.

**Architecture:** Samples and docs only. The step 5 account answers every repeated transfer command
with its first verdict; a `Transfer` workflow sends `SendTransfer`, `ReceiveTransfer`, and on an
explicit rejection `RefundTransfer`. Today's saga moves to `samples/accounts/transfer-with-a-saga`
and links step 5's account.

**Tech Stack:** F# and C# 15 on .NET 11, FCQRS 7.3.0, FCQRS.Workflows 7.13.0 (published), SQLite.

**Spec:** `specs/2026-11-19-transfer-as-workflow-design.md`

## Global Constraints

- Accounts samples reference `FCQRS` 7.3.0; steps 5 and 6 also `FCQRS.Workflows` 7.13.0.
- Steps 5 and 6 keep `<AssemblyName>Accounts</AssemblyName>`.
- Workflow name `Transfer`; workflow ID = transfer ID (`t1`, `t2`, `t3`).
- Result union `Completed | Refunded | Refused of reason` (C#: union of records).
- F# and C# output identical; CLAUDE.md documentation style.
- Nothing committed or released without the user asking.

## Review Focus

- Second run on the same database: `Start` stores nothing for `t1`/`t2`; the program reports the
  stored result without moving money. Pinned by `check-accounts.py` second-run expectations.
- A repeated `SendTransfer` after its balance was spent must answer `TransferSent`, not
  "Insufficient funds". Pinned by a Facade test on step 5's `decide`.
- A process kill during a transfer neither creates nor loses money. Pinned by
  `WorkflowBankInvariantTests` on step 5's code.
- The saga sample still runs with the shared account. Pinned by a `check-accounts.py` entry.
- Step 6 reads step 5's database. Pinned by `add_a_memo` in `check-accounts.py`.

---

### Task 1: Step 5 account and workflow (F#), with tests

**Files:** modify `samples/accounts/5-transfer-money/fsharp/{Account.fs,Transfer.fs,Program.fs,*.fsproj}`;
modify `test/Facade.Tests/{Facade.Tests.fsproj,WorkflowBankInvariantTests.fs}`; add a `decide`
repeat test to an existing Facade test file.

**Produces:** `Transfer.TransferRequest = { Source: string; Target: string; Amount: decimal }`,
`Transfer.TransferOutcome = Completed | Refunded | Refused of reason: string`,
`Transfer.definition: AggregateHandle<AccountCommand, AccountEvent> -> WorkflowDefinition<TransferRequest, TransferOutcome>`.

- [ ] `decide`: `SendTransfer(id, ...)` with `state.Sent.Contains id` → `DeferEvent(TransferSent ...)`,
      matched before the amount and balance rules.
- [ ] Test: open, deposit 50, send t1 of 50, repeat t1 → reply `TransferSent`, balance 0.
- [ ] Workflow body per the spec; `Program` starts t1, t2 via `Start`, waits with `Result`, catches
      the statement up, repeats `ReceiveTransfer t1`, prints statements.
- [ ] `WorkflowBankInvariantTests` uses step 5's `Account` and `Transfer` instead of the Vault.
- [ ] Run the Facade suite in Release; `BankInvariantTests` fixed in Task 3.

### Task 2: Step 5 in C#

**Files:** `samples/accounts/5-transfer-money/csharp/{Account.cs,Transfer.cs,Program.cs,*.csproj}`.

- [ ] Same rule, `TransferWorkflow : Workflow<TransferRequest, TransferOutcome>` named `Transfer`,
      registered with `AddWorkflow`; output identical to F#.

### Task 3: Saga sample kept

**Files:** create `samples/accounts/transfer-with-a-saga/{fsharp,csharp}` from the old step 5
(`git show HEAD:...`), module `TransferSaga`, account linked from step 5; README; update
`Facade.Tests.fsproj` and `BankInvariantTests.fs`; repoint `write-a-saga.md` sample markers.

- [ ] Both programs run; Facade suite passes including both kill tests.

### Task 4: Step 6 and all package versions

**Files:** `samples/accounts/6-add-a-memo/**`, `samples/accounts/{1..4}*/**/*proj`,
`samples/accounts/serve-over-http/**/*proj`, every `docs/tutorial/*.fsx` `#r` line.

- [ ] Step 6 adds `Memo: string option` to `TransferRequest` and `TransferSent`; starts t3.
- [ ] `check-accounts.py`: new step 5/6 expectations, saga sample entry; all steps pass.

### Task 5: Pages

**Files:** `docs/tutorial/transfer-money.fsx`, `docs/tutorial/add-a-memo.fsx`,
`docs/concepts/sagas.md`, `docs/how-to/write-a-saga.md`, new
`docs/how-to/start-a-workflow-from-an-event.md` (+ index row, renumbering), `docs/overview.fsx`,
`docs/concepts/{correlation-ids,process-termination}.md`,
`docs/how-to/{dispatch-async-effects,observability,serve-over-http}.md`,
`samples/accounts/README.md`, step READMEs, `README.md`, `CLAUDE.md`.

- [ ] Step 5 page sections per the spec; saga pages open with the "older mechanism" paragraph.
- [ ] `python3 scripts/build-docs.py` passes; `git diff --check` clean.
