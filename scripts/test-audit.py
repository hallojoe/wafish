import importlib.util
import json
from pathlib import Path

spec = importlib.util.spec_from_file_location('audit', Path(__file__).with_name('audit-dependencies.py'))
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)
clean = {'projects': [{'path': 'a.csproj', 'frameworks': [{'framework': 'net10.0'}]}]}
assert audit.clean_report(json.dumps(clean))
for value in ['', '{}', '[]', 'not json', '{"projects": []}', '{"projects": ["invalid"]}', '{"projects": [{}]}']:
    assert not audit.clean_report(value), value
for level in ['error', 'warning']:
    report = dict(clean, log=[{'level': level, 'message': 'source unavailable'}])
    assert not audit.clean_report(json.dumps(report))
for severity in ['Low', 'Moderate', 'High', 'Critical']:
    report = json.loads(json.dumps(clean))
    report['projects'][0]['frameworks'][0]['transitivePackages'] = [{'vulnerabilities': [{'severity': severity}]}]
    assert not audit.clean_report(json.dumps(report))
print('PASS: audit rejects missing, malformed, unavailable, and vulnerable reports at every severity')
