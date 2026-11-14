#!/usr/bin/env python3
"""Measure the recorded review 8–10 source-line changes against dotnet-coverage XML."""
import json
import pathlib
import sys
import xml.etree.ElementTree as ET

root = pathlib.Path.cwd()
changes = json.loads(pathlib.Path(__file__).with_name('changed-lines.json').read_text())
changed = {str(root / path): set(lines) for path, lines in changes.items()}
covered, measured = {}, {}
for module in ET.parse(sys.argv[1]).getroot().findall('.//module'):
    sources = {s.get('id'): s.get('path') for s in module.findall('.//source_file')}
    for item in module.findall('.//range'):
        path = sources.get(item.get('source_id'))
        if path not in changed:
            continue
        lines = set(range(int(item.get('start_line')), int(item.get('end_line')) + 1)) & changed[path]
        measured.setdefault(path, set()).update(lines)
        if item.get('covered') == 'yes':
            covered.setdefault(path, set()).update(lines)
num = den = 0
for path, lines in sorted(measured.items()):
    hits = lines & covered.get(path, set())
    num += len(hits)
    den += len(lines)
    print(f'{len(hits)}/{len(lines)} {pathlib.Path(path).relative_to(root)} missing {sorted(lines-hits)}')
print(f'TOTAL {num}/{den} = {100*num/den:.2f}%')
if den == 0 or num/den < .8:
    sys.exit(1)
