#!/usr/bin/env python3
"""Run the documented CLI path, real recovery, and old-journal/snapshot compatibility."""
import argparse
import json
import os
import re
import sqlite3
import subprocess
import tempfile
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--language', choices=['fsharp', 'csharp'])
parser.add_argument('--no-build', action='store_true')
parser.add_argument('--configuration', default='Debug', choices=['Debug', 'Release'])
options = parser.parse_args()

def exercise(language, database, *args):
    command = ['dotnet', 'run', '--project', f'samples/getting-started-{language}', '--configuration', options.configuration]
    if options.no_build: command.append('--no-build')
    if args: command += ['--', *args]
    result = subprocess.run(command, cwd=ROOT, env={**os.environ, 'DOCSTORE_DATABASE': str(database)},
                            text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=120)
    if result.returncode:
        raise AssertionError(f'{command}: exit {result.returncode}\n{result.stdout}')
    return result.stdout

def contains(output, expected):
    assert expected in output, f'Expected {expected!r}\n{output}'

def create(language, database):
    output = exercise(language, database)
    contains(output, "stored version 1; query returned 'first event'")
    contains(output, "repeat reply version 1; document contains 'first event'")
    return re.search(r'document id: (\S+)', output)[1]

def versions(database, identity):
    with sqlite3.connect(database) as connection:
        return [r[0] for r in connection.execute(
            'SELECT sequence_number FROM journal WHERE persistence_id LIKE ? ORDER BY sequence_number',
            ('%/' + identity,))]

def walkthrough(language, database):
    contains(exercise(language, database, '--check'), 'All document, replay, and saga checks passed.')
    identity = create(language, database)
    contains(exercise(language, database, '--recover', identity), "recovery reply version 1; document contains 'first event'")
    contains(exercise(language, database, '--edit', identity, 'second draft'), "edited version 2; query returned 'second draft'")
    contains(exercise(language, database, '--edit', identity, 'second draft'), "edit reply version 2; document contains 'second draft'")
    contains(exercise(language, database, '--recover', identity), "recovery reply version 2; document contains 'second draft'")
    contains(exercise(language, database, '--edit', 'missing-document', 'draft'), 'edit rejected: Document does not exist')
    contains(exercise(language, database, '--edit', identity, ''), 'edit rejected: Content must not be blank')
    output = exercise(language, database, '--publish', identity, 'guides/fcqrs')
    contains(output, 'publication version 4; guides/fcqrs -> Published')
    contains(output, "query returned 'second draft'")
    contains(exercise(language, database, '--publish', identity, 'guides/fcqrs'), 'publication version 4; guides/fcqrs -> Published')
    contains(exercise(language, database, '--edit', identity, 'third draft'), 'edit rejected: Editing closes when publication starts')
    assert versions(database, identity) == [1, 2, 3, 4]
    second = create(language, database)
    contains(exercise(language, database, '--publish', second, 'guides/fcqrs'), 'publication version 3; guides/fcqrs -> Rejected')
    assert versions(database, second) == [1, 2, 3]
    third = create(language, database)
    contains(exercise(language, database, '--pause-publication', third, 'guides/recovery'), 'publication paused after the reservation')
    assert versions(database, third) == [1, 2], 'Pause must occur before completion is stored'
    contains(exercise(language, database, '--publish', third, 'guides/recovery'), 'publication version 3; guides/recovery -> Published')
    assert versions(database, third) == [1, 2, 3], 'Recovery must complete without duplicate events'
    print(f'{language}: create, checks, recovery, edit, duplicate edit, rejection, publication, slug conflict, and saga recovery passed.', flush=True)

def legacy(language, database, use_snapshot):
    create(language, database)  # initialize schema in this isolated store
    fixture = json.loads((ROOT / f'samples/fixtures/document-created-v1-{language}.json').read_text())
    identity = fixture['persistenceId'].rsplit('/', 1)[1]
    raw = json.dumps(fixture['payload'], separators=(',', ':')).encode()
    with sqlite3.connect(database) as connection:
        created = connection.execute('SELECT created FROM journal LIMIT 1').fetchone()[0]
        connection.execute('INSERT INTO journal(created,deleted,persistence_id,sequence_number,message,manifest,identifier,writer_uuid) VALUES (?,0,?,1,?,?,1713,?)',
                           (created, fixture['persistenceId'], raw, fixture['manifest'], str(uuid.uuid4())))
        if use_snapshot:
            snapshot = json.loads((ROOT / f'samples/fixtures/document-snapshot-v1-{language}.json').read_text())
            connection.execute('INSERT INTO snapshot(persistence_id,sequence_number,created,snapshot,manifest,serializer_id) VALUES (?,1,?,?,?,1713)',
                               (fixture['persistenceId'], created, json.dumps(snapshot['payload']).encode(), snapshot['manifest']))
    contains(exercise(language, database, '--recover', identity), "recovery reply version 1; document contains 'first event'")
    contains(exercise(language, database, '--edit', identity, 'second draft'), "edited version 2; query returned 'second draft'")
    contains(exercise(language, database, '--recover', identity), "recovery reply version 2; document contains 'second draft'")
    contains(exercise(language, database, '--publish', identity, 'guides/legacy'), 'publication version 4; guides/legacy -> Published')
    assert versions(database, identity) == [1, 2, 3, 4]
    with sqlite3.connect(database) as connection:
        stored = connection.execute('SELECT message,manifest FROM journal WHERE persistence_id=? AND sequence_number=1', (fixture['persistenceId'],)).fetchone()
        assert stored == (raw, fixture['manifest']), 'The legacy row must remain byte-for-byte unchanged'
    print(f'{language}: legacy {"snapshot + journal" if use_snapshot else "journal"}, mixed-history recovery, and projection rebuild passed.', flush=True)

with tempfile.TemporaryDirectory(prefix='fcqrs-learning-') as temp:
    for language in ([options.language] if options.language else ['fsharp', 'csharp']):
        print(f'{language}: running the documented path...', flush=True)
        walkthrough(language, Path(temp) / f'{language}.db')
        legacy(language, Path(temp) / f'{language}-legacy.db', False)
        legacy(language, Path(temp) / f'{language}-snapshot.db', True)
