#!/usr/bin/env python3
"""Run each accounts tutorial step from a clean copy and check the output its page shows."""
import re
import shutil
import sqlite3
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
# The tutorial's programs need the .NET 11 SDK for C# 15; this file selects it.
GLOBAL_JSON = ROOT / 'samples/accounts/global.json'


def run(project):
    # A C# switch or F# match that misses a command or event case fails the check.
    build = subprocess.run(['dotnet', 'build', '-warnaserror:CS8509;FS0025'], cwd=project, text=True,
                           stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=300)
    assert build.returncode == 0, build.stdout
    result = subprocess.run(['dotnet', 'run', '--no-build'], cwd=project, text=True,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=300)
    assert result.returncode == 0, result.stdout
    return result.stdout


def copy(temp, step, language):
    source = ROOT / 'samples/accounts' / step / language
    project = Path(temp) / step / language
    project.mkdir(parents=True)
    for file in source.iterdir():
        if file.is_file() and file.suffix in {'.fs', '.cs', '.fsproj', '.csproj'}:
            shutil.copy2(file, project / file.name)
    shutil.copy2(GLOBAL_JSON, project / 'global.json')
    return project


def replies(output):
    """The printed replies, such as 'Deposited 100 (version 2)', in order."""
    return re.findall(r'^\S.* \(version \d+(?:, (?:not )?stored)?\)$', output, re.M)


def journal(output):
    """The printed journal rows of Alice's account as (sequence number, stored event) pairs."""
    return [(int(number), event)
            for number, event in re.findall(r'^  (\d+)  (.+)$', output, re.M)]


def journal_count(output):
    """The printed number of journal rows for Alice's account."""
    return int(re.search(r'^Journal: (\d+) events for Account/default-shard/alice$', output, re.M)[1])


def snapshots(output):
    """The printed snapshot rows as (version, state) pairs."""
    return [(int(version), state) for version, state in re.findall(r'^  version (\d+)  (.+)$', output, re.M)]


def statements(output, width):
    """Each printed statement as account -> [(version, entry, amount, balance)].
    width is the entry column's width."""
    result = {}
    for account, body in re.findall(
            r'^Statement for (\w+):\n  version  entry +amount  balance\n((?: +\d+  .*\n?)*)', output, re.M):
        result[account] = [(int(version), entry.strip(), int(amount), int(balance))
                           for version, entry, amount, balance
                           in re.findall(rf'^ +(\d+)  (.{{{width}}})(.{{7}})(.{{9}})$', body, re.M)]
    return result


def stored(language, case, field, value):
    """An event as the sample's serializer writes it into the journal."""
    if language == 'fsharp':
        return f'{{"Case":"{case}","{field}":{value}}}'
    return f'{{"$case":"{case}","$value":{{"{field.capitalize()}":{value}}}}}'


def same(actual, expected, output):
    assert actual == expected, f'expected {expected!r}\nactual   {actual!r}\noutput:\n{output}'


def open_an_account(project, language):
    first = run(project)
    same(replies(first), ['Opened for Alice (version 1)',
                          'Deposited 100 (version 2)',
                          'Deposited 50 (version 3)'], first)
    same(journal(first), [(1, stored(language, 'Opened', 'owner', '"Alice"')),
                          (2, stored(language, 'Deposited', 'amount', 100)),
                          (3, stored(language, 'Deposited', 'amount', 50))], first)
    # The second run loads the stored events first, so the versions continue.
    second = run(project)
    same(replies(second), ['Opened for Alice (version 4)',
                           'Deposited 100 (version 5)',
                           'Deposited 50 (version 6)'], second)
    same([number for number, _ in journal(second)], [1, 2, 3, 4, 5, 6], second)


def withdraw_money(project, language):
    first = run(project)
    same(replies(first), ['Opened for Alice (version 1, stored)',
                          'Deposited 100 (version 2, stored)',
                          'Withdrew 30 (version 3, stored)',
                          'Rejected: Insufficient funds: 70 available (version 3, not stored)',
                          'Rejected: The account is already open (version 3, not stored)',
                          'Withdrew 60 (version 4, stored)',
                          'Rejected: Insufficient funds: 10 available (version 4, not stored)'], first)
    # Rejections are replies only, so the journal holds the four accepted commands.
    same(journal(first), [(1, stored(language, 'Opened', 'owner', '"Alice"')),
                          (2, stored(language, 'Deposited', 'amount', 100)),
                          (3, stored(language, 'Withdrawn', 'amount', 30)),
                          (4, stored(language, 'Withdrawn', 'amount', 60))], first)
    # The second run starts from the stored events: the account is open and holds 10.
    second = run(project)
    same(replies(second), ['Rejected: The account is already open (version 4, not stored)',
                           'Deposited 100 (version 5, stored)',
                           'Withdrew 30 (version 6, stored)',
                           'Rejected: Insufficient funds: 80 available (version 6, not stored)',
                           'Rejected: The account is already open (version 6, not stored)',
                           'Withdrew 60 (version 7, stored)',
                           'Rejected: Insufficient funds: 20 available (version 7, not stored)'], second)
    same([number for number, _ in journal(second)], [1, 2, 3, 4, 5, 6, 7], second)


def restart_the_bank(project, language):
    # After v events Alice has one Opened and v - 1 deposits of 10.
    def snapshot(version):
        return (version, f'{{"Owner":"Alice","Balance":{10 * (version - 1)}}}')
    first = run(project)
    same(replies(first), ['Opened for Alice (version 1)', 'Deposited 10 (version 251)'], first)
    same(journal_count(first), 251, first)
    same(snapshots(first), [snapshot(100), snapshot(200)], first)
    # The second run loads the account from the snapshot at version 200 before its first command.
    second = run(project)
    same(replies(second), ['Rejected: The account is already open (version 251)',
                           'Deposited 10 (version 501)'], second)
    same(journal_count(second), 501, second)
    same(snapshots(second), [snapshot(version) for version in (100, 200, 300, 400, 500)], second)


def show_a_statement(project, language):
    # Alice's statement after each round of commands: Open, Deposit 100, Withdraw 30, Deposit 50.
    def rows(rounds):
        result, balance, version = [(1, 'Opened for Alice', 0, 0)], 0, 1
        for _ in range(rounds):
            for entry, amount in [('Deposit', 100), ('Withdrawal', -30), ('Deposit', 50)]:
                balance, version = balance + amount, version + 1
                result.append((version, entry, amount, balance))
        return result
    first = run(project)
    same(replies(first), ['Opened for Alice (version 1)',
                          'Deposited 100 (version 2)',
                          'Withdrew 30 (version 3)',
                          'Deposited 50 (version 4)',
                          'Rejected: Insufficient funds: 120 available (version 4)'], first)
    same(statements(first, 18), {'alice': rows(1)}, first)
    # The projection resumes after the last event it committed, so no row appears twice.
    second = run(project)
    same(replies(second), ['Rejected: The account is already open (version 4)',
                           'Deposited 100 (version 5)',
                           'Withdrew 30 (version 6)',
                           'Deposited 50 (version 7)',
                           'Rejected: Insufficient funds: 240 available (version 7)'], second)
    same(statements(second, 18), {'alice': rows(2)}, second)
    # Clearing the statement and the projection's progress rebuilds it from the journal.
    with sqlite3.connect(project / 'bin/Debug/net11.0/accounts.db') as database:
        database.execute('DELETE FROM statement')
        database.execute("DELETE FROM fcqrs_projection_progress WHERE projection_name = 'Statement'")
    third = run(project)
    same(statements(third, 18), {'alice': rows(3)}, third)


def transfer_money(project, language):
    alice = [(1, 'Opened for Alice', 0, 0), (2, 'Deposit', 100, 100),
             (3, 'Transfer t1 to bob', -30, 70), (4, 'Transfer t2 to carol', -20, 50),
             (5, 'Refund of transfer t2', 20, 70)]
    bob = [(1, 'Opened for Bob', 0, 0), (2, 'Transfer t1 from alice', 30, 30)]
    first = run(project)
    same(replies(first), ['Opened for Alice (version 1, stored)',
                          'Deposited 100 (version 2, stored)',
                          'Opened for Bob (version 1, stored)',
                          'Sent 30 to bob (t1) (version 3, stored)',
                          'Sent 20 to carol (t2) (version 4, stored)',
                          'Received 30 from alice (t1) (version 2, not stored)'], first)
    # t1 reached Bob once, although it was delivered twice; t2 came back to Alice.
    same(statements(first, 24), {'alice': alice, 'bob': bob}, first)
    # The second run repeats the requests: the transfer IDs stop them from moving money again.
    second = run(project)
    same(replies(second), ['Rejected: The account is already open (version 5, not stored)',
                           'Deposited 100 (version 6, stored)',
                           'Rejected: The account is already open (version 2, not stored)',
                           'Rejected: Transfer t1 was already sent (version 6, not stored)',
                           'Rejected: Transfer t2 was already sent (version 6, not stored)',
                           'Received 30 from alice (t1) (version 2, not stored)'], second)
    same(statements(second, 24), {'alice': alice + [(6, 'Deposit', 100, 170)], 'bob': bob}, second)


def memo_statements(output):
    """Each printed statement with a memo column as account -> [(version, entry, memo, amount, balance)]."""
    result = {}
    for account, body in re.findall(
            r'^Statement for (\w+):\n  version  entry +memo +amount  balance\n((?: +\d+  .*\n?)*)', output, re.M):
        result[account] = [(int(version), entry.strip(), memo.strip(), int(amount), int(balance))
                           for version, entry, memo, amount, balance
                           in re.findall(r'^ +(\d+)  (.{24}) (.{6})(.{7})(.{9})$', body, re.M)]
    return result


def add_a_memo(project, language):
    # Step 6 continues from step 5's database, so run step 5 once in a tree of its own.
    root = project.parent.parent / 'from-step-5'
    run(copy(root, '5-transfer-money', language))
    project = copy(root, '6-add-a-memo', language)

    def sent(id, target, amount, memo=None):
        fields = [('transferId', f'"{id}"'), ('target', f'"{target}"'), ('amount', amount)]
        if memo:
            fields.append(('memo', f'"{memo}"'))
        if language == 'csharp':
            fields = [(name[0].upper() + name[1:], value) for name, value in fields]
        return '{' + ','.join(f'"{name}":{value}' for name, value in fields) + '}'

    transfers = [(3, sent('t1', 'bob', 30)), (4, sent('t2', 'carol', 20)), (6, sent('t3', 'bob', 25, 'rent'))]
    statements = {
        'alice': [(1, 'Opened for Alice', '', 0, 0), (2, 'Deposit', '', 100, 100),
                  (3, 'Transfer t1 to bob', '', -30, 70), (4, 'Transfer t2 to carol', '', -20, 50),
                  (5, 'Refund of transfer t2', '', 20, 70), (6, 'Transfer t3 to bob', 'rent', -25, 45)],
        'bob': [(1, 'Opened for Bob', '', 0, 0), (2, 'Transfer t1 from alice', '', 30, 30),
                (3, 'Transfer t3 from alice', 'rent', 25, 55)]}
    first = run(project)
    same(replies(first), ['Sent 25 to bob (t3, "rent") (version 6, stored)'], first)
    # Events step 5 stored have no memo; the new code reads them and adds one with a memo.
    same(journal(first), transfers, first)
    # The new statement was built from the first event, so it has step 5's history too.
    same(memo_statements(first), statements, first)
    second = run(project)
    same(replies(second), ['Rejected: Transfer t3 was already sent (version 6, not stored)'], second)
    same(memo_statements(second), statements, second)


def domain_tests(temp):
    """Run the C# commands and test class from the domain-testing guide against step 2."""
    guide = (ROOT / 'docs/how-to/test-your-domain.fsx').read_text()
    commands = [block for block in re.findall(r'```text\n(.*?)\n```', guide, re.S) if 'dotnet new xunit' in block]
    assert len(commands) == 1, commands
    # The guide runs them from samples/accounts; the temporary folder has the same layout.
    for line in commands[0].splitlines():
        if line.startswith('cd '):
            continue
        result = subprocess.run(line.split(), cwd=temp, text=True,
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=300)
        assert result.returncode == 0, result.stdout
    test_code = guide.split('```csharp\n', 1)[1].split('\n```', 1)[0]
    (Path(temp) / 'Accounts.Tests/UnitTest1.cs').write_text(test_code)
    result = subprocess.run(['dotnet', 'test', 'Accounts.Tests'], cwd=temp, text=True,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=300)
    assert result.returncode == 0, result.stdout
    assert 'Passed:     3' in result.stdout, result.stdout


STEPS = {'1-open-an-account': open_an_account,
         '2-withdraw-money': withdraw_money,
         '3-restart-the-bank': restart_the_bank,
         '4-show-a-statement': show_a_statement,
         '5-transfer-money': transfer_money,
         '6-add-a-memo': add_a_memo}

# Optional arguments select steps by folder name, for example: 2-withdraw-money
selected = sys.argv[1:] or list(STEPS)
unknown = [step for step in selected if step not in STEPS]
assert not unknown, f'unknown steps: {unknown}'

with tempfile.TemporaryDirectory(prefix='fcqrs-accounts-') as temp:
    shutil.copy2(GLOBAL_JSON, Path(temp) / 'global.json')
    for step in selected:
        for language in ['fsharp', 'csharp']:
            STEPS[step](copy(temp, step, language), language)
            print(f'{step} ({language}) passed.', flush=True)
    if '2-withdraw-money' in selected:
        domain_tests(temp)
        print('Test your domain (C#) passed.', flush=True)
