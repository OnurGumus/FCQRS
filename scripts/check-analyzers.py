#!/usr/bin/env python3
"""Runs the determinism analyzers over the samples and fails on any warning.

The samples are correct workflows and aggregates, so a warning is a false positive. The F#
analyzer runs through the fsharp-analyzers tool after each project is built, because the tool
checks against the built references. The C# analyzer is added to each C# build through
CustomAfterMicrosoftCommonTargets, so the sample projects stay as readers copy them.
"""

import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

FSHARP = [
    'samples/workflows/fsharp',
    'samples/resilience/fsharp',
    'samples/approvals/fsharp',
    'samples/reservations/fsharp',
    'samples/start-from-an-event/fsharp',
]

CSHARP = [
    'samples/workflows/csharp',
    'samples/resilience/csharp',
    'samples/approvals/csharp',
    'samples/reservations/csharp',
    'samples/start-from-an-event/csharp',
    'test/Workflows.CSharp.Tests',
]


def run(*command: str) -> None:
    print('+', ' '.join(command), flush=True)
    subprocess.run(command, cwd=ROOT, check=True)


def main() -> int:
    run('dotnet', 'build', 'src/FCQRS.Analyzers', '-c', 'Release')
    fsharp_analyzers = ROOT / 'src/FCQRS.Analyzers/bin/Release/net10.0'
    csharp_analyzer = ROOT / 'src/FCQRS.Analyzers.CSharp/bin/Release/netstandard2.0/FCQRS.Analyzers.CSharp.dll'

    for directory in FSHARP:
        project = next((ROOT / directory).glob('*.fsproj'))
        run('dotnet', 'build', str(project))
        run('dotnet', 'fsharp-analyzers', '--project', str(project),
            '--analyzers-path', str(fsharp_analyzers),
            '--treat-as-error', 'FCQRS001', 'FCQRS002')

    with tempfile.TemporaryDirectory() as scratch:
        targets = Path(scratch) / 'add-analyzer.targets'
        targets.write_text(
            '<Project><ItemGroup>'
            f'<Analyzer Include="{csharp_analyzer}" />'
            '</ItemGroup></Project>')
        for directory in CSHARP:
            run('dotnet', 'build', directory, '-c', 'Release', '--no-incremental',
                f'-p:CustomAfterMicrosoftCommonTargets={targets}',
                '-warnaserror:FCQRS001,FCQRS002')

    print('No determinism warning in the samples.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
