#!/usr/bin/env python3
"""Run each workflow tutorial step from a clean copy and check the output its page shows."""
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
    """Replace the values that differ on every run with the placeholders the pages show."""
    output = re.sub(r'[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}', '<guid>', output)
    return re.sub(r'\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z?', '<time>', output)


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
            shutil.copy2(file, project / file.name)
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


STEPS = {'1-place-an-order': place_an_order,
         '2-retry-the-warehouse': retry_the_warehouse}

# Optional arguments select steps by folder name, for example: 2-retry-the-warehouse
selected = sys.argv[1:] or list(STEPS)
unknown = [step for step in selected if step not in STEPS]
assert not unknown, f'unknown steps: {unknown}'

for step in selected:
    with tempfile.TemporaryDirectory(prefix='fcqrs-orders-') as temp:
        STEPS[step](Path(temp))
        print(f'{step} passed (F# and C#).', flush=True)
