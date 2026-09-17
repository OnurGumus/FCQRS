#!/usr/bin/env python3
"""Require compiler tooltips on every visible F# guide example."""
import re
from html.parser import HTMLParser
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class Tooltips(HTMLParser):
    def __init__(self, source):
        super().__init__()
        self.divs = []
        self.setup = 0
        self.blocks = []
        self.current = None
        self.ids = set()
        self.references = set()
        self.feed(source)

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        classes = attrs.get('class', '').split()
        if 'id' in attrs:
            self.ids.add(attrs['id'])
        if 'data-fsdocs-tip' in attrs:
            self.references.add(attrs['data-fsdocs-tip'])
            if self.current is not None:
                self.current['tips'] += 1
        if tag == 'div':
            self.divs.append(classes)
        if tag == 'details':
            self.setup += 1
        if tag == 'code' and 'language-fsharp' in classes and not self.setup:
            self.current = {
                'semantic': any('livedocs-semantic-code' in c for c in self.divs),
                'tips': 0,
            }
            self.blocks.append(self.current)

    def handle_endtag(self, tag):
        if tag == 'div':
            self.divs.pop()
        if tag == 'details':
            self.setup -= 1
        if tag == 'code':
            self.current = None


def main():
    errors = []
    total = 0
    sources = list((ROOT / '.livedocs/content').rglob('*.md'))
    if not sources:
        raise SystemExit('No staged documentation; run scripts/build-docs.py first.')
    for source in sources:
        relative = source.relative_to(ROOT / '.livedocs/content').with_suffix('.html')
        relative = Path(*(re.sub(r'^\d+[._ -]*', '', part) for part in relative.parts))
        expected = sum('prepare' not in info.split() for info in
                       re.findall(r'^```fsharp([^\n]*)', source.read_text(), re.M))
        page = Tooltips((ROOT / 'output' / relative).read_text())
        total += expected
        if len(page.blocks) != expected:
            errors.append(f'{relative}: expected {expected} F# examples, found {len(page.blocks)}')
        for index, block in enumerate(page.blocks, 1):
            if not block['semantic'] or not block['tips']:
                errors.append(f'{relative}: F# example {index} has no compiler tooltips')
        missing = page.references - page.ids
        if missing:
            errors.append(f'{relative}: {len(missing)} tooltip targets are missing')
    if errors:
        raise SystemExit('\n'.join(errors))
    print(f'Checked compiler tooltips on all {total} visible F# examples.')


if __name__ == '__main__':
    main()
