#!/usr/bin/env python3
"""Check local HTML links, assets, and fragments in the generated documentation."""
import sys
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urlsplit

ROOT = Path(__file__).resolve().parents[1] / 'output'


class Page(HTMLParser):
    def __init__(self, path):
        super().__init__()
        self.ids = set()
        self.links = []
        self.redirect = False
        self.feed(path.read_text())

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if 'id' in attrs:
            self.ids.add(attrs['id'])
        if tag == 'a' and 'name' in attrs:
            self.ids.add(attrs['name'])
        for name in ('href', 'src'):
            if attrs.get(name):
                self.links.append(attrs[name])
        if tag == 'meta' and attrs.get('http-equiv', '').lower() == 'refresh':
            self.redirect = True
            self.links.append(attrs['content'].split('url=', 1)[1].strip())


def main():
    pages = {path.resolve(): Page(path) for path in ROOT.rglob('*.html')}
    if not pages:
        raise SystemExit('No generated documentation; run scripts/build-docs.py first.')
    errors = set()
    for path, page in pages.items():
        for link in page.links:
            url = urlsplit(link)
            if url.scheme or url.netloc:
                continue
            target = ((ROOT / unquote(url.path).lstrip('/')) if url.path.startswith('/') else
                      (path.parent / unquote(url.path)) if url.path else path).resolve()
            if target.is_dir():
                target /= 'index.html'
            if not target.is_file():
                errors.add(f'{path.relative_to(ROOT)}: missing {link}')
            elif url.fragment and target in pages and not pages[target].redirect:
                if unquote(url.fragment) not in pages[target].ids:
                    errors.add(f'{path.relative_to(ROOT)}: missing fragment {link}')
    if errors:
        print('\n'.join(sorted(errors)))
        return 1
    print(f'Checked local links and fragments in {len(pages)} HTML pages.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
