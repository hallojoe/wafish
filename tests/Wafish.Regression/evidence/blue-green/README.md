# R50: blue-green deployment verification

Verified on Linux with .NET SDK 10.0.403 on 2026-11-26.

The Release solution build passed before this script and documentation change.
The real HTTP walkthrough passed against an isolated SampleGateway and two loopback backends.
See [results](results.json) and [script coverage](coverage.json): 96.27% (129/134 executable lines).

```bash
dotnet build samples/SampleGateway/SampleGateway.csproj -c Release
python3 scripts/check-blue-green.py
```

The same route ID forwarded blue, then green, then blue again.
An unhealthy green backend was rejected before mutation.
A stale update returned HTTP 409 after cutover.
Configuration export confirmed route identity after every successful stage.
All hosts and temporary storage were removed.

Compatibility: no package code, public API, or default sample configuration changed.
The example uses existing preview, apply, and export endpoints.
This verifies configuration rollback; application data migration and draining remain deployment concerns.
