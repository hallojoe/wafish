#!/usr/bin/env python3
"""Test publication selection and push routing without contacting registries."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import zipfile

REPO = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location('publication', REPO / 'scripts/prepare-publication.py')
publication = importlib.util.module_from_spec(spec)
spec.loader.exec_module(publication)


class PublicationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='wafish-publication-test-')
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.env = patch.dict(os.environ, {'GITHUB_REF_TYPE': 'branch', 'GITHUB_REF_NAME': 'main'})
        self.env.start()
        self.addCleanup(self.env.stop)
        (self.root / 'src').mkdir()
        (self.root / 'src/Directory.Build.props').write_text('<Project><PropertyGroup><Version>1.0.0-rc.1</Version></PropertyGroup></Project>')
        # The dependent sorts first alphabetically. Publication must still put its dependency first.
        for name, reference in [('Wafish.A', 'Wafish.Z'), ('Wafish.Z', None)]:
            folder = self.root / 'src' / name
            folder.mkdir()
            refs = f'<ItemGroup><ProjectReference Include="../{reference}/{reference}.csproj" /></ItemGroup>' if reference else ''
            (folder / f'{name}.csproj').write_text(f'<Project>{refs}</Project>')
            for extension in ('nupkg', 'snupkg'):
                self.archive(name, extension)

    def archive(self, name, extension, version='1.0.0-rc.1'):
        path = self.root / 'artifacts/package-checks' / f'{name}.1.0.0-rc.1.{extension}'
        path.parent.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(path, 'w') as archive:
            archive.writestr(f'{name}.nuspec', f'<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><id>{name}</id><version>{version}</version></metadata></package>')
        return path

    def prepare(self, package='all'):
        return publication.prepare(self.root, package, '1.0.0-rc.1')

    def test_all_packages_publish_dependencies_first(self):
        manifest = self.prepare()
        self.assertEqual(['Wafish.Z', 'Wafish.A'], manifest['packages'])
        self.assertEqual(4, len(manifest['sha256']))
        self.assertTrue(all(len(value) == 64 for value in manifest['sha256'].values()))

    def test_single_selection_excludes_dependencies_and_stale_output(self):
        self.prepare()
        self.prepare('Wafish.A')
        output = self.root / 'artifacts/publish'
        self.assertEqual(['Wafish.A.1.0.0-rc.1.nupkg'], (output / 'packages.txt').read_text().splitlines())
        self.assertFalse(list(output.glob('Wafish.Z*')))

    def test_version_mismatch_rejected(self):
        with self.assertRaisesRegex(ValueError, 'differs from source'):
            publication.check_inputs(self.root, 'all', '1.0.0')

    def test_unknown_selection_rejected(self):
        with self.assertRaisesRegex(ValueError, 'Unknown package'):
            self.prepare('../Wafish.A')

    def test_mismatched_release_tag_rejected(self):
        with patch.dict(os.environ, {'GITHUB_REF_TYPE': 'tag', 'GITHUB_REF_NAME': 'v1.0.0'}):
            with self.assertRaisesRegex(ValueError, 'selected tag'):
                self.prepare()

    def test_matching_release_tag_accepted(self):
        with patch.dict(os.environ, {'GITHUB_REF_TYPE': 'tag', 'GITHUB_REF_NAME': 'v1.0.0-rc.1'}):
            self.prepare()

    def test_missing_symbols_rejected_before_staging(self):
        (self.root / 'artifacts/package-checks/Wafish.A.1.0.0-rc.1.snupkg').unlink()
        with self.assertRaisesRegex(ValueError, 'Missing verified artifact'):
            self.prepare()
        self.assertFalse((self.root / 'artifacts/publish').exists())

    def test_wrong_metadata_rejected(self):
        self.archive('Wafish.A', 'nupkg', version='9.0.0')
        with self.assertRaisesRegex(ValueError, 'metadata mismatch'):
            self.prepare()

    def push(self, registry, fail=False):
        self.prepare('Wafish.A')
        binary = self.root / 'bin'
        binary.mkdir(exist_ok=True)
        stub = binary / 'dotnet'
        stub.write_text('#!/usr/bin/env python3\nimport json,os,sys\nwith open(os.environ["CALLS"], "a") as f: f.write(json.dumps(sys.argv[1:])+"\\n")\nsys.exit(int(os.environ.get("FAIL_PUSH", "0")))\n')
        stub.chmod(0o755)
        calls = self.root / 'calls.jsonl'
        env = dict(os.environ, PATH=str(binary)+os.pathsep+os.environ['PATH'], CALLS=str(calls),
                   PUBLISH_API_KEY='test-token', GITHUB_REPOSITORY_OWNER='Example', FAIL_PUSH='1' if fail else '0')
        result = subprocess.run(['bash', str(REPO / 'scripts/push-packages.sh'), registry], cwd=self.root, env=env, capture_output=True, text=True)
        self.assertNotIn('test-token', result.stdout + result.stderr)
        return result, [json.loads(line) for line in calls.read_text().splitlines()] if calls.exists() else []

    def test_nuget_pushes_selected_package_and_symbols(self):
        result, calls = self.push('nuget')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(2, len(calls))
        self.assertTrue(calls[0][2].endswith('.nupkg'))
        self.assertTrue(calls[1][2].endswith('.snupkg'))
        for call in calls:
            self.assertIn('https://api.nuget.org/v3/index.json', call)
            self.assertIn('--skip-duplicate', call)

    def test_github_pushes_only_selected_nupkg(self):
        result, calls = self.push('github')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(1, len(calls))
        self.assertIn('https://nuget.pkg.github.com/example/index.json', calls[0])
        self.assertIn('--no-symbols', calls[0])

    def test_failed_push_stops_before_symbols(self):
        result, calls = self.push('nuget', fail=True)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(1, len(calls))

    def test_unknown_registry_never_pushes(self):
        result, calls = self.push('wrong')
        self.assertNotEqual(0, result.returncode)
        self.assertEqual([], calls)

    def test_every_package_has_a_workflow(self):
        import re
        workflows = list((REPO / '.github/workflows').glob('publish-*.yml'))
        selections = []
        for workflow in workflows:
            match = re.search(r'^          package: (.+)$', workflow.read_text(), re.M)
            self.assertIsNotNone(match, workflow.name)
            selections.append(match[1])
        self.assertCountEqual(['all', *publication.package_projects(REPO)], selections)


if __name__ == '__main__':
    unittest.main()
