#!/usr/bin/env python3
"""Select verified packages for publication. This script never contacts a registry."""
import argparse
from graphlib import TopologicalSorter
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import xml.etree.ElementTree as ET
import zipfile


def package_projects(root):
    projects = {}
    for path in sorted((root / 'src').glob('*/*.csproj')):
        tree = ET.parse(path)
        if tree.findtext('.//IsPackable', '').lower() == 'false':
            continue
        package_id = tree.findtext('.//PackageId', path.stem)
        if not re.fullmatch(r'[A-Za-z0-9_.-]+', package_id) or package_id in projects:
            raise ValueError(f'Invalid or duplicate package ID: {package_id}')
        projects[package_id] = path
    if not projects:
        raise ValueError('No package projects found')
    return projects


def check_inputs(root, package, version):
    configured = ET.parse(root / 'src/Directory.Build.props').findtext('.//Version')
    if not re.fullmatch(r'\d+\.\d+\.\d+(?:-[A-Za-z0-9]+(?:\.[A-Za-z0-9]+)*)?', version):
        raise ValueError('Use a three-part NuGet version, with an optional prerelease suffix')
    if version != configured:
        raise ValueError(f'Requested version {version} differs from source version {configured}')
    if os.environ.get('GITHUB_REF_TYPE') == 'tag' and os.environ.get('GITHUB_REF_NAME') != f'v{version}':
        raise ValueError(f'The selected tag must be v{version}')
    projects = package_projects(root)
    if package != 'all' and package not in projects:
        raise ValueError(f'Unknown package: {package}')
    return projects


def package_order(projects):
    by_path = {path.resolve(): package_id for package_id, path in projects.items()}
    graph = {}
    for package_id, path in projects.items():
        references = ET.parse(path).findall('.//ProjectReference')
        paths = [(path.parent / item.attrib['Include'].replace('\\', '/')).resolve() for item in references]
        graph[package_id] = [by_path[item] for item in paths if item in by_path]
    return list(TopologicalSorter(graph).static_order())


def check_archive(path, package_id, version):
    with zipfile.ZipFile(path) as archive:
        specs = [name for name in archive.namelist() if name.endswith('.nuspec')]
        if len(specs) != 1:
            raise ValueError(f'Expected one nuspec in {path.name}')
        spec = ET.fromstring(archive.read(specs[0]))
        actual_id = spec.findtext('.//{*}metadata/{*}id')
        actual_version = spec.findtext('.//{*}metadata/{*}version')
        if (actual_id, actual_version) != (package_id, version):
            raise ValueError(f'Package metadata mismatch: {path.name}')


def prepare(root, package, version):
    projects = check_inputs(root, package, version)
    selected = [item for item in package_order(projects) if package == 'all' or item == package]
    source = root / 'artifacts/package-checks'
    artifacts = []
    for package_id in selected:
        for extension in ('nupkg', 'snupkg'):
            path = source / f'{package_id}.{version}.{extension}'
            if not path.is_file():
                raise ValueError(f'Missing verified artifact: {path.name}')
            check_archive(path, package_id, version)
            artifacts.append(path)
    output = root / 'artifacts/publish'
    if output.exists():
        shutil.rmtree(output)
    output.mkdir(parents=True)
    hashes = {}
    for path in artifacts:
        shutil.copyfile(path, output / path.name)
        hashes[path.name] = hashlib.sha256(path.read_bytes()).hexdigest()
    manifest = {
        'version': version,
        'commit': os.environ.get('GITHUB_SHA', ''),
        'ref': os.environ.get('GITHUB_REF', 'local'),
        'packages': selected,
        'sha256': hashes,
    }
    (output / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    (output / 'packages.txt').write_text(''.join(f'{item}.{version}.nupkg\n' for item in selected))
    print(f'Staged {len(selected)} package(s), version {version}, in {output}')
    return manifest


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--package', required=True)
    parser.add_argument('--version', required=True)
    parser.add_argument('--check-only', action='store_true')
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    try:
        if args.check_only:
            check_inputs(root, args.package, args.version)
        else:
            prepare(root, args.package, args.version)
    except (ValueError, OSError, zipfile.BadZipFile, ET.ParseError) as error:
        parser.exit(1, f'Publication preparation failed: {error}\n')
