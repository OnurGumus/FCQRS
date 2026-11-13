#!/usr/bin/env python3
"""Run each workflow tutorial step from a clean copy, check its output, and check that its page's
"Run it" blocks show that output."""
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SAMPLES = ROOT / 'samples/orders'
# The tutorial's programs need the .NET 11 SDK for C# 15; this file selects it.
GLOBAL_JSON = SAMPLES / 'global.json'
LANGUAGES = ['fsharp', 'csharp']


def normalise(output):
    """Replace the values that differ on every run, GUIDs and times, with the `…` the pages show."""
    output = re.sub(r'[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}', '…', output)
    return re.sub(r'\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?', '…', output)


def run(project, *args, expect_exit=0):
    """Build the project, run it, and return its normalised output lines."""
    # A C# switch or F# match that misses a case fails the check.
    build = subprocess.run(['dotnet', 'build', '-warnaserror:CS8509;FS0025'], cwd=project, text=True,
                           stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=600)
    assert build.returncode == 0, build.stdout
    result = subprocess.run(['dotnet', 'run', '--no-build', '--', *args], cwd=project, text=True,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=600)
    assert result.returncode == expect_exit, f'exit {result.returncode}, expected {expect_exit}\n{result.stdout}'
    return normalise(result.stdout).rstrip('\n').splitlines()


def copy(root, step, language):
    """Copy a step's program into root/<language>. A later step copied into the same folder replaces
    the sources and keeps bin/, so it runs on the earlier step's database."""
    source = SAMPLES / step / language
    project = Path(root) / language
    project.mkdir(parents=True, exist_ok=True)
    for file in project.iterdir():
        if file.is_file():
            file.unlink()
    for file in source.iterdir():
        if file.is_file() and file.suffix in {'.fs', '.cs', '.fsproj', '.csproj'}:
            # A fresh modification time makes the build compile a later step's sources over an
            # earlier step's output.
            shutil.copy(file, project / file.name)
    shutil.copy2(GLOBAL_JSON, project / 'global.json')
    return project


def same(actual, expected, output):
    assert actual == expected, ('expected:\n' + '\n'.join(expected) + '\nactual:\n' + '\n'.join(actual)
                                + '\noutput:\n' + '\n'.join(output))


def runs(root, steps):
    """Run a list of (step, args, expected exit) in one folder per language and return, per run,
    the output, after checking that F# and C# printed the same lines."""
    outputs = {language: [] for language in LANGUAGES}
    for language in LANGUAGES:
        for step, args, exit_code in steps:
            project = copy(root, step, language)
            outputs[language].append(run(project, *args, expect_exit=exit_code))
    for fsharp, csharp in zip(outputs['fsharp'], outputs['csharp']):
        same(csharp, fsharp, csharp)
    return outputs['fsharp']


def place_an_order(root):
    first, second = runs(root, [('1-place-an-order', [], 0), ('1-place-an-order', [], 0)])
    same(first, ['Started o-1',
                 '  warehouse reserved 2 widget (key order/o-1/0)',
                 'o-1 finished: reservation r-1',
                 'o-1 is already started (Completed)',
                 'Journal of order/default-shard/o-1:',
                 '  1 Started',
                 '  2 StepScheduled',
                 '  3 StepCompleted',
                 '  4 Finished'], first)
    # The second run finds o-1 in the journal: nothing is stored and the warehouse is not called.
    same(second, ['o-1 is already started (Completed)'] + first[2:], second)
    return [first, second]


JOURNAL = ['Journal of order/default-shard/o-1:',
           '  1 Started',
           '  2 StepScheduled',
           '  3 StepCompleted',
           '  4 Finished']


def retry_the_warehouse(root):
    [output] = runs(root, [('2-retry-the-warehouse', [], 0)])
    # Two failed attempts and one that succeeds, all with the step's key; only the result is stored.
    same(output, ['Started o-1',
                  '  warehouse busy (key order/o-1/0)',
                  '  warehouse busy (key order/o-1/0)',
                  '  warehouse reserved 2 widget (key order/o-1/0)',
                  'o-1 finished: reservation r-1',
                  'o-1 is already started (Completed)'] + JOURNAL, output)
    return [output]


RESERVE = ['  warehouse busy (key order/o-1/0)',
           '  warehouse busy (key order/o-1/0)',
           '  warehouse reserved 2 widget (key order/o-1/0)']


def read_the_clock(root):
    [output] = runs(root, [('3-read-the-clock', [], 0)])
    same(output, ['Started o-1'] + RESERVE +
                 ['o-1 status: Running ["3 sleep"]',
                  '  carrier shipped r-1 (key order/o-1/4)',
                  'o-1 finished: reservation r-1, shipment s-1',
                  '  confirmation …, placed …',
                  'o-1 is already started (Completed)',
                  'Journal of order/default-shard/o-1:',
                  '  1 Started',
                  '  2 StepScheduled',
                  '  3 StepCompleted',
                  '  4 StepCompleted',
                  '  5 StepCompleted',
                  '  6 StepScheduled',
                  '  7 StepCompleted',
                  '  8 StepScheduled',
                  '  9 StepCompleted',
                  '  10 Finished'], output)
    return [output]


def reserve(id, quantity, number):
    key = f'order/{id}/0'
    return [f'  warehouse busy (key {key})', f'  warehouse busy (key {key})',
            f'  warehouse reserved {quantity} widget (key {key})']


# The journal of o-1 after it ran five steps.
SHIPPED_JOURNAL = ['Journal of order/default-shard/o-1:'] + [
    f'  {number} {record}' for number, record in enumerate(
        ['Started', 'StepScheduled', 'StepCompleted', 'StepCompleted', 'StepCompleted', 'StepScheduled',
         'StepCompleted', 'StepScheduled', 'StepCompleted', 'Finished'], 1)]


def wait_for_approval(root):
    [output] = runs(root, [('4-wait-for-approval', [], 0)])
    # o-2's signal is sent right after its start, before the body waits for it, and is kept.
    # o-3 gets no signal; after three seconds its wait ends without one.
    same(output, ['Started o-1'] + reserve('o-1', 2, 1) +
                 ['  carrier shipped r-1 (key order/o-1/4)',
                  'o-1 finished: shipped as s-1',
                  'Started o-2'] + reserve('o-2', 30, 2) +
                 ['  carrier shipped r-2 (key order/o-2/5)',
                  'o-2 finished: shipped as s-2',
                  'Started o-3'] + reserve('o-3', 50, 3) +
                 ['o-3 finished: not approved'] + SHIPPED_JOURNAL, output)
    return [output]


def restart_during_shipping(root):
    step = '5-restart-during-shipping'
    first, second = runs(root, [(step, ['--crash'], 1), (step, [], 0)])
    # The first run stops after the carrier shipped and before FCQRS stored the step's result.
    same(first, ['Started o-1'] + reserve('o-1', 2, 1) +
                ['  carrier shipped r-1 (key order/o-1/4)',
                 '  the process stops before this step is stored'], first)
    # The second run resumes o-1: the warehouse is not called again, and ship runs again with the
    # same key.
    same(second, ['o-1 is already started (Running ["4 ship"])',
                  '  carrier shipped r-1 (key order/o-1/4)',
                  'o-1 finished: shipped as s-1'] + SHIPPED_JOURNAL, second)
    return [first, second]


def charge_the_customer(root):
    [output] = runs(root, [('6-charge-the-customer', [], 0)])
    same(output,
         ['Deposited 1500 to ada', 'Deposited 50 to bob', 'Started o-1'] + reserve('o-1', 2, 1) +
         ['  carrier shipped r-1 (key order/o-1/5)',
          'o-1 finished: shipped as s-1',
          'Started o-2'] + reserve('o-2', 30, 2) +
         ['  carrier shipped r-2 (key order/o-2/6)',
          'o-2 finished: shipped as s-2',
          # Not approved: the stock is released.
          'Started o-3'] + reserve('o-3', 50, 3) +
         ['  warehouse released r-3 (key order/o-3/4)',
          'o-3 finished: not approved',
          # Bob has 50 and the order costs 80: the account refuses the debit.
          'Started o-4'] + reserve('o-4', 2, 4) +
         ['  warehouse released r-4 (key order/o-4/4)',
          'o-4 finished: payment refused',
          # o-1's journal has the send step's records, 6 and 7.
          'Journal of order/default-shard/o-1:'] +
         [f'  {number} {record}' for number, record in enumerate(
             ['Started', 'StepScheduled', 'StepCompleted', 'StepCompleted', 'StepCompleted',
              'StepScheduled', 'StepCompleted', 'StepScheduled', 'StepCompleted', 'StepScheduled',
              'StepCompleted', 'Finished'], 1)], output)
    return [output]


def no_gizmo(id):
    return [f'  warehouse has no gizmo (key order/{id}/0)'] * 5


def when_a_step_fails(root):
    [output] = runs(root, [('7-when-a-step-fails', [], 0)])
    same(output,
         ['Deposited 1500 to ada', 'Deposited 50 to bob',
          # The body catches reserve-stock's failure and returns a result.
          'Started o-5'] + no_gizmo('o-5') +
         ['o-5 finished: out of stock',
          # Nothing catches ship's failure, so the order faults.
          'Started o-6'] + reserve('o-6', 1, 1)[:2] +
         ['  warehouse reserved 1 crate (key order/o-6/0)'] +
         ['  carrier does not take crates (key order/o-6/5)'] * 3 +
         ['o-6 faulted: The carrier does not take crates',
          # o-7 is left waiting for its approval; step 8 finds it running.
          'Started o-7'] + reserve('o-7', 30, 1) +
         ['o-7 status: Running ["3 approval"]',
          # o-6 was charged (records 6 and 7) before ship failed (10 and 11).
          'Journal of order/default-shard/o-6:'] +
         [f'  {number} {record}' for number, record in enumerate(
             ['Started', 'StepScheduled', 'StepCompleted', 'StepCompleted', 'StepCompleted',
              'StepScheduled', 'StepCompleted', 'StepScheduled', 'StepCompleted', 'StepScheduled',
              'StepFailed', 'Faulted'], 1)], output)
    return [output]


def add_a_step(root):
    # Step 8 runs on the database step 7 left, with o-7 waiting for its approval.
    _, output = runs(root, [('7-when-a-step-fails', [], 0), ('8-add-a-step', [], 0)])
    # The new step stands where o-7's journal holds the approval wait, so o-7 is parked.
    same(output,
         ["o-7 is parked: The workflow body asks for Activity 'check-fraud' at step 3, "
          "but the journal holds Signal 'approval'.",
          'Sent approval to o-7',
          'Started o-8'] + reserve('o-8', 2, 1) +
         ['  fraud check passed for ada (key order/o-8/3)',
          '  carrier shipped r-1 (key order/o-8/6)',
          'o-8 finished: shipped as s-1',
          'Journal of order/default-shard/o-7:'] +
         [f'  {number} {record}' for number, record in enumerate(
             ['Started', 'StepScheduled', 'StepCompleted', 'StepCompleted', 'StepCompleted',
              'StepScheduled', 'SignalReceived'], 1)], output)
    return [output]


def guard_the_change(root):
    # Step 9 runs on the database steps 7 and 8 left: o-7 is parked with its approval stored.
    _, _, output = runs(root, [('7-when-a-step-fails', [], 0), ('8-add-a-step', [], 0),
                               ('9-guard-the-change', [], 0)])
    # o-7 resumes without the fraud check, uses the approval stored while it was parked, and ships.
    # o-9 is new and runs the fraud check.
    same(output,
         ['  carrier shipped r-2 (key order/o-7/6)',
          'o-7 finished: shipped as s-1',
          'Started o-9'] + reserve('o-9', 2, 1) +
         ['  fraud check passed for ada (key order/o-9/3)',
          '  carrier shipped r-1 (key order/o-9/6)',
          'o-9 finished: shipped as s-2',
          # Record 7 is the approval o-7 got while parked; record 8 completes its wait. The
          # version step stored nothing for o-7.
          'Journal of order/default-shard/o-7:'] +
         [f'  {number} {record}' for number, record in enumerate(
             ['Started', 'StepScheduled', 'StepCompleted', 'StepCompleted', 'StepCompleted',
              'StepScheduled', 'SignalReceived', 'StepCompleted', 'StepScheduled', 'StepCompleted',
              'StepScheduled', 'StepCompleted', 'StepScheduled', 'StepCompleted', 'Finished'], 1)],
         output)
    return [output]


STEPS = {'1-place-an-order': place_an_order,
         '2-retry-the-warehouse': retry_the_warehouse,
         '3-read-the-clock': read_the_clock,
         '4-wait-for-approval': wait_for_approval,
         '5-restart-during-shipping': restart_during_shipping,
         '6-charge-the-customer': charge_the_customer,
         '7-when-a-step-fails': when_a_step_fails,
         '8-add-a-step': add_a_step,
         '9-guard-the-change': guard_the_change}

# Optional arguments select steps by folder name, for example: 2-retry-the-warehouse
selected = sys.argv[1:] or list(STEPS)
unknown = [step for step in selected if step not in STEPS]
assert not unknown, f'unknown steps: {unknown}'

def page_outputs(step):
    """The program output a step's page shows: the text blocks in its "Run it" sections that are
    not commands to run."""
    page = ROOT / 'docs/workflow-tutorial' / (step.split('-', 1)[1] + '.fsx')
    sections = re.split(r'^(?=## )', page.read_text(), flags=re.M)
    runs_shown = ''.join(section for section in sections if section.startswith('## Run it'))
    blocks = re.findall(r'^```text\n(.*?)\n```', runs_shown, re.S | re.M)
    return [block.splitlines() for block in blocks
            if not all(line.startswith('dotnet ') for line in block.splitlines())]


for step in selected:
    with tempfile.TemporaryDirectory(prefix='fcqrs-orders-') as temp:
        outputs = STEPS[step](Path(temp))
    shown = page_outputs(step)
    assert len(shown) == len(outputs), f'{step}: the page shows {len(shown)} outputs, the check ran {len(outputs)}'
    for printed, page in zip(outputs, shown):
        same(page, printed, printed)
    print(f'{step} passed (F# and C#, and its page).', flush=True)
