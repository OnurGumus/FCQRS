#!/usr/bin/env python3
"""Exercise the accounts HTTP samples in fresh .NET 11 projects."""
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
ACCOUNTS = ROOT / 'samples/accounts'


def request(base, method, path, body=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(base + path, data=data, method=method,
                                 headers={'Content-Type': 'application/json'})
    try:
        response = urllib.request.urlopen(req, timeout=65)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        text = response.read().decode()
        content_type = response.headers.get('Content-Type', '')
        return response.status, json.loads(text) if text and 'json' in content_type else text or None


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
                    status, body = request(base, 'GET', '/accounts/nobody/statement')
                    assert status == 404, (status, body)
                    break
                except (urllib.error.URLError, TimeoutError, ConnectionError):
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


def expect(base, method, path, status, body=None, reply=None):
    actual, result = request(base, method, path, body)
    assert (actual, result) == (status, reply), (method, path, actual, result)


def statement(base, account):
    status, rows = request(base, 'GET', f'/accounts/{account}/statement')
    assert status == 200, (status, rows)
    return [(row['version'], row['entry'], row['amount'], row['balance']) for row in rows]


INVALID = {'error': 'The ID and owner must be non-blank, at most 255 characters.'}

with tempfile.TemporaryDirectory(prefix='fcqrs-accounts-http-') as temp:
    root = Path(temp)
    shutil.copy2(ACCOUNTS / 'global.json', root / 'global.json')
    for language, extension in [('fsharp', 'fs'), ('csharp', 'cs')]:
        # The HTTP project compiles step 4's account and statement from their own folder.
        domain = root / '4-show-a-statement' / language
        domain.mkdir(parents=True)
        for name in ['Account', 'Statement']:
            shutil.copy2(ACCOUNTS / '4-show-a-statement' / language / f'{name}.{extension}', domain)
        project = root / 'serve-over-http' / language
        project.mkdir(parents=True)
        for source in (ACCOUNTS / 'serve-over-http' / language).iterdir():
            if source.is_file() and source.suffix in {'.fs', '.cs', '.fsproj', '.csproj'}:
                shutil.copy2(source, project / source.name)
        build = subprocess.run(['dotnet', 'build', '-warnaserror:CS8509;FS0025'], cwd=project,
                               text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                               timeout=300)
        assert build.returncode == 0, build.stdout

        with server(project) as base:
            for path, body in [('/accounts/alice', {'owner': ''}), ('/accounts/alice', {}),
                               ('/accounts/alice', {'owner': 'x' * 256}),
                               ('/accounts/' + 'x' * 256, {'owner': 'Alice'}),
                               ('/accounts/' + 'x' * 256 + '/deposits', {'amount': 5})]:
                expect(base, 'POST', path, 400, body, INVALID)
            assert request(base, 'POST', '/accounts/alice', 'not an object')[0] == 400

            expect(base, 'POST', '/accounts/alice', 200, {'owner': 'Alice'}, {'balance': 0})
            expect(base, 'POST', '/accounts/alice', 422, {'owner': 'Alice'},
                   {'error': 'The account is already open'})
            expect(base, 'POST', '/accounts/alice/deposits', 200, {'amount': 100}, {'balance': 100})
            expect(base, 'POST', '/accounts/alice/withdrawals', 200, {'amount': 30}, {'balance': 70})
            expect(base, 'POST', '/accounts/alice/withdrawals', 422, {'amount': 500},
                   {'error': 'Insufficient funds: 70 available'})
            expect(base, 'POST', '/accounts/alice/deposits', 422, {},
                   {'error': 'The amount must be positive'})
            expect(base, 'POST', '/accounts/bob/deposits', 422, {'amount': 5},
                   {'error': 'The account is not open'})
            assert statement(base, 'alice') == [(1, 'Opened for Alice', 0, 0), (2, 'Deposit', 100, 100),
                                                (3, 'Withdrawal', -30, 70)]
            expect(base, 'GET', '/accounts/bob/statement', 404, reply=None)

            # The account decides one command at a time, so concurrent deposits all count.
            with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
                replies = list(pool.map(
                    lambda _: request(base, 'POST', '/accounts/alice/deposits', {'amount': 10}),
                    range(3)))
            assert all(status == 200 for status, _ in replies), replies
            # Each balance includes its own deposit and possibly the others.
            assert all(80 <= body['balance'] <= 100 for _, body in replies), replies
            rows = statement(base, 'alice')
            assert [row[0] for row in rows] == [1, 2, 3, 4, 5, 6], rows
            assert rows[-1][3] == 100, rows

        # A restart keeps the journal and the statement; commands continue from version 6.
        with server(project) as base:
            assert len(statement(base, 'alice')) == 6
            expect(base, 'POST', '/accounts/alice/withdrawals', 200, {'amount': 100}, {'balance': 0})
            assert statement(base, 'alice')[-1] == (7, 'Withdrawal', -100, 0)

        with sqlite3.connect(project / 'bin/Debug/net11.0/accounts.db') as connection:
            count = connection.execute(
                "SELECT COUNT(*) FROM journal WHERE persistence_id = 'Account/default-shard/alice'"
            ).fetchone()[0]
            assert count == 7, count
        print(f'{language}: HTTP commands, rejections, validation, statement, concurrency, '
              'and restart passed.', flush=True)
