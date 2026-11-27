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
from package_version import check_archive, validate_version, version_from_tag


def release_version():
    event_path = os.environ.get('GITHUB_EVENT_PATH')
    if not event_path:
        raise ValueError('Release event payload is required')
    event = json.loads(Path(event_path).read_text())
    release = event.get('release') if isinstance(event, dict) else None
    if not isinstance(release, dict) or event.get('action') != 'published' or release.get('draft') is not False:
        raise ValueError('Publication requires a published, non-draft release')
    tag = release.get('tag_name')
    version = version_from_tag(tag)
    prerelease = validate_version(version)
    if type(release.get('prerelease')) is not bool:
        raise ValueError('Release prerelease status must be a boolean')
    if prerelease != release['prerelease']:
        if prerelease:
            raise ValueError('Tags with a suffix must be marked as a GitHub prerelease')
        raise ValueError('A GitHub prerelease requires a tag suffix; clean tags publish stable NuGet versions')
    return version


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
    validate_version(version)
    if os.environ.get('GITHUB_EVENT_NAME') == 'release' and release_version() != version:
        raise ValueError('Package version must match the release tag')
    if os.environ.get('GITHUB_REF_TYPE') == 'tag' and version_from_tag(os.environ.get('GITHUB_REF_NAME')) != version:
        raise ValueError(f'The selected tag must be {version} or v{version}')
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
    parser.add_argument('--version', default='')
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--check-only', action='store_true')
    mode.add_argument('--resolve-version', action='store_true', help='Validate release or manual inputs and write the version to GITHUB_OUTPUT')
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    try:
        if args.resolve_version:
            version = release_version() if os.environ.get('GITHUB_EVENT_NAME') == 'release' else args.version
            check_inputs(root, args.package, version)
            output = os.environ.get('GITHUB_OUTPUT')
            if not output:
                raise ValueError('GITHUB_OUTPUT is required to resolve the workflow version')
            with Path(output).open('a') as stream:
                stream.write(f'version={version}\n')
        elif args.check_only:
            check_inputs(root, args.package, args.version)
        else:
            prepare(root, args.package, args.version)
    except (ValueError, OSError, zipfile.BadZipFile, ET.ParseError) as error:
        parser.exit(1, f'Publication preparation failed: {error}\n')
