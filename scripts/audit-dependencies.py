#!/usr/bin/env python3
"""Fail closed on audit warnings, report errors, or any reported vulnerable dependency."""
from pathlib import Path
import json
import subprocess
import sys


def clean_report(text):
    try:
        data = json.loads(text)
        if not isinstance(data, dict) or not data.get('projects'):
            return False
        projects = data['projects']
        if not isinstance(projects, list):
            return False
        for project in projects:
            if not isinstance(project, dict) or not isinstance(project.get('path'), str):
                return False
            frameworks = project.get('frameworks')
            if not isinstance(frameworks, list) or not frameworks:
                return False
            if any(not isinstance(f, dict) or not isinstance(f.get('framework'), str) for f in frameworks):
                return False
        def unsafe(value):
            if isinstance(value, dict):
                if value.get('vulnerabilities') or str(value.get('level', '')).lower() in ('error', 'warning'):
                    return True
                return any(unsafe(v) for v in value.values())
            return isinstance(value, list) and any(unsafe(v) for v in value)
        return not unsafe(data)
    except (ValueError, TypeError):
        return False


def main():
    root = Path(__file__).resolve().parent.parent
    out = root / 'artifacts/dependency-audit'
    out.mkdir(parents=True, exist_ok=True)
    restore = subprocess.run(['dotnet', 'restore', 'Wafish.slnx', '--force', '-p:AuditPipeline=true'], cwd=root, text=True, capture_output=True)
    (out / 'restore.log').write_text(restore.stdout + restore.stderr)
    result = subprocess.run(['dotnet', 'package', 'list', '--project', 'Wafish.slnx', '--vulnerable', '--include-transitive', '--format', 'json', '--no-restore'], cwd=root, text=True, capture_output=True)
    (out / 'packages.json').write_text(result.stdout)
    (out / 'packages.stderr.log').write_text(result.stderr)
    passed = restore.returncode == 0 and result.returncode == 0 and clean_report(result.stdout)
    (out / 'summary.json').write_text(json.dumps({'schemaVersion': 1, 'passed': passed, 'restoreExitCode': restore.returncode, 'reportExitCode': result.returncode}, indent=2))
    print('Dependency audit ' + ('passed' if passed else 'FAILED') + '; reports: ' + str(out))
    return 0 if passed else 1


if __name__ == '__main__':
    sys.exit(main())
