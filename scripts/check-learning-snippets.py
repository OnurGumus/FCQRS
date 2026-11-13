#!/usr/bin/env python3
"""Verify (or --update) tutorial, task-guide and concept-page excerpts against named regions in runnable samples."""
import re
import sys
import textwrap
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PATTERN = re.compile(r'(<!-- sample: ([\w./-]+) ([\w.]+) ([\w-]+) -->\n```\w*[^\n]*\n)(.*?)(^```[ \t]*$)', re.S | re.M)

# At desktop width, F# and C# frames show about 80 columns, and other code blocks, which sit in the
# text column, about 72. Longer lines scroll sideways.
FENCE = re.compile(r'^```(\w*)[^\n]*\n(.*?)^```[ \t]*$', re.S | re.M)
WIDTH = {'fsharp': 80, 'csharp': 80}
PLAIN_WIDTH = 72

MARKER = re.compile(r'^[ \t]*// docs:[\w-]+\n', re.M)


# A marker names a folder under samples/, a file in it, and a region. The region `file` is the
# whole file without its region markers.
def region(sample, filename, name):
    source = (ROOT / 'samples' / sample / filename).read_text()
    if name == 'file':
        return MARKER.sub('', source).strip()
    body = source.split(f'// docs:{name}\n', 1)[1].split('// docs:end', 1)[0]
    return textwrap.dedent(body).strip()


# The workflow tutorial shows every line of its programs: each step's page quotes every region,
# and every project file, that is new or differs from the previous step.
ORDERS = ROOT / 'samples/orders'
SOURCE_SUFFIXES = {'.fs', '.cs'}
PROJECT_SUFFIXES = {'.fsproj', '.csproj'}


def units(folder):
    """A step folder's quotable parts as {(file, region): text}, and the source lines that are in no
    region."""
    result, outside = {}, []
    for file in sorted(folder.iterdir()):
        if file.suffix in PROJECT_SUFFIXES:
            result[(file.name, 'file')] = file.read_text().strip()
        elif file.suffix in SOURCE_SUFFIXES:
            current = None
            for number, line in enumerate(file.read_text().splitlines(), 1):
                marker = re.match(r'^\s*// docs:([\w-]+)$', line)
                if marker and marker[1] == 'end':
                    current = None
                elif marker:
                    current = marker[1]
                    result[(file.name, current)] = region(str(folder.relative_to(ROOT / 'samples')), file.name, current)
                elif current is None and line.strip():
                    outside.append(f'{file.relative_to(ROOT)}:{number} is in no docs: region')
    return result, outside


def orders_rules(quoted):
    """Errors for program lines that no page shows and for changes that their step's page does not show."""
    problems = []
    if not ORDERS.exists():
        return problems
    if ('orders', 'global.json', 'file') not in quoted:
        problems.append('samples/orders/global.json is quoted on no page')
    steps = sorted((step for step in ORDERS.iterdir() if re.match(r'\d+-', step.name)),
                   key=lambda step: int(step.name.split('-')[0]))
    for language in ['fsharp', 'csharp']:
        previous = {}
        for step in steps:
            folder = step / language
            current, outside = units(folder)
            problems += outside
            sample = f'orders/{step.name}/{language}'
            for (filename, name), text in current.items():
                if previous.get((filename, name)) != text and (sample, filename, name) not in quoted:
                    problems.append(f'{sample} {filename} {name} is new or changed but its page does not show it')
            previous = current
    return problems

errors = []
too_wide = []
quoted = set()
count = 0
# Concept pages are checked only where they quote a sample.
PAGES = [*sorted((ROOT / 'docs/how-to').glob('*')), *sorted((ROOT / 'docs/tutorial').glob('*')),
         *sorted((ROOT / 'docs/workflow-tutorial').glob('*')),
         *(page for page in sorted((ROOT / 'docs/concepts').glob('*.md')) if '<!-- sample:' in page.read_text())]
for path in PAGES:
    if path.suffix not in {'.fsx', '.md'}:
        continue
    def replace(match):
        global count
        count += 1
        quoted.add((match[2], match[3], match[4]))
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
        # A concept page's F# examples are not sample excerpts and keep their own width.
        if path.parent.name == 'concepts' and fence[1] != 'csharp':
            continue
        # The workflow tutorial's text blocks show program output, which may hold a long line.
        if path.parent.name == 'workflow-tutorial' and fence[1] == 'text':
            continue
        if path.parent.name in {'tutorial', 'workflow-tutorial'} or fence[1] in WIDTH:
            limit = WIDTH.get(fence[1], PLAIN_WIDTH)
            for line in fence[2].splitlines():
                if len(line) > limit:
                    too_wide.append(f'{path.relative_to(ROOT)}: {fence[1] or "plain"} line over {limit} columns: {line.strip()}')
errors += orders_rules(quoted)
print(f'Checked {count} sample excerpts.')
if errors and '--update' not in sys.argv:
    print('\n'.join(errors))
if too_wide:
    print('\n'.join(too_wide))
if (errors and '--update' not in sys.argv) or too_wide:
    sys.exit(1)
