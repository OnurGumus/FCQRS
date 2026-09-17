#!/usr/bin/env python3
"""Build FsLiveDocs from the Markdown and runnable literate scripts in docs/."""
import argparse
import html
import json
import re
import shutil
import subprocess
import textwrap
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DOCS = ROOT / 'docs'
CONTENT = ROOT / '.livedocs/content'
OUTPUT = ROOT / 'output'
CONFIG = json.loads((ROOT / '.livedocs/config.json').read_text())
PROJECTS = [str(ROOT / project) for project in CONFIG['projects']]
CONTEXTS = ROOT / '.livedocs/contexts'
EXAMPLES_PROJECT = ROOT / '.livedocs/examples/Examples.fsproj'
VERSION = ET.parse(PROJECTS[0]).findtext('.//Version')


def run(*command):
    print('+', ' '.join(command), flush=True)
    subprocess.run(command, cwd=ROOT, check=True)


def literate_markdown(source, name):
    """Render our literate subset; executable .fsx files remain the source of truth."""
    directives = re.findall(r'\(\*\*\*.*?\*\*\*\)', source, re.S)
    if any(directive != '(*** hide ***)' for directive in directives):
        raise ValueError(f'{name}: unsupported literate directive; extend the converter explicitly.')
    parts = re.split(r'(\(\*\*\* hide \*\*\*\)|\(\*\*(?!\*)(?:.|\n)*?\*\))', source)
    result = []
    hidden = False
    for part in parts:
        if part == '(*** hide ***)':
            hidden = True
        elif part.startswith('(**'):
            result.append(part[3:-2].strip())
            hidden = False
        elif part.strip() and not hidden:
            if '(***' in part:
                raise ValueError('Unsupported literate directive; extend the converter explicitly.')
            result.append('```fsharp\n' + part.strip() + '\n```')
    return '\n\n'.join(result) + '\n'


def compiler_context(body, relative):
    """Assemble page fragments with checked setup, without copying the excerpts."""
    context = (CONTEXTS / relative).with_suffix('.fs')
    if not context.exists():
        return body
    template = context.read_text()
    def include(match):
        source = (ROOT / match[1]).read_text()
        source = re.sub(r'^// docs:.*\n', '', source, flags=re.M)
        # Sample files use a file-scoped module; a page uses nested modules.
        module = re.search(r'^module (\w+)\n', source, re.M)
        if module:
            source = 'module ' + module[1] + ' =\n' + textwrap.indent(source[module.end():].lstrip(), '    ')
        return source.rstrip()
    template = re.sub(r'^// include: (.+)$', include, template, flags=re.M)
    markers = list(re.finditer(r'^( *)// snippet: (\d+)( module)?\n', template, re.M))
    fences = list(re.finditer(r'^```fsharp[^\n]*\n(.*?)^```', body, re.M | re.S))
    if [int(m[2]) for m in markers] != list(range(1, len(fences) + 1)):
        raise ValueError(f'{context}: must include every F# fence once, in page order')
    def setup(code):
        return '\n\n```fsharp prepare\n' + code.rstrip() + '\n```\n\n' if code.strip() else ''
    replacements = []
    start = 0
    for marker, fence in zip(markers, fences):
        code = fence[1]
        if marker[3]:
            lines = code.splitlines()
            if not re.fullmatch(r'module \w+', lines[0]):
                raise ValueError(f'{context}: module snippet must start with a module declaration')
            code = lines[0] + ' =\n' + textwrap.indent('\n'.join(lines[1:]), '    ') + '\n'
        code = textwrap.indent(code, marker[1])
        replacements.append(setup(template[start:marker.start()]) + '```fsharp\n' + code + '```')
        start = marker.end()
    replacements[-1] += setup(template[start:])
    for fence, replacement in reversed(list(zip(fences, replacements))):
        body = body[:fence.start()] + replacement + body[fence.end():]
    return body.replace('---\n', f'---\nproject: {EXAMPLES_PROJECT.relative_to(ROOT)}\n', 1)


def redirect(target):
    escaped = html.escape(target, quote=True)
    return ('<!doctype html>\n<html lang="en"><head><meta charset="utf-8">'
            f'<script>location.replace({json.dumps(target)} + location.search + location.hash);</script>'
            f'<noscript><meta http-equiv="refresh" content="0; url={escaped}"></noscript>'
            '<title>FCQRS documentation</title></head><body>'
            f'<p><a href="{escaped}">Continue to the FCQRS guide</a></p>'
            '</body></html>\n')


def heading_anchors(body):
    lines = []
    fence = None
    for line in body.splitlines():
        marker = re.match(r'^\s*(`{3,}|~{3,})', line)
        if marker:
            if fence is None:
                fence = marker[1]
            elif marker[1][0] == fence[0] and len(marker[1]) >= len(fence):
                fence = None
        if fence is None and re.match(r'^#{1,6} ', line):
            title = line.split(' ', 1)[1]
            anchor = re.sub(r'[^\w -]', '', title).replace(' ', '-')
            line += ' {#' + anchor + '}'
        lines.append(line)
    return '\n'.join(lines) + '\n'


def prepare():
    if CONTENT.exists():
        shutil.rmtree(CONTENT)
    CONTENT.mkdir(parents=True)
    pages = []
    for source in sorted(DOCS.rglob('*')):
        if not source.is_file():
            continue
        relative = source.relative_to(DOCS)
        if source.suffix not in {'.md', '.fsx'}:
            continue
        body = source.read_text()
        if source.suffix == '.fsx':
            body = literate_markdown(body, str(relative))
        body = compiler_context(body, relative)
        front = re.match(r'---\n(.*?)\n---\n', body, re.S)
        if not front:
            raise ValueError(f'{source}: missing front matter')
        metadata = front[1]
        order = int(re.search(r'^index: (\d+)$', metadata, re.M)[1])
        group = int(re.search(r'^categoryindex: (\d+)$', metadata, re.M)[1])
        stem = re.sub(r'^\d+[\s._-]*', '', source.stem)
        page_order = group * 100 + order if relative.parent == Path('.') else order
        filename = 'index.md' if stem == 'index' else f'{page_order:03d}-{stem}.md'
        directory = Path(f'{group * 100 + 50:03d}-{relative.parent}') if relative.parent != Path('.') else Path('.')
        target = CONTENT / directory / filename
        target.parent.mkdir(parents=True, exist_ok=True)
        # Keep fsdocs heading anchors, including existing external deep links.
        body = heading_anchors(body)
        body = re.sub(r'^(?:category|categoryindex|index): .*\n', '', body, flags=re.M)
        body = body.replace('1-the-aggregate.html', 'the-aggregate.html').replace('2-running-it.html', 'running-it.html')
        target.write_text(body)
        output_path = relative.with_name(stem + '.html')
        pages.append(str(output_path))
    return pages


def finish(pages):
    # 0.7.3 treats static assets as guides when using docsSets. Keep only Markdown
    # in the input set, and copy the homepage, redirects, and assets after rendering.
    for source in DOCS.rglob('*'):
        if source.is_file() and source.suffix not in {'.md', '.fsx'}:
            target = OUTPUT / source.relative_to(DOCS)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, target)
    for source in DOCS.rglob('*.fsx'):
        relative = source.relative_to(DOCS).with_suffix('.html')
        canonical = re.sub(r'^\d+[\s._-]*', '', relative.name)
        if canonical != relative.name:
            (OUTPUT / relative).write_text(redirect(canonical))
    (OUTPUT / 'reference').mkdir(exist_ok=True)
    (OUTPUT / 'reference/index.html').write_text(redirect('../api/index.html'))
    (OUTPUT / 'api.html').write_text(redirect('api/index.html'))
    # FsLiveDocs has no custom-script setting. Enhance generated pages, preserving
    # the tool's semantic F# highlighting and its C# highlighter.
    for page in OUTPUT.rglob('*.html'):
        text = page.read_text()
        if page.is_relative_to(OUTPUT / 'api'):
            # 0.7.3 emits links to record fields without matching row anchors.
            # Anchor those field links where their type and summary are rendered.
            ids = set(re.findall(r'\bid="([^"]+)"', text))
            def anchor_field(match):
                target = match[2]
                if target in ids:
                    return match[0]
                ids.add(target)
                return match[1] + f'id="{target}" href="#{target}"'
            text = re.sub(r'(<a\s+)href="#([^"]+)"', anchor_field, text)
        if 'id="sidebar-root"' in text:
            depth = len(page.relative_to(OUTPUT).parts) - 1
            asset = '../' * depth + 'content/language-tabs.js'
            if f'src="{asset}"' not in text:
                highlighter = '../' * depth + 'content/highlight.min.js'
                scripts = f'<script src="{highlighter}" defer></script><script src="{asset}" defer></script>'
                text = text.replace('</body>', scripts + '</body>')
            page.write_text(text)
    # Keep fsdocs API entry points usable when following old bookmarks.
    for page in (OUTPUT / 'api').glob('*.html'):
        if page.stem == 'index':
            continue
        legacy = re.sub(r'[.`]', '-', page.stem).lower() + '.html'
        (OUTPUT / 'reference' / legacy).write_text(redirect('../api/' + page.name))
    for page in pages:
        if not (OUTPUT / page).is_file():
            raise ValueError(f'Missing generated documentation page: {page}')
    print(f'Verified {len(pages)} guide pages.', flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--prepare-only', action='store_true', help='Generate FsLiveDocs inputs without building.')
    parser.add_argument('--no-build', action='store_true', help='Use already built Release assemblies.')
    options = parser.parse_args()
    pages = prepare()
    if options.prepare_only:
        return
    if not options.no_build:
        run('dotnet', 'build', '-c', 'Release', 'FCQRS.sln')
        run('dotnet', 'build', '-c', 'Release', str(EXAMPLES_PROJECT))
    run('python3', 'scripts/check-learning-snippets.py')
    for script in sorted(DOCS.rglob('*.fsx')):
        run('dotnet', 'fsi', '--exec', str(script))
    # 0.7.3 requires absolute project paths when loading assemblies for extraction.
    run('dotnet', 'livedocs', 'test', *PROJECTS, '--interactive', 'false', '--banner', 'false')
    if OUTPUT.exists():
        shutil.rmtree(OUTPUT)
    run('dotnet', 'livedocs', 'build', *PROJECTS, '--version', VERSION, '--interactive', 'false', '--banner', 'false')
    finish(pages)
    run('python3', 'scripts/check-doc-tooltips.py')
    # Reindex the final homepage and fail if Pagefind cannot produce the index.
    run('npx', '--yes', 'pagefind@1.5.2', '--site', 'output')
    run('python3', 'scripts/check-doc-links.py')


if __name__ == '__main__':
    main()
