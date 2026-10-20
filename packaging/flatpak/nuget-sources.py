#!/usr/bin/env python3
"""Generate Flatpak downloads from the package hashes in the locked restore graph."""
import argparse
import base64
import json
import hashlib
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUTPUT = Path(__file__).with_name('nuget-sources.json')


def sources(root=ROOT):
    packages = {}
    for project in ('OpenXLR.Core', 'OpenXLR.Daemon', 'OpenXLR.UI', 'OpenXLR.Tui'):
        graph = json.loads((root / 'src' / project / 'packages.lock.json').read_text())
        for dependencies in graph['dependencies'].values():
            for name, package in dependencies.items():
                if package['type'] == 'Project':
                    continue
                name, version = name.lower(), package['resolved'].lower()
                digest = base64.b64decode(package['contentHash'], validate=True).hex()
                if len(digest) != 128:
                    raise ValueError(f'Invalid SHA-512 for {name}')
                key = (name, version)
                if key in packages and packages[key] != digest:
                    raise ValueError(f'Conflicting package hash: {name} {version}')
                packages[key] = digest
    runtime = Path(__file__).with_name('runtime-version').read_text().strip()
    for name in ('microsoft.netcore.app.runtime.linux-x64', 'microsoft.aspnetcore.app.runtime.linux-x64'):
        packages[(name, runtime)] = 'SDK runtime pack ' + runtime
    return [{'type': 'file', 'url': f'https://api.nuget.org/v3-flatcontainer/{name}/{version}/{name}.{version}.nupkg',
             '//': digest, 'dest': 'nuget-sources', 'dest-filename': f'{name}.{version}.nupkg'}
            for (name, version), digest in sorted(packages.items())]


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    expected = sources()
    if args.check:
        actual = json.loads(OUTPUT.read_text())
        # NuGet's lock hash describes package content; the download checksum
        # also covers the ZIP and repository signature. They are not equal.
        if [{k: v for k, v in item.items() if k != 'sha512'} for item in actual] != expected:
            raise SystemExit('Flatpak NuGet sources are stale; run packaging/flatpak/nuget-sources.py')
        if any(len(item.get('sha512', '')) != 128 or any(c not in '0123456789abcdef' for c in item['sha512']) for item in actual):
            raise SystemExit('Invalid Flatpak download checksum')
    else:
        def download(item):
            with urllib.request.urlopen(item['url'], timeout=60) as response:
                digest = hashlib.sha512()
                for block in iter(lambda: response.read(1024 * 1024), b''):
                    digest.update(block)
            return {**item, 'sha512': digest.hexdigest()}
        with ThreadPoolExecutor(max_workers=4) as pool:
            OUTPUT.write_text(json.dumps(list(pool.map(download, expected)), indent=2) + '\n')
