#!/usr/bin/env python3
"""Verify (or --update) tutorial and task-guide excerpts against named regions in runnable samples."""
import re
import sys
import textwrap
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PATTERN = re.compile(r'(<!-- sample: ([\w./-]+) ([\w.]+) ([\w-]+) -->\n```(?:fsharp|csharp)[^\n]*\n)(.*?)(^```[ \t]*$)', re.S | re.M)

# At desktop width, F# and C# frames show about 80 columns, and other code blocks, which sit in the
# text column, about 72. Longer lines scroll sideways.
FENCE = re.compile(r'^```(\w*)[^\n]*\n(.*?)^```[ \t]*$', re.S | re.M)
WIDTH = {'fsharp': 80, 'csharp': 80}
PLAIN_WIDTH = 72

# A marker names a folder under samples/, a file in it, and a region.
def region(sample, filename, name):
    source = (ROOT / 'samples' / sample / filename).read_text()
    body = source.split(f'// docs:{name}\n', 1)[1].split('// docs:end', 1)[0]
    return textwrap.dedent(body).strip()

errors = []
too_wide = []
count = 0
for path in [*sorted((ROOT / 'docs/how-to').glob('*')), *sorted((ROOT / 'docs/tutorial').glob('*'))]:
    if path.suffix not in {'.fsx', '.md'}:
        continue
    def replace(match):
        global count
        count += 1
        expected = region(match[2], match[3], match[4])
        if expected != match[5].rstrip('\n'):
            errors.append(f'{path.relative_to(ROOT)}: {match[3]} / {match[4]}')
        return match[1] + expected + '\n' + match[6]
    text = path.read_text()
    updated = PATTERN.sub(replace, text)
    if '--update' in sys.argv and updated != text:
        path.write_text(updated)
    # Task guides quote logs and shell commands that cannot wrap, so only their code is checked.
    for fence in FENCE.finditer(updated):
        if path.parent.name == 'tutorial' or fence[1] in WIDTH:
            limit = WIDTH.get(fence[1], PLAIN_WIDTH)
            for line in fence[2].splitlines():
                if len(line) > limit:
                    too_wide.append(f'{path.relative_to(ROOT)}: {fence[1] or "plain"} line over {limit} columns: {line.strip()}')
print(f'Checked {count} sample excerpts.')
if errors and '--update' not in sys.argv:
    print('\n'.join(errors))
if too_wide:
    print('\n'.join(too_wide))
if (errors and '--update' not in sys.argv) or too_wide:
    sys.exit(1)
