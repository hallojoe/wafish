"""Shared version and artifact validation for package verification and publication."""
import re
import xml.etree.ElementTree as ET
import zipfile


def validate_version(version):
    number = r'(?:0|[1-9][0-9]*)'
    match = re.fullmatch(rf'{number}\.{number}\.{number}(?:-([A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*))?', version)
    if not match:
        raise ValueError('Use a three-part NuGet version, with an optional prerelease suffix; build metadata is not allowed')
    suffix = match[1]
    if suffix and any(part.isdigit() and len(part) > 1 and part.startswith('0') for part in suffix.split('.')):
        raise ValueError('Numeric prerelease identifiers must not contain leading zeros')
    return suffix is not None


def version_from_tag(tag):
    if not isinstance(tag, str):
        raise ValueError('Release tag must be a version, optionally prefixed with v')
    version = tag.removeprefix('v')
    validate_version(version)
    return version


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
        for dependency in spec.findall('.//{*}dependency'):
            if dependency.get('id', '').startswith('Wafish') and dependency.get('version') != version:
                raise ValueError(f'Package dependency version mismatch: {path.name}, {dependency.get("id")}')
