# Orders workflow tutorial samples

Each folder is one step of the workflow tutorial, in F# and C#. Every step is a complete program
that uses the published `FCQRS.Workflows` package and stores its journal in a SQLite file beside
the executable. The tutorial's pages show every line of each program.

The programs target .NET 11, and the C# programs use C# 15 unions from step 6. This folder's
`global.json` selects the .NET 11 SDK; run the programs from here or from a step's folder.
`Directory.Build.props` keeps the repository's build settings out, so the programs build as they
would in a folder of your own.

1. [Place an order](1-place-an-order/): a workflow, an activity, a repeated start, and the journal.
2. [Retry the warehouse](2-retry-the-warehouse/): retry policies and idempotency keys.
3. [Read the clock](3-read-the-clock/): `now`, `newGuid`, a timer, and a shipping step.
4. [Wait for approval](4-wait-for-approval/): a signal with a timeout, and a signal sent early.
5. [Restart during shipping](5-restart-during-shipping/): run with `--crash`, then again, to see
   replay.
6. [Charge the customer](6-charge-the-customer/): a command to an account aggregate, and releasing
   the stock when the order does not ship.
7. [When a step fails for good](7-when-a-step-fails/): a caught failure and a faulted order. It
   leaves `o-7` waiting for step 8.
8. [Add a step](8-add-a-step/): a new step parks `o-7`. It continues from step 7's database.
9. [Guard the change](9-guard-the-change/): `version` lets `o-7` resume. It continues from step 8's
   database.
10. [Feed orders from a stream](10-feed-orders-from-a-stream/): `WorkflowFlow` starts five orders,
    two at a time. Run with `--stop`, then again, to see the orders resume and the stream not.

Steps 8 and 9 continue from the previous step's database, so their projects share the assembly name
`Orders`. `python3 scripts/check-orders.py` runs every step and checks its output.
