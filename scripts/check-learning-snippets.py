#!/usr/bin/env python3
"""Verify (or --update) tutorial excerpts against named regions in runnable samples."""
import re
import sys
import textwrap
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PATTERN = re.compile(r'(<!-- sample: (fsharp|csharp) ([\w.]+) ([\w-]+) -->\n```(?:fsharp|csharp)\n)(.*?)(\n```)', re.S)

def region(language, filename, name):
    source = (ROOT / f'samples/getting-started-{language}' / filename).read_text()
    body = source.split(f'// docs:{name}\n', 1)[1].split('// docs:end', 1)[0]
    return textwrap.dedent(body).strip()

errors = []
count = 0
for path in [ROOT / 'docs/get-started.fsx', *sorted((ROOT / 'docs/tutorial').glob('*'))]:
    if path.suffix not in {'.fsx', '.md'}:
        continue
    def replace(match):
        global count
        count += 1
        expected = region(match[2], match[3], match[4])
        if expected != match[5]:
            errors.append(f'{path.relative_to(ROOT)}: {match[3]} / {match[4]}')
        return match[1] + expected + match[6]
    text = path.read_text()
    updated = PATTERN.sub(replace, text)
    if '--update' in sys.argv and updated != text:
        path.write_text(updated)
print(f'Checked {count} sample excerpts.')
if errors and '--update' not in sys.argv:
    print('\n'.join(errors))
    sys.exit(1)
