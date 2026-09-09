#!/usr/bin/env python3
"""Exercise the optional HTTP registration samples in fresh .NET 10 projects."""
import concurrent.futures
import contextlib
import json
import os
import shutil
import signal
import socket
import sqlite3
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def request(base, method, account, body=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(base + '/accounts/' + account, data=data, method=method,
                                 headers={'Content-Type': 'application/json'})
    try:
        response = urllib.request.urlopen(req, timeout=65)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        text = response.read().decode()
        body = json.loads(text) if text and 'json' in response.headers.get('Content-Type', '') else text or None
        return response.status, body, response.headers


@contextlib.contextmanager
def server(project):
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        port = listener.getsockname()[1]
    base = f'http://127.0.0.1:{port}'
    log_path = project / 'http.log'
    with log_path.open('w') as log:
        process = subprocess.Popen(['dotnet', 'run', '--no-build', '--', '--urls', base],
                                   cwd=project, stdout=log, stderr=subprocess.STDOUT,
                                   start_new_session=True)
        try:
            deadline = time.monotonic() + 90
            while time.monotonic() < deadline:
                assert process.poll() is None, log_path.read_text()
                try:
                    status, body, _ = request(base, 'GET', 'not-registered')
                    assert status == 404, (status, body)
                    break
                except (urllib.error.URLError, TimeoutError):
                    pass
                time.sleep(0.1)
            else:
                raise AssertionError('HTTP startup timed out:\n' + log_path.read_text())
            yield base
        except Exception:
            print(log_path.read_text(), flush=True)
            raise
        finally:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGINT)
                try:
                    process.wait(timeout=25)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait()


def expect(base, method, account, status, name, body=None):
    actual, result, headers = request(base, method, account, body)
    assert actual == status, (actual, result)
    assert result == {'id': account, 'name': name}, result
    if status == 201:
        assert headers['Location'] == '/accounts/' + account, headers


with tempfile.TemporaryDirectory(prefix='fcqrs-registration-http-') as temp:
    root = Path(temp)
    shutil.copy2(ROOT / 'global.json', root / 'global.json')
    for language, extension in [('fsharp', 'fs'), ('csharp', 'cs')]:
        project = root / f'registration-http-{language}'
        project.mkdir()
        for source in (ROOT / f'samples/registration-http-{language}').iterdir():
            if source.is_file() and source.suffix in {'.fs', '.cs', '.fsproj', '.csproj'}:
                shutil.copy2(source, project / source.name)
        domain = root / f'registration-{language}'
        domain.mkdir()
        shutil.copy2(ROOT / f'samples/registration-{language}/Account.{extension}', domain)
        build = subprocess.run(['dotnet', 'build'], cwd=project, text=True,
                               stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=180)
        assert build.returncode == 0, build.stdout

        with server(project) as base:
            for account, body in [('invalid', {'name': ''}), ('invalid', {}),
                                  ('invalid', {'name': None}), ('invalid', {'name': 'x' * 256}),
                                  ('invalid', 'not an object'), ('x' * 256, {'name': 'Alice'})]:
                assert request(base, 'POST', account, body)[0] == 400
            expect(base, 'POST', 'alice', 201, 'Alice', {'name': 'Alice'})
            expect(base, 'GET', 'alice', 200, 'Alice')
            expect(base, 'POST', 'alice', 200, 'Alice', {'name': 'Bob'})
            expect(base, 'GET', 'alice', 200, 'Alice')
            expect(base, 'POST', 'bob', 201, 'Bob', {'name': 'Bob'})
            expect(base, 'GET', 'bob', 200, 'Bob')

            # Concurrent requests for one account must all observe the same winning registration.
            with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
                replies = list(pool.map(lambda name: request(base, 'POST', 'shared', {'name': name}),
                                        ['Carol', 'Chris', 'Casey']))
            assert sorted(status for status, _, _ in replies) == [200, 200, 201], replies
            saved = {body['name'] for _, body, _ in replies}
            assert len(saved) == 1, replies
            expect(base, 'GET', 'shared', 200, saved.pop())

        # Rebuild the query view from the journal without sending any new command.
        with server(project) as base:
            deadline = time.monotonic() + 30
            while any(request(base, 'GET', account)[0] != 200 for account in ['alice', 'bob']):
                assert time.monotonic() < deadline, 'Projection did not recover the accounts'
                time.sleep(0.1)
            expect(base, 'GET', 'alice', 200, 'Alice')
            expect(base, 'GET', 'bob', 200, 'Bob')
            expect(base, 'POST', 'alice', 200, 'Alice', {'name': 'replacement'})
            expect(base, 'GET', 'alice', 200, 'Alice')

        with sqlite3.connect(project / 'bin/Debug/net10.0/registration-http.db') as connection:
            assert connection.execute('SELECT COUNT(*) FROM journal').fetchone()[0] == 3
        print(f'{language}: HTTP registration, immediate query, validation, repeats, concurrency, and restart passed.',
              flush=True)
