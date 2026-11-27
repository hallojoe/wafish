#!/usr/bin/env python3
"""Exercise the actual frontend MSBuild targets in an isolated, dependency-free fixture."""
from pathlib import Path
import json
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET

source = Path('src/Wafish.ReactUi')
with tempfile.TemporaryDirectory(prefix='wafish-build-inputs-') as temporary:
    root = Path(temporary) / "src" / "Probe"
    root.mkdir(parents=True)
    (Path(temporary) / "brand").mkdir()
    shutil.copyfile("brand/logo.png", Path(temporary) / "brand" / "logo.png")
    project = ET.parse(source / 'Wafish.ReactUi.csproj')
    for group in project.getroot().findall('ItemGroup'):
        for item in group.findall('ProjectReference'):
            group.remove(item)
    project.write(root / 'Probe.csproj', encoding='unicode')
    client = root / 'ClientApp'
    (client / 'scripts').mkdir(parents=True)
    (client / 'src').mkdir()
    shutil.copyfile(source / 'ClientApp/scripts/verify-assets.mjs', client / 'scripts/verify-assets.mjs')
    (client / 'index.html').write_text('<!--APP_CONFIG-->')
    (client / 'vite.config.ts').write_text('export {};')
    package = {'name': 'frontend-build-probe', 'version': '1.0.0', 'scripts': {'build': 'node scripts/build.mjs'}}
    (client / 'package.json').write_text(json.dumps(package))
    (client / 'package-lock.json').write_text(json.dumps({'name': package['name'], 'version': '1.0.0', 'lockfileVersion': 3, 'packages': {'': {'name': package['name'], 'version': '1.0.0'}}}))
    (client / 'scripts/build.mjs').write_text('''import {mkdirSync,writeFileSync,appendFileSync} from 'node:fs';
mkdirSync('../wwwroot/assets',{recursive:true});mkdirSync('../wwwroot/.vite',{recursive:true});
writeFileSync('../wwwroot/assets/app.js','fixture');
writeFileSync('../wwwroot/index.html','<!--APP_CONFIG--><script src="./assets/app.js"></script>');
writeFileSync('../wwwroot/.vite/manifest.json',JSON.stringify({'index.html':{isEntry:true,file:'assets/app.js'}}));
appendFileSync('../build-runs.txt','build\\n');
''')
    def command(*args, success=True):
        result = subprocess.run(args, cwd=root, text=True, capture_output=True, timeout=60)
        if (result.returncode == 0) != success:
            raise AssertionError(result.stdout + result.stderr)
        return result.stdout + result.stderr
    def builds():
        return len((root / 'build-runs.txt').read_text().splitlines())
    command('dotnet', 'build', 'Probe.csproj', '-c', 'Release')
    assert builds() == 1
    command('dotnet', 'build', 'Probe.csproj', '-c', 'Release', '--no-restore')
    assert builds() == 1, 'Unchanged frontend rebuilt'
    print('PASS: no-change build preserves generated output')
    package['scripts']['build'] += ' --changed-script'
    (client / 'package.json').write_text(json.dumps(package))
    command('dotnet', 'build', 'Probe.csproj', '-c', 'Release', '--no-restore')
    assert builds() == 2, 'package.json did not invalidate frontend'
    print('PASS: package build-script changes rebuild assets')
    (client / 'src/extra.ts').write_text('export {};')
    command('dotnet', 'build', 'Probe.csproj', '-c', 'Release', '--no-restore')
    assert builds() == 3
    (client / 'src/extra.ts').unlink()
    command('dotnet', 'build', 'Probe.csproj', '-c', 'Release', '--no-restore')
    assert builds() == 4
    print('PASS: added and removed source inputs rebuild assets')
    command('dotnet', 'pack', 'Probe.csproj', '-c', 'Release', '--no-build', '-o', 'packages')
    print('PASS: complete embedded assets can be packed without rebuilding')
    (root / 'wwwroot/assets/app.js').unlink()
    failure = command('dotnet', 'pack', 'Probe.csproj', '-c', 'Release', '--no-build', '-o', 'packages', success=False)
    assert 'Frontend assets are incomplete' in failure
    print('PASS: packing rejects a missing referenced asset with an explicit error')
