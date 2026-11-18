#!/usr/bin/env python3
"""Compiles test/Model.Fable.Tests to JavaScript with Fable and runs it with Node.

FCQRS.Model is a Fable library: Fable apps compile it from its source. This check fails when
the package stops compiling under Fable, or when a free program gives another result there.
Fable is pinned in .config/dotnet-tools.json; run `dotnet tool restore` first.
"""
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PROJECT = ROOT / 'test/Model.Fable.Tests'
OUT = PROJECT / 'out'


def run(*args: str, cwd: Path = ROOT) -> None:
    print('+', ' '.join(args), flush=True)
    subprocess.run(args, cwd=cwd, check=True)


def main() -> int:
    if OUT.exists():
        shutil.rmtree(OUT)
    run('dotnet', 'fable', str(PROJECT / 'Model.Fable.Tests.fsproj'), '--outDir', str(OUT), '--noCache')
    run('node', str(OUT / 'Program.js'), cwd=PROJECT)
    return 0


if __name__ == '__main__':
    sys.exit(main())
