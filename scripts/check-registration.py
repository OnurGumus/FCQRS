#!/usr/bin/env python3
"""Run the published-package quickstart and its name/account-ID exercises."""
import shutil
import sqlite3
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
with tempfile.TemporaryDirectory(prefix='fcqrs-registration-') as temp:
    shutil.copy2(ROOT / 'global.json', Path(temp) / 'global.json')
    for language, extension in [('fsharp', 'fs'), ('csharp', 'cs')]:
        source = ROOT / f'samples/registration-{language}'
        project = Path(temp) / language
        project.mkdir()
        for file in source.iterdir():
            if file.is_file() and file.suffix in {'.fs', '.cs', '.fsproj', '.csproj'}:
                shutil.copy2(file, project / file.name)
        shutil.copy2(ROOT / 'global.json', project / 'global.json')

        def run(expected, name='Alice'):
            result = subprocess.run(['dotnet', 'run'], cwd=project, text=True,
                                    stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=180)
            assert result.returncode == 0, result.stdout
            assert expected in result.stdout, result.stdout
            assert f'Query: {name}' in result.stdout, result.stdout

        run('Registered: Alice (version 1)')
        run('Already registered: Alice (version 1)')
        program = project / f'Program.{extension}'
        text = program.read_text()
        before, after = ('RegisterUser "Alice"', 'RegisterUser "Bob"') if language == 'fsharp' else ('new RegisterUser("Alice")', 'new RegisterUser("Bob")')
        assert before in text
        program.write_text(text.replace(before, after))
        run('Already registered: Alice (version 1)')
        database = project / 'bin/Debug/net10.0/registration.db'
        with sqlite3.connect(database) as connection:
            assert connection.execute('SELECT COUNT(*) FROM journal').fetchone()[0] == 1

        program.write_text(program.read_text().replace('accountId = "alice"', 'accountId = "bob"'))
        run('Registered: Bob (version 1)', 'Bob')
        program.write_text(program.read_text().replace('accountId = "bob"', 'accountId = "alice"'))
        run('Already registered: Alice (version 1)')
        with sqlite3.connect(database) as connection:
            assert connection.execute('SELECT COUNT(*) FROM journal').fetchone()[0] == 2
        print(f'{language}: registration, restart, query rebuild, repeat, and independent account passed.', flush=True)

    # Compile and run the exact C# test class taught by the domain-testing guide.
    tests = Path(temp) / 'Registration.Tests'
    def dotnet(*args):
        result = subprocess.run(['dotnet', *args], cwd=temp, text=True,
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=180)
        assert result.returncode == 0, result.stdout
        return result.stdout

    dotnet('new', 'xunit', '-n', 'Registration.Tests', '--framework', 'net10.0')
    dotnet('add', 'Registration.Tests', 'reference', 'csharp/Registration.CSharp.csproj')
    guide = (ROOT / 'docs/how-to/test-your-domain.fsx').read_text()
    test_code = guide.split('```csharp\n', 1)[1].split('\n```', 1)[0]
    (tests / 'UnitTest1.cs').write_text(test_code)
    result = dotnet('test', 'Registration.Tests')
    print(result.strip(), flush=True)
