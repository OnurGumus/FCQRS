#!/usr/bin/env python3
"""Verify the IL of FCQRS's Debug assemblies with ILVerify.

The F# compiler can emit IL that the JIT rejects only in Debug builds (dotnet/fsharp#20564: a
value-position `raise` whose argument contains a let, |>, if or match). The JIT reports such a
method only when it first runs, so a sample run covers only the code it reaches. ILVerify checks
every method.

Each project is published in Debug to a temporary folder, which puts every dependency next to its
assembly. The assembly is verified against that folder and the shared runtime of the SDK that
global.json selects. Release builds are not verified: ILVerify reports a PathStackUnexpected in
the Release IL of `Projections.catchUp`'s task code, which the JIT accepts.

Run `dotnet tool restore` first; the manifest pins the ILVerify version.
"""
import re
import subprocess
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
FRAMEWORK = 'net10.0'
PROJECTS = ['FCQRS.Model', 'FCQRS.Serialization', 'FCQRS', 'FCQRS.Workflows', 'FCQRS.SQLProvider']


def run(args):
    return subprocess.run(args, cwd=ROOT, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)


def runtime_folder():
    """The Microsoft.NETCore.App folder that ships with the SDK global.json selects."""
    version = run(['dotnet', '--version']).stdout.strip()
    sdks = run(['dotnet', '--list-sdks']).stdout
    match = re.search(rf'^{re.escape(version)} \[(.+)\]$', sdks, re.MULTILINE)
    assert match, f'SDK {version} not found in:\n{sdks}'
    sdk = Path(match.group(1)) / version
    props = (sdk / 'Microsoft.NETCoreSdk.BundledVersions.props').read_text()
    runtime = re.search(r'<BundledNETCoreAppPackageVersion>([^<]+)<', props).group(1)
    folder = sdk.parents[1] / 'shared' / 'Microsoft.NETCore.App' / runtime
    assert (folder / 'System.Private.CoreLib.dll').is_file(), f'runtime {runtime} not found at {folder}'
    return folder


def main():
    started = time.monotonic()
    runtime = runtime_folder()
    print(f'Runtime references: {runtime}', flush=True)
    failed = []
    with tempfile.TemporaryDirectory(prefix='fcqrs-verify-il-') as temp:
        for name in PROJECTS:
            output = Path(temp) / name
            publish = run(['dotnet', 'publish', f'src/{name}/{name}.fsproj', '-c', 'Debug',
                           '-f', FRAMEWORK, '-o', str(output), '-nologo', '-v', 'quiet'])
            if publish.returncode != 0:
                print(publish.stdout)
                sys.exit(f'Debug publish of {name} failed')
            verify = run(['dotnet', 'ilverify', str(output / f'{name}.dll'),
                          '-r', str(runtime / '*.dll'), '-r', str(output / '*.dll'),
                          '--system-module', 'System.Private.CoreLib', '--statistics'])
            methods = re.search(r'^Methods verified: (\d+)$', verify.stdout, re.MULTILINE)
            if verify.returncode != 0 or not methods:
                failed.append(name)
                print(f'FAIL {name}\n{verify.stdout}', flush=True)
            else:
                print(f'ok   {name}: {methods.group(1)} methods verified', flush=True)
    print(f'Finished in {time.monotonic() - started:.0f}s')
    if failed:
        sys.exit(f'Invalid Debug IL in: {", ".join(failed)}')


if __name__ == '__main__':
    main()
