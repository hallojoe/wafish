#!/usr/bin/env python3
"""Measure changed production executable lines against one or more dotnet-coverage XML reports."""
import json, pathlib, re, subprocess, sys, xml.etree.ElementTree as ET
root = pathlib.Path.cwd()
base = '0be18f0'
changed = {}
path = None
for line in subprocess.check_output(['git', 'diff', '--src-prefix=a/', '--dst-prefix=b/', '--unified=0', base, '--', 'src'], text=True).splitlines():
    if line.startswith('+++ b/'):
        path = line[6:]
    elif path and path.endswith('.cs') and (match := re.match(r'@@ .* \+(\d+)(?:,(\d+))? @@', line)):
        start = int(match[1]); length = int(match[2]) if match[2] is not None else 1
        changed.setdefault(str(root / path), set()).update(range(start, start + length))
measured, covered = {}, {}
for report in sys.argv[1:]:
    for module in ET.parse(report).getroot().findall('.//module'):
        sources = {s.get('id'): s.get('path') for s in module.findall('.//source_file')}
        for item in module.findall('.//range'):
            source = sources.get(item.get('source_id'))
            if source not in changed: continue
            lines = set(range(int(item.get('start_line')), int(item.get('end_line')) + 1)) & changed[source]
            measured.setdefault(source, set()).update(lines)
            if item.get('covered') == 'yes': covered.setdefault(source, set()).update(lines)
files = []
for source, lines in sorted(measured.items()):
    hits = lines & covered.get(source, set())
    files.append(dict(file=str(pathlib.Path(source).relative_to(root)), covered=len(hits), executable=len(lines), uncovered=sorted(lines-hits)))
result = dict(base=base, covered=sum(f['covered'] for f in files), executable=sum(f['executable'] for f in files), files=files)
result['percent'] = round(100 * result['covered'] / max(1, result['executable']), 2)
print(json.dumps(result, indent=2))
if result['percent'] < 80: sys.exit(1)
