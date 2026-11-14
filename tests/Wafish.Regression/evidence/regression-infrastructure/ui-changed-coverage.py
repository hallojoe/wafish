#!/usr/bin/env python3
"""Check changed executable frontend lines; whitespace-only changes are excluded."""
import json
from pathlib import Path
changes=json.loads(Path(__file__).with_name('ui-changed-lines.json').read_text())
sections=Path('src/Wafish.ReactUi/ClientApp/coverage/lcov.info').read_text().split('end_of_record')
covered=total=0
for path, lines in changes.items():
    relative=path.split('/ClientApp/')[1]
    section=next(block for block in sections if 'SF:'+relative+'\n' in block)
    hits={int(line[3:].split(',')[0]):int(line.split(',')[1]) for line in section.splitlines() if line.startswith('DA:')}
    measured=set(lines)&hits.keys()
    passed={line for line in measured if hits[line]>0}
    covered+=len(passed);total+=len(measured)
    print(f'{len(passed)}/{len(measured)} {relative}; missing {sorted(measured-passed)}')
assert total and covered/total>=.8
print(f'TOTAL {covered}/{total} = {covered/total:.2%}')
