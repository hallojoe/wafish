#!/usr/bin/env bash
set -euo pipefail
export WAFISH_VERIFICATION_DIRECTORY="${WAFISH_VERIFICATION_DIRECTORY:-$(mktemp -d)}"
export WAFISH_TEST_REPORT="$WAFISH_VERIFICATION_DIRECTORY/main.json"
dotnet run --project tests/Wafish.Regression -c Release --no-build > "$WAFISH_VERIFICATION_DIRECTORY/main.log" 2>&1
python3 - <<'PY'
from pathlib import Path
import socket,subprocess,json,os
root=Path(os.environ['WAFISH_VERIFICATION_DIRECTORY'])
for name,extra in [('first',['--suite=first','--soak-duration=1']), ('combined',['--transport=https2','--suite=plugins','--plugin-sets=none;response-cache+compression;waf+rate-limit+traffic-metrics+request-retry','--workload=events','--stream-chunks=3','--stream-bytes=128','--stream-interval=50','--arrival-rate=20','--arrival-queue=8'])]:
    sockets=[socket.socket() for _ in range(3)]
    for s in sockets:s.bind(('127.0.0.1',0))
    ports=[s.getsockname()[1] for s in sockets]
    for s in sockets:s.close()
    scheme='https' if name=='combined' else 'http'
    args=['dotnet','run','--project','tests/Wafish.Performance/Runner','-c','Release','--no-build','--',*extra,'--duration=1','--warmup=0','--concurrency=2','--repetitions=1',f'--results-directory={root/name}']
    args += [f'--{role}-url={scheme}://127.0.0.1:{port}' for role,port in zip(['backend','native','enhanced'],ports)]
    with (root/f'{name}.log').open('w') as log:subprocess.run(args,check=True,stdout=log,stderr=subprocess.STDOUT)
    report=json.loads(next((root/name).glob('*.json')).read_text())
    if name=='combined':
        assert len(report['results'])==4
        for row in report['results']:
            assert row['failures']==0 and row['completedResponses']>0 and row['firstByteSamples']>0
            assert row['arrivals']['offered']==20
            assert row['arrivals']['offered']==sum(row['arrivals'][k] for k in ['started','dropped','cancelledBeforeStart'])
    else:assert all(row['passed'] for row in report['results'])
PY
